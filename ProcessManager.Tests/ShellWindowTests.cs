using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The window's shell: the rail, the command bar and the lower pane (PRD §9, §10).
/// </summary>
/// <remarks>
/// Everything §9 asks for behind the rail was already collected and already printable from the
/// command line — its complaint was that there was no way to <em>get</em> to any of it. What is
/// checked here is the getting: that every view is reachable by name, that choosing one puts it in
/// the content region, and that an empty answer says which of the two things "empty" means.
/// </remarks>
[TestFixture]
public sealed class ShellWindowTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) => snapshot.PrepareProcesses(0);
    public Counter GetHandleCount(ProcessKey key) => Counter.NotSupported;
    public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];
    public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];
    public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];
    public IReadOnlyList<ConnectionRecord> GetConnections() => this.Connections;
    public Query.ServiceNames DescribePortNames() => this.PortNames;

    /// <summary>Every socket on the machine, for the rail's own network view.</summary>
    public IReadOnlyList<ConnectionRecord> Connections { get; set; } = [];

    /// <summary>What this machine would call its ports. Empty is a machine with no such file.</summary>
    public Query.ServiceNames PortNames { get; set; } = Query.ServiceNames.Empty;
    public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];
    public IReadOnlyList<StartupEntry> GetStartupEntries() => this.Startup;
    public IReadOnlyList<SessionRecord> GetSessions() => [];
    public IReadOnlyList<ServiceRecord> GetServices() => this.Services;
    public DiskInfo DescribeDisk(string name) => new(name, null, null, Counter.NotSupported);

    public NetworkInterfaceInfo DescribeInterface(string name)
      => new(name, null, Counter.NotSupported, null, Counter.NotSupported, false);

    public IReadOnlyList<StartupEntry> Startup { get; set; } = [];

    public IReadOnlyList<ServiceRecord> Services { get; set; } = [];

    public void Dispose() { }
  }

  private static IEnumerable<Control> Descendants(Control root) {
    foreach (Control child in root.Controls) {
      yield return child;
      foreach (var deeper in Descendants(child))
        yield return deeper;
    }
  }

  private static MainWindow Window(StubProbe? probe = null) {
    probe ??= new();
    return new(new Sampler(probe), probe, null);
  }

  #region the rail (PRD §9, §10)

  /// <summary>
  /// Every view the rail is supposed to carry, in the order it carries them.
  /// </summary>
  /// <remarks>
  /// Exact rather than a containment check, and that is the point: a view added here has to be added
  /// here too, and a view that quietly stops being built fails rather than passing a "contains the
  /// ones I remembered" test. The order is part of it, because the rail is read top to bottom and
  /// "Find resources" belongs at the end — it is a question rather than a place.
  /// </remarks>
  [Test]
  public void TheRailCarriesEveryPrimaryView() {
    var window = Window();

    Assert.That(window.ViewTitles, Is.EqualTo(new[] {
      "Processes", "Performance", "Startup", "Users", "Services", "Network",
      "History", "Timeline", "Find resources",
    }));
  }

  /// <summary>
  /// The rail's own network view names a port, which is the fourth surface that shows an endpoint
  /// and the one it is easiest to forget: the lower pane, the terminal and the command line were all
  /// fixed together and this one reads its cells in a different file (PRD §40, §58).
  /// </summary>
  [Test]
  public void TheNetworkViewNamesAPort() {
    var probe = new StubProbe {
      PortNames = Query.ServiceNames.Parse("https 443/tcp"),
      Connections = [
        new(
          ConnectionProtocol.Tcp, SocketKind.Stream,
          "192.168.1.5", 38658, "93.184.216.34", 443,
          "ESTABLISHED", 77, 4242, 1000, "alice", "wlp1s0",
          Counter.Of(0ul), Counter.Of(0ul), Counter.Of(0ul),
          SocketStatistics.NotSupported, Rate.NotSampledYet, Rate.NotSampledYet,
          null, null, Counter.NotSupported
        ),
      ],
    };

    var window = Window(probe);
    Assert.That(window.ShowView("Network"), Is.True);

    var shown = window.DescribeView("Network");
    Assert.That(shown, Does.Contain("93.184.216.34:https"));
    Assert.That(shown, Does.Not.Contain(":443"));
  }

  /// <summary>
  /// A socket row offers its owner from a menu and not only from a double-click. The gesture worked
  /// and was the only way in, which for somebody who works from a menu is the same as a command that
  /// is not there (PRD §25.3, §40).
  /// </summary>
  [Test]
  public void ASocketRowOffersItsOwner() {
    var window = Window();
    Assert.That(window.ShowView("Network"), Is.True);

    var labels = new List<string>();
    foreach (var control in Descendants(window))
      if (control is TreeListView { AccessibleName: "Connections" } list && list.ContextMenuStrip is { } menu)
        foreach (var item in menu.Items)
          if (item is ToolStripMenuItem entry)
            labels.Add(entry.Text);

    Assert.That(labels, Does.Contain("Go to process"));
    Assert.That(labels, Does.Contain("Process properties…"));
  }

  /// <summary>
  /// A socket whose owner this account may not see is not a socket owned by nobody. Going to it says
  /// which of the two it is, rather than navigating nowhere and leaving the reader to conclude the
  /// program cannot attribute sockets at all (PRD §72.3).
  /// </summary>
  [Test]
  public void GoingToAnUnattributableOwnerSaysWhyRatherThanNothing() {
    var probe = new StubProbe {
      Connections = [
        new(
          ConnectionProtocol.Tcp, SocketKind.Stream,
          "0.0.0.0", 22, string.Empty, 0,
          "LISTEN", 91, 0, -1, null, "lo",
          Counter.NotSupported, Counter.NotSupported, Counter.NotSupported,
          SocketStatistics.NotSupported, Rate.NotSampledYet, Rate.NotSampledYet,
          null, null, Counter.NotSupported
        ),
      ],
    };

    var window = Window(probe);
    var said = new List<string>();
    window.Say = said.Add;
    Assert.That(window.ShowView("Network"), Is.True);

    foreach (var control in Descendants(window))
      if (control is TreeListView { AccessibleName: "Connections" } list) {
        list.SelectedNode = list.Nodes[0];
        foreach (var item in list.ContextMenuStrip!.Items)
          if (item is ToolStripMenuItem { Text: "Go to process" } go)
            go.PerformClick();
      }

    Assert.That(said, Has.Count.EqualTo(1));
    Assert.That(said[0], Does.Contain("not visible from this account"));
    Assert.That(window.ShownView, Is.EqualTo("Network"), "and the view is left where it was");
  }

  [Test]
  public void TheWindowOpensOnTheProcessList() =>
    Assert.That(Window().ShownView, Is.EqualTo("Processes"));

  [Test]
  public void ChoosingAViewPutsItInTheContentRegion() {
    var window = Window();

    Assert.That(window.ShowView("Services"), Is.True);
    Assert.That(window.ShownView, Is.EqualTo("Services"));

    Assert.That(window.ShowView("Processes"), Is.True);
    Assert.That(window.ShownView, Is.EqualTo("Processes"));
  }

  [Test]
  public void AViewItHasNotGotIsSaidToBeMissingRatherThanSilentlyIgnored() {
    var window = Window();

    Assert.That(window.ShowView("Telemetry"), Is.False);
    Assert.That(window.ShownView, Is.EqualTo("Processes"), "and the window is left where it was");
  }

  /// <summary>
  /// An entry that opens a window of its own must not leave the rail claiming to be showing
  /// something the content region is not showing.
  /// </summary>
  [Test]
  public void AnEntryThatOpensAWindowLeavesTheContentRegionAlone() {
    var window = Window();

    window.ShowView("Services");
    try {
      window.ShowView("Find resources");
    } catch (InvalidOperationException) {
      // There is no display here, so the toolkit refuses to show the dialog. That is the point of
      // the test rather than a problem with it: the rail has to be put back before the window is
      // opened, so that a modal one — or one that will not open at all — cannot leave the rail
      // pointing at an entry the content region is not showing.
    }

    Assert.That(window.ShownView, Is.EqualTo("Services"));
  }

  /// <summary>
  /// Nought rows is the one answer that needs a sentence, because it is the one that can mean two
  /// different things — nothing is configured, or nothing here may read it (PRD §5.3, §72.3).
  /// </summary>
  [Test]
  public void AnEmptyViewSaysWhichKindOfEmptyItIs() {
    var window = Window();
    window.ShowView("Startup");

    var text = window.DescribeView("Startup");
    Assert.That(text, Is.Not.Empty);
    Assert.That(text, Does.Not.Contain("0 entries"), "a count is not an explanation");
    Assert.That(text.Split('\n')[0], Does.Contain("or nothing this build knows how to read is"));
  }

  [Test]
  public void AViewWithRowsCountsThemAndSaysHowManyMatter() {
    var probe = new StubProbe {
      Services = [
        new("one.service", "The first", ServiceState.Running, true, false, 42, null, "/lib/systemd/system/one.service", "always"),
        new("two.service", "The second", ServiceState.Inactive, false, false, 0, null, "/lib/systemd/system/two.service", null),
      ],
    };

    var window = Window(probe);
    window.ShowView("Services");

    var text = window.DescribeView("Services");
    Assert.That(text.Split('\n')[0], Does.Contain("2 units, 1 running"));
    Assert.That(text, Does.Contain("one.service"));
    Assert.That(text, Does.Contain("inactive"));
  }

  /// <summary>
  /// The reason, not the boolean. "Hidden by a user override" and "not for this desktop" are
  /// different problems with different fixes, and a column of "no" tells the reader neither.
  /// </summary>
  [Test]
  public void ADisabledStartupEntrySaysWhyRatherThanNo() {
    var probe = new StubProbe {
      Startup = [
        new("Panel applet", "xfce4-panel", "/etc/xdg/autostart/panel.desktop", false, "not for KDE", StartupScope.System, "XFCE;"),
      ],
    };

    var window = Window(probe);
    window.ShowView("Startup");

    Assert.That(window.DescribeView("Startup"), Does.Contain("not for KDE"));
  }

  #endregion

  #region the lower pane (PRD §10)

  [Test]
  public void TheLowerPaneIsShowingToStartWith() =>
    Assert.That(Window().LowerPaneVisible, Is.True);

  [Test]
  public void TheLowerPaneRemembersWhetherItWasShowing() {
    var window = Window();

    window.ApplySettings(new() { LowerPaneVisible = false }, _ => true);
    Assert.That(window.LowerPaneVisible, Is.False);
    Assert.That(window.DescribeSettings().LowerPaneVisible, Is.False);

    window.ApplySettings(new() { LowerPaneVisible = true }, _ => true);
    Assert.That(window.LowerPaneVisible, Is.True);
  }

  [Test]
  public void WhetherTheLowerPaneIsShowingSurvivesTheFile() {
    var written = new UserSettings { LowerPaneVisible = false }.Write();

    Assert.That(UserSettings.Parse(written).LowerPaneVisible, Is.False);
    // On by default, so the file stays short for everybody who never turned it off.
    Assert.That(new UserSettings().Write(), Does.Not.Contain("window.lowerpane"));
  }

  #endregion

  #region what the capture log sees (PRD §9.6)

  /// <summary>
  /// Counts and no contents. The number is the empty-view detector; the rows themselves are this
  /// machine's services, its logins and its open sockets, and none of that belongs in a log that
  /// goes into a public repository.
  /// </summary>
  [Test]
  public void TheCaptureLogCountsTheViewsWithoutQuotingThem() {
    var probe = new StubProbe {
      Services = [
        new("secret.service", "Something private", ServiceState.Running, true, false, 42, null, "/lib/systemd/system/secret.service", null),
      ],
    };

    var window = Window(probe);
    var described = window.DescribeShellForCapture();

    Assert.That(described, Does.Contain("Services"));
    Assert.That(described, Does.Contain("1 row(s)"));
    Assert.That(described, Does.Not.Contain("secret.service"));
  }

  /// <summary>Walking every view for the log must leave the window where it found it.</summary>
  [Test]
  public void DescribingEveryViewPutsTheShownOneBack() {
    var window = Window();
    window.ShowView("Users");

    window.DescribeShellForCapture();

    Assert.That(window.ShownView, Is.EqualTo("Users"));
  }

  [Test]
  public void TheCaptureDescriptionNamesTheShell() {
    var described = Window().DescribeForCapture();

    Assert.That(described, Does.Contain("rail:"));
    Assert.That(described, Does.Contain("command bar:"));
    Assert.That(described, Does.Contain("lower pane shown"));
  }

  #endregion

  #region what a row offers (PRD §41, §42)

  private static List<string> MenuItems(Control root, string table) {
    foreach (var control in Descendants(root))
      if (control is TreeListView list
        && string.Equals(list.AccessibleName, table, StringComparison.Ordinal)
        && list.ContextMenuStrip is { } menu) {
        var texts = new List<string>();
        foreach (var item in menu.Items)
          texts.Add(item.Text ?? string.Empty);

        return texts;
      }

    Assert.Fail($"the {table} table has no menu");
    return [];
  }

  /// <summary>
  /// §41's six read-only actions, on a machine with no manager to command. They were the whole of
  /// what a person looking at a unit list actually needs and none of them was reachable from
  /// anywhere: a service list you cannot open the unit file from is a list you have to leave to use.
  /// </summary>
  [Test]
  public void AUnitRowOffersTheActionsThatOnlyReadIt() {
    var window = Window();
    window.ShowView("Services");

    Assert.That(MenuItems(window, "Services"), Is.SupersetOf(new[] {
      "Open its configuration",
      "Reveal its executable",
      "Go to its main process",
      "Unit properties…",
      "Inspect dependencies…",
      "Copy row",
      "Copy all units",
    }));
  }

  /// <summary>
  /// And with no manager the six verbs are absent rather than present and refusing. A menu of items
  /// that all answer "not on this platform" is worse than a shorter menu (PRD §7).
  /// </summary>
  [Test]
  public void WithNoManagerTheUnitRowOffersNoVerbs() {
    var window = Window();
    window.ShowView("Services");

    Assert.That(MenuItems(window, "Services"), Does.Not.Contain("Stop"));
  }

  [Test]
  public void ALoginEntryOffersTheActionsThatOnlyReadIt() {
    var window = Window();
    window.ShowView("Startup");

    Assert.That(MenuItems(window, "Startup entries"), Is.SupersetOf(new[] {
      "Run it now",
      "Open its configuration",
      "Reveal its program",
      "Entry properties…",
      "Copy row",
      "Copy all entries",
    }));
  }

  /// <summary>
  /// Deleting is not offered where nothing can write the switch, and never beside it: turning an
  /// entry off is undone by the item above it and deleting a file is undone by nothing (PRD §42).
  /// </summary>
  [Test]
  public void DeletingIsNotOfferedWhereNothingCanWriteTheSwitch() {
    var window = Window();
    window.ShowView("Startup");

    Assert.That(MenuItems(window, "Startup entries"), Does.Not.Contain("Delete this entry…"));
  }

  /// <summary>
  /// The columns §41 asks for that this machine's files answer, in the table rather than only in a
  /// box: the type, the account, the load state, the activation time and the dependency counts.
  /// </summary>
  [Test]
  public void TheServiceTableCarriesTheFieldsTheUnitFilesAnswer() {
    var probe = new StubProbe {
      Services = [
        new("one.service", "The first", ServiceState.Running, true, false, 42, "/usr/bin/one --daemon", "/lib/systemd/system/one.service", "always") {
          Type = "notify",
          Account = "nobody",
          LoadState = ServiceLoadState.Loaded,
        },
      ],
    };

    var window = Window(probe);
    window.ShowView("Services");

    var text = window.DescribeView("Services");
    Assert.Multiple(() => {
      Assert.That(text, Does.Contain("notify"));
      Assert.That(text, Does.Contain("nobody"));
      Assert.That(text, Does.Contain("loaded"));
      Assert.That(text, Does.Contain("/usr/bin/one --daemon"));
    });
  }

  /// <summary>
  /// A unit that is active with no processes is neither running nor stopped, and this reader used to
  /// call it stopped — which is the answer somebody would act on and be wrong (PRD §41).
  /// </summary>
  [Test]
  public void AUnitActiveWithNoProcessesIsNeitherRunningNorInactive() {
    var probe = new StubProbe {
      Services = [
        new("done.service", "Set something up", ServiceState.Active, true, false, 0, null, "/lib/systemd/system/done.service", null),
      ],
    };

    var window = Window(probe);
    window.ShowView("Services");

    var text = window.DescribeView("Services");
    Assert.That(text, Does.Contain("active · exited"));
    Assert.That(text.Split('\n')[0], Does.Contain("1 more active"));
  }

  /// <summary>
  /// The impact column says nothing measured it rather than inventing a category. A "Medium" beside a
  /// program nobody has timed is a guess wearing a measurement's clothes, and it is the one a reader
  /// would act on (PRD §42).
  /// </summary>
  [Test]
  public void TheImpactColumnSaysThatNothingMeasuredIt() {
    var probe = new StubProbe {
      Startup = [new("Thing", "/usr/bin/thing", "/etc/xdg/autostart/thing.desktop", true, null, StartupScope.System, null)],
    };

    var window = Window(probe);
    window.ShowView("Startup");

    var text = window.DescribeView("Startup");
    Assert.That(text, Does.Contain("not measured"));
    Assert.That(text, Does.Not.Contain("Medium"));
    Assert.That(text.Split('\n')[0], Does.Contain("Impact is not measured"));
  }

  /// <summary>
  /// Which mechanism will start an entry is in the table, because it is also what turning it off
  /// means: a desktop file and a user unit are switched in completely different ways (PRD §42).
  /// </summary>
  [Test]
  public void TheStartupTableSaysWhichMechanismWillStartAnEntry() {
    var probe = new StubProbe {
      Startup = [
        new("agent.service", "/usr/lib/agent", "/usr/lib/systemd/user/agent.service", true, null, StartupScope.System, null) {
          Mechanism = StartupMechanism.SystemdUserUnit,
          Executable = "/usr/lib/agent",
        },
      ],
    };

    var window = Window(probe);
    window.ShowView("Startup");

    Assert.That(window.DescribeView("Startup"), Does.Contain("systemd user unit"));
  }

  #endregion

}
