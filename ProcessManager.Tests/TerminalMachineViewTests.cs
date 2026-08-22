using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The two views the terminal was missing: every unit, and what runs at login (PRD §3, §41, §42).
/// </summary>
/// <remarks>
/// The window has had both since §41 and §42 and the terminal had only the unit a process belongs
/// to, which is a different question — "what is running under this pid" against "what is on this
/// machine". A capability reachable from one front-end and not the other is the drift §58 forbids,
/// and it was what kept §3's first box open.
/// </remarks>
[TestFixture]
public sealed class TerminalMachineViewTests {

  private sealed class Probe(IReadOnlyList<ServiceRecord> services, IReadOnlyList<StartupEntry> startup)
    : ISystemProbe {

    public string Description => "stub";
    public HostInfo DescribeHost() => new();

    public void Sample(SystemSnapshot snapshot) {
      var records = snapshot.PrepareProcesses(1);
      records[0] = default;
      records[0].Key = new(1, 1);
      records[0].Name = "init";
    }

    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => startup;
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => services;
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public MemoryMapReading GetMemoryRegions(ProcessKey key) => MemoryMapReading.NotImplemented;
    public ProcessSecurity? DescribeSecurity(ProcessKey key) => null;
    public CgroupInfo? DescribeCgroup(ProcessKey key) => null;
    public ImageInfo? DescribeImage(ProcessKey key) => null;
    public void Dispose() { }
  }

  private static ServiceRecord Unit(string name, ServiceState state, int pid = 0)
    => new(name, null, state, null, false, pid, null, $"/lib/systemd/system/{name}", null);

  private static StartupEntry Item(string name, bool enabled, string? why = null)
    => new(name, $"/usr/bin/{name}", $"/etc/xdg/autostart/{name}.desktop", enabled, why, StartupScope.System, null);

  private static string Screen(
    IReadOnlyList<ServiceRecord> services,
    IReadOnlyList<StartupEntry> startup,
    char key
  ) {
    var probe = new Probe(services, startup);
    var ui = new TerminalUi(new Sampler(probe), probe, null, 120, 40, ColorDepth.None) { ShowTiming = false };
    ui.Update();
    ui.HandleKey(new(key, default, false, false, false));
    ui.Refresh();
    return ui.Screen.Capture();
  }

  private static string Services(params ServiceRecord[] units) => Screen(units, [], 'W');

  private static string Startup(params StartupEntry[] entries) => Screen([], entries, 'b');

  // --- units ------------------------------------------------------------------------------------

