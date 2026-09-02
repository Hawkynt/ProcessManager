using System.Diagnostics;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class ProcessActivityGraphsTests {

  private static ProcessRecord Process(int pid, ulong ioBytes, ulong privateBytes, bool exited = false) => new() {
    Key = new(pid, (ulong)pid * 1000ul),
    Name = $"process-{pid}",
    CpuTimeNs = Counter.Of(0),
    ReadBytes = Counter.Of(ioBytes),
    WriteBytes = Counter.Of(0),
    OtherBytes = Counter.Of(0),
    PrivateBytes = Counter.Of(privateBytes),
    ExitedUtcTicks = exited ? 1 : 0,
  };

  private static SystemSnapshot Snapshot(long timestamp, params ProcessRecord[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = timestamp };
    snapshot.System.CoreCount = 1;
    var rows = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i)
      rows[i] = processes[i];

    return snapshot;
  }

  private static SnapshotDelta Delta(SystemSnapshot before, SystemSnapshot after) {
    var delta = new SnapshotDelta();
    delta.Update(before, after, CpuPercentMode.PerCore);
    return delta;
  }

  [Test]
  public void TheThreeGraphsMatchTheMetricsWhoseProcessesAreRetained() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(
      start,
      Process(10, 0, 10_000),
      Process(20, 0, 100)
    );
    var after = Snapshot(
      start + Stopwatch.Frequency,
      Process(10, 7_000, 9_000),
      Process(20, 4_000, 600)
    );

    var graphs = ProcessActivityGraphs.Build(after, Delta(before, after));

    Assert.Multiple(() => {
      Assert.That(graphs.Select(graph => graph.Label), Is.EqualTo(new[] {
        ProcessActivityGraphs.Processor,
        ProcessActivityGraphs.Io,
        ProcessActivityGraphs.MemoryGrowth,
      }));
      Assert.That(graphs[0].Unit, Is.EqualTo(PerformanceUnit.Percent));
      Assert.That(graphs[0].Maximum, Is.EqualTo(100));
      Assert.That(graphs[1].Unit, Is.EqualTo(PerformanceUnit.BytesPerSecond));
      Assert.That(graphs[1].Value.Value, Is.EqualTo(11_000).Within(0.001));
      Assert.That(graphs[2].Unit, Is.EqualTo(PerformanceUnit.BytesPerSecond));
      Assert.That(graphs[2].Value.Value, Is.EqualTo(500).Within(0.001), "shrinking private memory did not cause a growth spike");
    });
  }

  [Test]
  public void ExitedRowsDoNotBecomeCurrentActivity() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(start, Process(10, 0, 100));
    var after = Snapshot(start + Stopwatch.Frequency, Process(10, 10_000, 10_000, exited: true));

    var graphs = ProcessActivityGraphs.Build(after, Delta(before, after));

    Assert.Multiple(() => {
      Assert.That(graphs.Single(graph => graph.Label == ProcessActivityGraphs.Io).Value.HasValue, Is.False);
      Assert.That(graphs.Single(graph => graph.Label == ProcessActivityGraphs.MemoryGrowth).Value.HasValue, Is.False);
    });
  }

  [TestCase(ProcessActivityGraphs.Processor, SpikeMetric.Cpu)]
  [TestCase(ProcessActivityGraphs.Io, SpikeMetric.Io)]
  [TestCase(ProcessActivityGraphs.MemoryGrowth, SpikeMetric.MemoryGrowth)]
  public void OnlyAttributableGraphLabelsMapToSpikeHistory(string label, SpikeMetric expected) {
    Assert.That(ProcessActivityGraphs.TryGetMetric(label, out var actual), Is.True);
    Assert.That(actual, Is.EqualTo(expected));
  }

  [TestCase("Active time")]
  [TestCase("Transfer rate")]
  [TestCase("Throughput")]
  [TestCase("Physical memory")]
  public void DeviceAndCapacityGraphsNeverClaimProcessAttribution(string label) {
    Assert.That(ProcessActivityGraphs.TryGetMetric(label, out _), Is.False);
  }

}
