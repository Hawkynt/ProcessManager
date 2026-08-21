using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The window's half of §11 and §83: the column layout and the grouping surviving a restart.
/// </summary>
/// <remarks>
/// Testable without a display for the reason the binder is: the controls are owner-drawn and their
/// state is real before anything is realised. What a display is needed for — that the widths reach
/// the header and the headings are drawn as headings — is the capture leg's job, and the shoot log
/// records both.
/// </remarks>
[TestFixture]
public sealed class MainWindowTableTests {

  private sealed class StubProbe : ISystemProbe {
    public string Description => "stub";
    public HostInfo DescribeHost() => new();
    public void Sample(SystemSnapshot snapshot) => snapshot.PrepareProcesses(0);
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

  private static MainWindow Window() {
    var probe = new StubProbe();
    return new(new Sampler(probe), probe, null);
  }

  /// <summary>
  /// The order somebody chose is the order that comes back. The chooser hands over a set; the order
  /// is the window's own state and the file has to carry it.
  /// </summary>
  [Test]
  public void TheColumnOrderSurvivesARestart() {
    var window = Window();
    ProcessField[] order = [ProcessField.Pid, ProcessField.Name, ProcessField.UserName];

    window.ApplySettings(new() { DesktopColumns = order }, _ => true);

    Assert.That(window.DescribeSettings().DesktopColumns, Is.EqualTo(order));
  }

  [Test]
  public void AChosenWidthSurvivesARestart() {
    var window = Window();
    window.ApplySettings(new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid],
      DesktopColumnWidths = [new(ProcessField.Pid, 140)],
    }, _ => true);

    var described = window.DescribeSettings();
    Assert.That(described.DesktopColumnWidths, Has.Count.EqualTo(1));
    Assert.That(described.DesktopColumnWidths[0], Is.EqualTo(new KeyValuePair<ProcessField, int>(ProcessField.Pid, 140)));
  }

  /// <summary>
  /// A width for a column that is not showing is dropped rather than kept forever: the file lists
  /// widths of columns, and a column that is not there has none.
  /// </summary>
  [Test]
  public void AWidthForAColumnThatIsNotShowingIsNotCarried() {
    var window = Window();
    window.ApplySettings(new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid],
      DesktopColumnWidths = [new(ProcessField.CommandLine, 900)],
    }, _ => true);

    Assert.That(window.DescribeSettings().DesktopColumnWidths, Is.Empty);
  }

  [Test]
  public void TheGroupingSurvivesARestart() {
    var window = Window();
    window.ApplySettings(new() { Grouping = ProcessGrouping.Session }, _ => true);

    Assert.That(window.Grouping, Is.EqualTo(ProcessGrouping.Session));
    Assert.That(window.DescribeSettings().Grouping, Is.EqualTo(ProcessGrouping.Session));
    Assert.That(window.FlatMode, Is.True, "a session-grouped list is not the parent tree");
  }

  /// <summary>
  /// The old spelling still works. A settings file that says <c>tree=true</c> was written before
  /// grouping existed, and it has to open the window as a tree.
  /// </summary>
  [Test]
  public void TheOlderTreeSettingStillMeansTheTree() {
    var window = Window();
    window.ApplySettings(UserSettings.Parse("tree=true"), _ => true);

    Assert.That(window.Grouping, Is.EqualTo(ProcessGrouping.ParentTree));
    Assert.That(window.FlatMode, Is.False);
  }

  /// <summary>
  /// A grouping written by a newer build leaves an older one on its default rather than on nothing:
  /// a word this build cannot parse must not silently flatten the list.
  /// </summary>
  /// <remarks>
  /// The line itself is not carried through, which is what this file does with every recognised key
  /// whose value it cannot read — <c>sort=</c> and <c>cpu.mode=</c> behave the same way. Only
  /// unrecognised <em>keys</em> are kept verbatim. It is worth knowing which of the two this is.
  /// </remarks>
  [Test]
  public void AGroupingThisBuildDoesNotKnowIsLeftAlone() {
    var parsed = UserSettings.Parse("grouping=publisher\ninterval=4");

    Assert.That(parsed.Grouping, Is.EqualTo(ProcessGrouping.ParentTree), "the default, not a flat list");
    Assert.That(parsed.IntervalSeconds, Is.EqualTo(4), "and the rest of the file still parsed");
  }

}
