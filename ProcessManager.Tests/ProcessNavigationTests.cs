using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The navigations of §25.3 that land somewhere, checked by following them (PRD §25.3, §41).
/// </summary>
/// <remarks>
/// A navigation is not verified by watching it open the right view. It is verified by looking at
/// which row the cursor came to rest on: a menu item that shows the service list with the cursor on
/// somebody else's unit is worse than one that says it cannot go anywhere, because the second is
/// obviously broken and the first is quietly wrong.
/// </remarks>
[TestFixture]
public sealed class ProcessNavigationTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();

    /// <summary>What the process table holds: a name and the cgroup its unit is read out of.</summary>
    public IReadOnlyList<(int Pid, string Name, string? Cgroup)> Processes { get; init; } = [];

    public void Sample(SystemSnapshot snapshot) {
      var records = snapshot.PrepareProcesses(this.Processes.Count);
      for (var i = 0; i < this.Processes.Count; ++i) {
        records[i] = default;
        records[i].Key = new(this.Processes[i].Pid, 100);
        records[i].Name = this.Processes[i].Name;
        records[i].UserName = "alice";
        records[i].ContainerPath = this.Processes[i].Cgroup;
        records[i].HandleCount = Counter.NotSampledYet;
      }
    }

    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => [];
    public IReadOnlyList<SessionRecord> GetSessions() => [];

    public IReadOnlyList<ServiceRecord> Services { get; init; } = [];

    public IReadOnlyList<ServiceRecord> GetServices() => this.Services;
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public void Dispose() { }
  }

  private static ServiceRecord Unit(string name, int mainPid = 0)
    => new(name, name + " does something", ServiceState.Running, true, false, mainPid, "/usr/bin/thing", "/usr/lib/systemd/system/" + name, "on-failure");

  /// <summary>A window with one sample already taken and its first row selected.</summary>
  private static MainWindow Window(StubProbe probe) {
    var window = new MainWindow(new Sampler(probe), probe, null);
    // One sample, so there are rows to select. Start() would do it and would also start a timer this
    // test has no message loop to run.
    window.RefreshOnce();
    // Through the grouping call, which is the public route that takes a sample, refills the tree and
    // puts the cursor on the first process. Start() would do the same and would also start a timer
    // this test has no message loop to run.
    window.ShowGrouping(Query.ProcessGrouping.None);
    return window;
  }

  #region go to the owning service (PRD §25.3)

  /// <summary>
  /// The whole point: the list comes up <em>and</em> the cursor is on the unit the process is in.
  /// </summary>
  [Test]
  public void GoingToTheOwningServiceLandsOnThatUnitsRow() {
    var window = Window(new() {
      Processes = [(4242, "sshd", "/system.slice/sshd.service")],
      Services = [Unit("cups.service"), Unit("sshd.service", 4242), Unit("dbus.service")],
    });

    var unit = window.GoToOwningService(out var refusal);

    Assert.Multiple(() => {
      Assert.That(unit, Is.EqualTo("sshd.service"));
      Assert.That(refusal, Is.Null);
      Assert.That(window.ShownView, Is.EqualTo("Services"), "the rail follows the navigation");
      Assert.That(window.SelectedServiceUnit, Is.EqualTo("sshd.service"), "and it lands on the right row");
    });
  }

  /// <summary>
  /// The innermost unit wins. A desktop application sits inside its user's session manager, which is
  /// itself a unit, and naming the outer one would report every program somebody starts as belonging
  /// to the thing that started it.
  /// </summary>
  [Test]
  public void TheInnermostUnitIsTheOwnerAndNotTheSessionManager() {
    var window = Window(new() {
      Processes = [(4242, "firefox", "/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope")],
      Services = [Unit("user@1000.service"), Unit("app-firefox.scope", 4242)],
    });

    Assert.That(window.GoToOwningService(out _), Is.EqualTo("app-firefox.scope"));
    Assert.That(window.SelectedServiceUnit, Is.EqualTo("app-firefox.scope"));
  }

  /// <summary>
  /// Most of a desktop is in no unit, and that is a finding about the process rather than a fault.
  /// It is said out loud, because "nothing happened" is what a broken menu item looks like too.
  /// </summary>
  [Test]
  public void AProcessInNoUnitIsToldSoAndNamesTheCgroupItLookedIn() {
    var window = Window(new() {
      Processes = [(4242, "editor", "/user.slice/user-1000.slice")],
      Services = [Unit("cups.service")],
    });

    Assert.That(window.GoToOwningService(out var refusal), Is.Null);
    Assert.That(refusal, Does.Contain("/user.slice/user-1000.slice"));
    Assert.That(refusal, Does.Contain("no systemd unit"));
    Assert.That(window.ShownView, Is.EqualTo("Processes"), "and it does not open a list it cannot point into");
  }

  /// <summary>An unreadable cgroup and an empty one are different sentences (PRD §72.3).</summary>
  [Test]
  public void AProcessWithNoReadableCgroupSaysThatRatherThanNamingOne() {
    var window = Window(new() {
      Processes = [(4242, "kthreadd", null)],
      Services = [Unit("cups.service")],
    });

    Assert.That(window.GoToOwningService(out var refusal), Is.Null);
    Assert.That(refusal, Does.Contain("no readable cgroup"));
  }

  /// <summary>
  /// A transient scope is in the cgroup tree and on no disk, so the unit-file walk never produced a
  /// row for it. The name is still the truth about the process and is reported as such.
  /// </summary>
  [Test]
  public void AUnitWithNoFileOnDiskIsNamedRatherThanSilentlyMissed() {
    var window = Window(new() {
      Processes = [(4242, "bash", "/user.slice/user-1000.slice/session-3.scope")],
      Services = [Unit("cups.service")],
    });

    Assert.That(window.GoToOwningService(out var refusal), Is.Null);
    Assert.That(refusal, Does.Contain("session-3.scope"));
    Assert.That(refusal, Does.Contain("transient"));
  }

  /// <summary>
  /// A machine that publishes no services at all is a different answer from a unit that is missing
  /// from a list that came back, and the two must not share a sentence (PRD §5.3).
  /// </summary>
  [Test]
  public void AMachineWithNoServicesAtAllSaysSoRatherThanBlamingTheUnit() {
    var window = Window(new() {
      Processes = [(4242, "sshd", "/system.slice/sshd.service")],
      Services = [],
    });

    Assert.That(window.GoToOwningService(out var refusal), Is.Null);
    Assert.That(refusal, Does.Contain("no service list came back"));
  }

  /// <summary>Nothing selected is not a refusal, and a dialog for it would be one.</summary>
  [Test]
  public void WithNothingSelectedThereIsNothingToSayAndNothingIsSaid() {
    var probe = new StubProbe { Services = [Unit("cups.service")] };
    var window = new MainWindow(new Sampler(probe), probe, null);

    Assert.That(window.GoToOwningService(out var refusal), Is.Null);
    Assert.That(refusal, Is.Null);
  }

  #endregion

}
