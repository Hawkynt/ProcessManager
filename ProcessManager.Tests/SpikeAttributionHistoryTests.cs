using System.Diagnostics;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class SpikeAttributionHistoryTests {

  private static ProcessRecord Process(
    int pid,
    string name,
    ulong cpuNanoseconds,
    ulong ioBytes,
    ulong privateBytes
  ) => new() {
    Key = new(pid, pid * 1000L),
    Name = name,
    UserName = "tester",
    CpuTimeNs = Counter.Of(cpuNanoseconds),
    ReadBytes = Counter.Of(ioBytes),
    WriteBytes = Counter.Of(0),
    OtherBytes = Counter.Of(0),
    PrivateBytes = Counter.Of(privateBytes),
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
  public void TheLargestContributorsAreKeptInDescendingOrder() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(
      start,
      Process(10, "small", 0, 0, 1000),
      Process(20, "large", 0, 0, 1000),
      Process(30, "middle", 0, 0, 1000)
    );
    var after = Snapshot(
      start + Stopwatch.Frequency,
      Process(10, "small", 100_000_000, 1000, 1100),
      Process(20, "large", 700_000_000, 7000, 1700),
      Process(30, "middle", 400_000_000, 4000, 1400)
    );
    var history = new SpikeAttributionHistory(8, contributors: 2);

    history.Add(after, Delta(before, after), 12345);

    Assert.Multiple(() => {
      Assert.That(history.AtAge(SpikeMetric.Cpu, 0).ToArray().Select(item => item.Name), Is.EqualTo(new[] { "large", "middle" }));
      Assert.That(history.AtAge(SpikeMetric.Io, 0).ToArray().Select(item => item.Name), Is.EqualTo(new[] { "large", "middle" }));
      Assert.That(history.AtAge(SpikeMetric.MemoryGrowth, 0).ToArray().Select(item => item.Name), Is.EqualTo(new[] { "large", "middle" }));
      Assert.That(history.UtcTicksAtAge(0), Is.EqualTo(12345));
    });
  }

  [Test]
  public void MemoryAttributionMeansGrowthNotLargestResidentProcess() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(
      start,
      Process(10, "huge-but-shrinking", 0, 0, 10_000),
      Process(20, "allocator", 0, 0, 100)
    );
    var after = Snapshot(
      start + Stopwatch.Frequency,
      Process(10, "huge-but-shrinking", 0, 0, 9_000),
      Process(20, "allocator", 0, 0, 600)
    );
    var history = new SpikeAttributionHistory(4);

    history.Add(after, Delta(before, after), 1);

    var memory = history.AtAge(SpikeMetric.MemoryGrowth, 0);
    Assert.Multiple(() => {
      Assert.That(memory, Has.Length.EqualTo(1));
      Assert.That(memory[0].Name, Is.EqualTo("allocator"));
      Assert.That(memory[0].Value.Value, Is.EqualTo(500).Within(0.001));
    });
  }

  [Test]
  public void HistoricalIdentitySurvivesAfterTheProcessIsGone() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(start, Process(42, "brief-worker", 0, 0, 0));
    var busy = Snapshot(start + Stopwatch.Frequency, Process(42, "brief-worker", 900_000_000, 0, 0));
    var empty = Snapshot(start + (2 * Stopwatch.Frequency));
    var history = new SpikeAttributionHistory(4);

    history.Add(busy, Delta(before, busy), 100);
    history.Add(empty, Delta(busy, empty), 200);

    var old = history.AtAge(SpikeMetric.Cpu, 1);
    Assert.Multiple(() => {
      Assert.That(old, Has.Length.EqualTo(1));
      Assert.That(old[0].Key, Is.EqualTo(new ProcessKey(42, 42_000)));
      Assert.That(old[0].Name, Is.EqualTo("brief-worker"));
      Assert.That(history.AtAge(SpikeMetric.Cpu, 0), Is.Empty);
    });
  }

  [Test]
  public void TheRingDropsOnlyTheOldestAttribution() {
    var start = Stopwatch.GetTimestamp();
    var history = new SpikeAttributionHistory(2, contributors: 1);
    var before = Snapshot(start, Process(1, "worker", 0, 0, 0));

    for (var sample = 1; sample <= 3; ++sample) {
      var after = Snapshot(
        start + (sample * Stopwatch.Frequency),
        Process(1, "worker", (ulong)sample * 100_000_000ul, 0, 0)
      );
      history.Add(after, Delta(before, after), sample * 100L);
      before = after;
    }

    Assert.Multiple(() => {
      Assert.That(history.Count, Is.EqualTo(2));
      Assert.That(history.UtcTicksAtAge(0), Is.EqualTo(300));
      Assert.That(history.UtcTicksAtAge(1), Is.EqualTo(200));
      Assert.That(history.UtcTicksAtAge(2), Is.Zero);
    });
  }

  [Test]
  public void TombstoneRowsCannotReappearAsCurrentContributors() {
    var start = Stopwatch.GetTimestamp();
    var before = Snapshot(start, Process(9, "gone", 0, 0, 0));
    var gone = Process(9, "gone", 900_000_000, 9000, 9000);
    gone.ExitedUtcTicks = DateTime.UtcNow.Ticks;
    var after = Snapshot(start + Stopwatch.Frequency, gone);
    var history = new SpikeAttributionHistory(4);

    history.Add(after, Delta(before, after), 1);

    Assert.Multiple(() => {
      Assert.That(history.AtAge(SpikeMetric.Cpu, 0), Is.Empty);
      Assert.That(history.AtAge(SpikeMetric.Io, 0), Is.Empty);
      Assert.That(history.AtAge(SpikeMetric.MemoryGrowth, 0), Is.Empty);
    });
  }

}
