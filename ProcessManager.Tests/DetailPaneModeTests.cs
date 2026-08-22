using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The lower pane's modes (PRD §10).
/// </summary>
/// <remarks>
/// §10 lists eleven of them and the pane had six. The four added here are pages rather than lists,
/// and they were the properties window's until they moved: §26 asks for one row of tabs and not two,
/// so a window that hosts this pane and also adds its own Security page puts two tabs of that name on
/// one strip — which is what the duplicate check below exists to stop happening again.
/// </remarks>
[TestFixture]
public sealed class DetailPaneModeTests {

  private sealed class StubProbe : ISystemProbe {

    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) { }
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    /// <summary>What the unit-file walk of §41 produced, or nothing on a machine with no systemd.</summary>
    public IReadOnlyList<ServiceRecord> Services { get; set; } = [];

    public IReadOnlyList<ServiceRecord> GetServices() => this.Services;

    /// <summary>
    /// What the on-demand pages get. Settable rather than init-only because one of the tests changes
    /// it between two processes: the point of that test is that the page reads again.
    /// </summary>
    public MemoryMapReading Map { get; set; } = MemoryMapReading.NotImplemented;

    public MemoryMapReading GetMemoryRegions(ProcessKey key) => this.Map;

    public void Dispose() { }

  }

  private readonly List<DetailPane> _panes = [];

  [TearDown]
  public void CloseThePanes() {
    foreach (var pane in this._panes)
      pane.Dispose();

    this._panes.Clear();
  }

  private DetailPane Pane(StubProbe probe) {
    var pane = new DetailPane(probe);
    this._panes.Add(pane);
    return pane;
  }

  private static List<string> Tabs(DetailPane pane) {
    var titles = new List<string>();
    foreach (var page in ((TabControl)pane.Control).TabPages)
      titles.Add(page.Text);

    return titles;
  }

  /// <summary>
  /// One process, enough of it for the pane to have something to describe.
  /// </summary>
  /// <param name="cgroup">
  /// What the unit look-up of §41 works from. Null is most of a desktop: a process under no unit.
  /// </param>
  private static (ProcessKey Key, SystemSnapshot Snapshot, ProcessRow Row) Machine(
    int pid = 4242,
    string? cgroup = null
  ) {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(pid, 100);
    records[0].Name = "editor";
    records[0].UserName = "alice";
    records[0].ContainerPath = cgroup;
    records[0].HandleCount = Counter.NotSampledYet;

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var row = new ProcessRow(records[0].Key);
    row.Update(in snapshot.Processes[0], delta, 0, Counter.NotSupported, currentUserId: 1000);
    return (records[0].Key, snapshot, row);
  }

  /// <summary>Points a pane at a process and hands it the sample its own pages need.</summary>
  private static void Show(DetailPane pane, in SystemSnapshot snapshot, ProcessKey key, ProcessRow row, string tab) {
    pane.Select(key);
    pane.UpdateOverview(in snapshot.Processes[0], row);
    pane.ShowTab(tab);
  }

  /// <summary>
  /// Six modes were there; §10 asks for eleven. These four are the ones this build can fill.
  /// </summary>
  [Test]
  public void ThePaneCarriesTheModesSectionTenAsksFor() {
    var tabs = Tabs(this.Pane(new()));

    Assert.That(tabs, Is.SupersetOf(new[] {
      "Overview",
      "Threads",
      "Modules",
      "Handles",
      "Network",
      "Environment",
      "Memory map",
      "Windows",
      "Services",
      "Security",
    }));
  }

  /// <summary>
  /// And not Timeline, which needs the event history of §63 — nothing in this program records one.
  /// A tab named for a feature nobody wrote is worse than a missing one (PRD §7).
  /// </summary>
  [Test]
  public void ThereIsNoTimelineTabBecauseNothingRecordsEvents() =>
    Assert.That(Tabs(this.Pane(new())), Does.Not.Contain("Timeline"));

  /// <summary>
  /// Every tab exactly once. The properties window hosts this pane and adds its own pages to the same
  /// strip, so a page it forgot to stop adding would show up as a second tab of the same name — which
  /// no other test would catch and every screenshot would (PRD §26).
  /// </summary>
  [Test]
  public void EveryTabAppearsExactlyOnceInAPropertiesWindow() {
    var (key, _, row) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var title in window.TabTitles)
      seen[title] = seen.TryGetValue(title, out var count) ? count + 1 : 1;

    Assert.Multiple(() => {
      foreach (var (title, count) in seen)
        Assert.That(count, Is.EqualTo(1), $"'{title}' is on the strip {count} times");
    });
  }

  /// <summary>
  /// The pages the window used to own are still reachable through it, under the same names. They live
  /// on the pane now, which is an implementation change and must not be a visible one.
  /// </summary>
  [Test]
  public void ThePropertiesWindowStillHasEveryPageItAdvertised() {
    var (key, _, row) = Machine();
    var window = new ProcessPropertiesWindow(new StubProbe(), key, row.Name);

    Assert.That(window.TabTitles, Is.SupersetOf(new[] {
      "General", "Performance", "CPU", "Memory", "I/O", "GPU", "cgroup",
      "Memory map", "Security", "Services", "Windows",
    }));
  }

  /// <summary>
  /// Moving the pane to another process throws away what the on-demand pages read for the last one.
  /// In a properties window the key never changes; at the foot of the main window it changes with
  /// every arrow key, and a page that kept its "already filled" flag showed one process's mappings
  /// under another's name (PRD §72.2, §86).
  /// </summary>
  [Test]
  public void PointingThePaneAtAnotherProcessReadsTheMemoryMapAgain() {
    var probe = new StubProbe();
    var pane = this.Pane(probe);
    var first = Machine();
    Show(pane, in first.Snapshot, first.Key, first.Row, "Memory map");

    Assert.That(pane.MemoryMapHeading, Does.Contain("not implemented"));

    // A different answer for a different process. Without the re-read the heading would still be the
    // first one's, which is a sentence about a process nobody is looking at any more.
    probe.Map = new(MemoryMapState.NotPermitted, false, []);
    var second = Machine(pid: 5151);
    Show(pane, in second.Snapshot, second.Key, second.Row, "Memory map");

    Assert.That(pane.MemoryMapHeading, Does.Contain("attaching a debugger"));
  }

  /// <summary>
  /// A machine with no service manager is the one case the Services tab may be taken off the strip,
  /// and only under the preference that asks for that. Disabled is the default and the tab stays,
  /// saying which kind of nothing it is looking at (PRD §26, §41).
  /// </summary>
  [Test]
  public void TheServicesTabStaysUnderTheDefaultPreferenceAndExplainsItself() {
    var pane = this.Pane(new());
    var (key, snapshot, row) = Machine();
    Show(pane, in snapshot, key, row, "Services");

    Assert.That(Tabs(pane), Does.Contain("Services"));
    Assert.That(pane.ServicesText, Does.Contain("Nothing on this machine publishes services"));
  }

  [Test]
  public void TheServicesTabGoesWhenTheMachineHasNoManagerAndThePreferenceSaysHide() {
    var pane = this.Pane(new());
    pane.Unavailable = UnavailableTabs.Hidden;
    var (key, snapshot, row) = Machine();
    Show(pane, in snapshot, key, row, "Services");

    Assert.That(Tabs(pane), Does.Not.Contain("Services"));
  }

  /// <summary>
  /// A process in a unit is told which one, whether it is the process systemd watches, and the fields
  /// §41 gained: the type and the account.
  /// </summary>
  [Test]
  public void AProcessInAUnitIsToldWhichOneAndWhetherItIsTheMainProcess() {
    var probe = new StubProbe {
      Services = [
        new("thing.service", "A thing", ServiceState.Running, true, false, 4242, "/usr/bin/thing", "/lib/systemd/system/thing.service", "always") {
          Type = "notify",
          Account = "nobody",
        },
      ],
    };

    var pane = this.Pane(probe);
    var (key, snapshot, row) = Machine(cgroup: "/system.slice/thing.service");
    Show(pane, in snapshot, key, row, "Services");

    Assert.Multiple(() => {
      Assert.That(pane.ServicesText, Does.Contain("thing.service"));
      Assert.That(pane.ServicesText, Does.Contain("this process"));
      Assert.That(pane.ServicesText, Does.Contain("notify"));
      Assert.That(pane.ServicesText, Does.Contain("nobody"));
    });
  }

  /// <summary>
  /// A process under no unit keeps its tab and says so, naming the cgroup it looked in. That is a
  /// finding about the process — most of a desktop is like this — and not a gap (PRD §26).
  /// </summary>
  [Test]
  public void AProcessUnderNoUnitKeepsItsTabAndSaysWhy() {
    var probe = new StubProbe {
      Services = [new("other.service", null, ServiceState.Inactive, null, false, 0, null, "/lib/systemd/system/other.service", null)],
    };

    var pane = this.Pane(probe);
    pane.Unavailable = UnavailableTabs.Hidden;
    var (key, snapshot, row) = Machine(cgroup: "/user.slice/user-1000.slice");
    Show(pane, in snapshot, key, row, "Services");

    Assert.That(Tabs(pane), Does.Contain("Services"), "the machine has units; this process is in none");
    Assert.That(pane.ServicesText, Does.Contain("under no systemd unit"));
  }

  /// <summary>
  /// The security page is filled from the row the sample already carries, so it has something on it
  /// the first time somebody opens the tab rather than a second later.
  /// </summary>
  [Test]
  public void TheSecurityPageIsFilledFromTheSampleTheOverviewAlreadyHad() {
    var pane = this.Pane(new());
    var (key, snapshot, row) = Machine();
    Show(pane, in snapshot, key, row, "Security");

    Assert.That(pane.SecurityText, Does.Contain("alice"));
    // The namespace row is there either way. Every process on Linux is in a namespace of every kind,
    // so a page with none on it would be stating something that cannot be true (PRD §72.3).
    Assert.That(pane.SecurityText, Does.Contain("Namespace"));
  }

}
