using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

[TestFixture]
public sealed class PerformanceAttributionWindowTests {

  private sealed class StubProbe : ISystemProbe {

    private ulong _sample;

    public string Description => "activity attribution stub";

    public void Sample(SystemSnapshot snapshot) {
      var sample = ++this._sample;
      snapshot.System.CoreCount = 1;
      snapshot.System.Cpu = new() {
        UserNs = sample * 400_000_000ul,
        IdleNs = sample * 600_000_000ul,
      };
      snapshot.System.TotalMemoryBytes = Counter.Of(8ul * 1024 * 1024 * 1024);
      snapshot.System.AvailableMemoryBytes = Counter.Of(4ul * 1024 * 1024 * 1024);

      var cores = snapshot.PrepareCores(1);
      cores[0] = new() {
        UserNs = sample * 400_000_000ul,
        IdleNs = sample * 600_000_000ul,
      };

      var processes = snapshot.PrepareProcesses(2);
      processes[0] = new() {
        Key = new(10, 10_000ul),
        Name = "worker",
        UserName = "alice",
        CpuTimeNs = Counter.Of(sample * 300_000_000ul),
        ReadBytes = Counter.Of(sample * 8_192ul),
        WriteBytes = Counter.Of(0),
        OtherBytes = Counter.Of(0),
        PrivateBytes = Counter.Of(1_000_000ul + (sample * 4_096ul)),
      };
      processes[1] = new() {
        Key = new(20, 20_000ul),
        Name = "server",
        UserName = "bob",
        CpuTimeNs = Counter.Of(sample * 100_000_000ul),
        ReadBytes = Counter.Of(sample * 2_048ul),
        WriteBytes = Counter.Of(0),
        OtherBytes = Counter.Of(0),
        PrivateBytes = Counter.Of(2_000_000ul),
      };
    }

    public HostInfo DescribeHost() => new() { HostName = "stub", CpuModel = "Fixture CPU" };
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }

  }

  [Test]
  public void ActivityPageShowsExactlyTheThreeProcessAttributableHistories() {
    using var probe = new StubProbe();
    using var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();
    var window = new PerformanceWindow(probe, sampler, openOnBusiest: false, historyMultiplier: 1);

    Assert.That(window.Show("Activity"), Is.True);
    var plots = new List<HistoryPlot>();
    foreach (var control in window.Controls)
      if (control is HistoryPlot { Visible: true } plot)
        plots.Add(plot);

    Assert.Multiple(() => {
      Assert.That(plots.Select(plot => plot.Caption), Is.EquivalentTo(new[] {
        Query.ProcessActivityGraphs.Processor,
        Query.ProcessActivityGraphs.Io,
        Query.ProcessActivityGraphs.MemoryGrowth,
      }));
      Assert.That(plots, Has.Count.EqualTo(3));
      Assert.That(plots.All(plot => plot.Width > 200 && plot.Height > 40), Is.True);
      Assert.That(window.DescribeForCapture(), Does.Contain("3 visible"));
      Assert.That(window.CurrentValuesText(), Does.Contain("worker"), "the existing current top-process rows remain on the page");
    });
  }

  [Test]
  public void ActivityHistoriesKeepFollowingSamplerUpdates() {
    using var probe = new StubProbe();
    using var sampler = new Sampler(probe);
    sampler.Sample();
    sampler.Sample();
    var window = new PerformanceWindow(probe, sampler, openOnBusiest: false, historyMultiplier: 1);
    Assert.That(window.Show("Activity"), Is.True);

    sampler.Sample();
    window.UpdateFromSample();

    var plots = 0;
    foreach (var control in window.Controls)
      if (control is HistoryPlot { Visible: true })
        ++plots;

    Assert.That(plots, Is.EqualTo(3));
  }

}