  [Test]
  public void EveryUnitIsListedWithItsState() {
    var text = Services(
      Unit("a.service", ServiceState.Running, 42),
      Unit("b.service", ServiceState.Inactive)
    );

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("a.service"));
      Assert.That(text, Does.Contain("b.service"));
      Assert.That(text, Does.Contain("running"));
      Assert.That(text, Does.Contain("inactive"));
      Assert.That(text, Does.Contain("2 units, 1 running"));
    });
  }

  /// <summary>
  /// The pid where there is one: "running" and "running as 1234" answer different follow-up
  /// questions, and the second is the one that leads back to the table.
  /// </summary>
  [Test]
  public void ARunningUnitCarriesItsMainPid()
    => Assert.That(Services(Unit("a.service", ServiceState.Running, 4242)), Does.Contain("4242"));

  /// <summary>
  /// Running first. Somebody opening this after something broke reads the live ones and then the
  /// dead ones, which is the order the question comes in.
  /// </summary>
  [Test]
  public void RunningComesBeforeInactive() {
    var text = Services(
      Unit("zzz.service", ServiceState.Running, 1),
      Unit("aaa.service", ServiceState.Inactive)
    );

    Assert.That(text.IndexOf("zzz.service", StringComparison.Ordinal),
      Is.LessThan(text.IndexOf("aaa.service", StringComparison.Ordinal)),
      "a running unit sorts above an inactive one whatever its name");
  }

  /// <summary>
  /// Nothing back is a sentence and not an empty box. A machine with no service manager and a
  /// machine whose units this user may not read look identical from here, and only one of them is
  /// worth acting on (PRD §5.3, §72.3).
  /// </summary>
  [Test]
  public void NoUnitsSaysWhichKindOfNothingItIs()
    => Assert.That(Services(), Does.Contain("no service manager").IgnoreCase);

  // --- autostart --------------------------------------------------------------------------------

  [Test]
  public void WhatRunsAndWhatDoesNotAreSeparated() {
    var text = Startup(Item("kept", true), Item("dropped", false, "hidden"));

    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("will run (1)"));
      Assert.That(text, Does.Contain("will not (1)"));
      Assert.That(text, Does.Contain("2 autostart entries, 1 will run"));
    });
  }

  /// <summary>
  /// <b>The reason and not just the fact.</b> "Hidden" and "only shown in GNOME" are different
  /// problems and only one of them is a mistake; a list saying nothing but "off" makes somebody open
  /// the file to find out which.
  /// </summary>
  [Test]
  public void AnEntryThatWillNotRunSaysWhy() {
    var text = Startup(Item("gnome-thing", false, "only for GNOME"));

    Assert.That(text, Does.Contain("only for GNOME"));
  }

  /// <summary>And one nobody gave a reason for still says something rather than nothing.</summary>
  [Test]
  public void AnEntryWithNoStatedReasonStillSaysItIsOff()
    => Assert.That(Startup(Item("quiet", false)), Does.Contain("switched off"));

  /// <summary>
  /// A machine where everything runs has no second heading. An empty "will not" group would read as
  /// a list that had failed to load rather than as good news.
  /// </summary>
  [Test]
  public void NothingDisabledMeansNoSecondHeading() {
    var text = Startup(Item("a", true), Item("b", true));

    Assert.That(text, Does.Contain("will run (2)"));
    Assert.That(text, Does.Not.Contain("will not"));
  }

  [Test]
  public void NoEntriesSaysSoRatherThanShowingAnEmptyBox()
    => Assert.That(Startup(), Does.Contain("no autostart").IgnoreCase);

  // --- the keys ---------------------------------------------------------------------------------

  /// <summary>
  /// Both are in the binding table, so the help screen lists them and somebody can rebind them. A
  /// key wired straight into the switch works and is invisible (PRD §57.3).
  /// </summary>
  [Test]
  public void BothViewsAreBoundAndDescribed() {
    var found = new Dictionary<TerminalAction, BindingInfo>();
    foreach (var binding in KeyBindings.Catalogue)
      found[binding.Action] = binding;

    Assert.Multiple(() => {
      foreach (var action in (ReadOnlySpan<TerminalAction>)[TerminalAction.ServicesView, TerminalAction.StartupView]) {
        Assert.That(found.ContainsKey(action), Is.True, $"{action} is not in the catalogue");
        Assert.That(found[action].DefaultKeys, Is.Not.Empty, $"{action} has no key");
        Assert.That(found[action].Description, Is.Not.Empty, $"{action} has no sentence");
      }
    });
  }

  /// <summary>
  /// And neither took a key something else already had. Two actions on one key is a binding that
  /// silently never fires, which is the failure the catalogue exists to make visible.
  /// </summary>
  [Test]
  public void NeitherKeyWasAlreadyTaken() {
    var owners = new Dictionary<string, TerminalAction>(StringComparer.Ordinal);

    Assert.Multiple(() => {
      foreach (var binding in KeyBindings.Catalogue)
        foreach (var key in binding.DefaultKeys) {
          Assert.That(
            owners.TryAdd(key, binding.Action),
            Is.True,
            $"'{key}' is bound to both {owners.GetValueOrDefault(key)} and {binding.Action}"
          );
        }
    });
  }

}
