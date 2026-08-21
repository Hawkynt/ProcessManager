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

  private static MainWindow Window(StubProbe? probe = null) {
    probe ??= new();
    return new(new Sampler(probe), probe, null);
  }

  #region the rail (PRD §9, §10)

  [Test]
  public void TheRailCarriesEveryPrimaryView() {
    var window = Window();

    Assert.That(window.ViewTitles, Is.EqualTo(new[] {
      "Processes", "Performance", "Startup", "Users", "Services", "Network", "Find resources",
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

}
