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

  /// <summary>
  /// The pinned run is a count into the column order, so it has to be restored after the order is —
  /// restoring it first clamps it against whatever the window happened to open with (PRD §11).
  /// </summary>
  [Test]
  public void ThePinnedColumnsSurviveARestart() {
    var window = Window();
    window.ApplySettings(new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.CpuPercent],
      PinnedDesktopColumns = 3,
    }, _ => true);

    Assert.That(window.DescribeSettings().PinnedDesktopColumns, Is.EqualTo(3));
    Assert.That(window.DescribeForCapture(), Does.Contain("pinned:       3 of them"));
  }

  /// <summary>
  /// A file asking for more pinned columns than it lists gets what it can have rather than a table
  /// the toolkit will not scroll at all.
  /// </summary>
  [Test]
  public void MorePinnedColumnsThanThereAreIsClampedToWhatIsThere() {
    var window = Window();
    window.ApplySettings(new() {
      DesktopColumns = [ProcessField.Name, ProcessField.Pid],
      PinnedDesktopColumns = 9,
    }, _ => true);

    Assert.That(window.DescribeSettings().PinnedDesktopColumns, Is.EqualTo(2));
  }

  /// <summary>
  /// The third shape of copy §11 asks a table for. It needs a column and not a cell selection: the
  /// column cursor is what "this column" means, in this window and in the terminal alike.
  /// </summary>
  /// <remarks>
  /// Over the recorded machine rather than the stub, because a column copy of nothing would pass
  /// against an empty table however it was built.
  /// </remarks>
  [Test]
  public void CopyingAColumnTakesItDownEveryRowThatIsShowing() {
    using var probe = TerminalFixture.Probe();
    var window = new MainWindow(new Sampler(probe), probe, null);
    window.ApplySettings(new() { DesktopColumns = [ProcessField.Name, ProcessField.Pid] }, _ => true);
    window.Start();

    var lines = (window.ColumnAsText() ?? string.Empty).TrimEnd('\n').Split('\n');
    Assert.That(lines[0], Is.EqualTo(FieldRegistry.Get(ProcessField.Name).Header), "the column is named");
    Assert.That(lines, Has.Length.EqualTo(6), "the recorded machine's five processes, under a header");
    Assert.That(lines[1], Is.Not.Empty);
  }

  /// <summary>A drawn history has no text, and an empty copy looks exactly like one that failed.</summary>
  [Test]
  public void ADrawnHistoryIsNotAColumnACopyCanTake() {
    var window = Window();
    window.ApplySettings(new() { DesktopColumns = [ProcessField.CpuHistory, ProcessField.Pid] }, _ => true);

    Assert.That(window.ColumnAsText(), Is.Null);
  }

  #region the sample tick (PRD §12)

  [Test]
  public void TheWindowOffersTheSameRatesTheTerminalDoes() {
    var window = Window();
    window.ApplySettings(new(), _ => true);

    foreach (var seconds in UserSettings.OfferedIntervalSeconds) {
      var described = window.ShowRefresh((int)Math.Round(seconds * 1000));
      Assert.That(described, Does.Contain("refresh:      every " + UserSettings.NameOfInterval(seconds)));
    }
  }

  /// <summary>
  /// Pausing holds the list where it was. Nothing rebuilds while the tick is off, so the selection,
  /// the expansion and the scroll position are all exactly where the reader left them (PRD §12).
  /// </summary>
  [Test]
  public void PausingHoldsTheListWhereItWas() {
    var window = Window();
    window.ApplySettings(new(), _ => true);
    window.Start();
    window.SelectFirstRow();
    var before = window.DescribeForCapture();

    window.ShowRefresh(0, paused: true);

    Assert.That(window.Paused, Is.True);
    Assert.That(window.DescribeForCapture(), Does.Contain("refresh:      paused"));
    Assert.That(
      Rows(window.DescribeForCapture()),
      Is.EqualTo(Rows(before)),
      "a paused list is the list that was there, not a rebuilt one"
    );
  }

  private static string Rows(string described) {
    foreach (var line in described.Split('\n'))
      if (line.StartsWith("process rows:", StringComparison.Ordinal))
        return line;

    return string.Empty;
  }

  /// <summary>
  /// A by-hand refresh is a preference and is written down; a pause is a toggle somebody flips for a
  /// few seconds, and a window that opened paused because it was paused when it was last closed is a
  /// window showing a table of nothing at all.
  /// </summary>
  [Test]
  public void RefreshingByHandSurvivesARestartAndAPauseDoesNot() {
    var window = Window();
    window.ApplySettings(new() { IntervalSeconds = 5 }, _ => true);

    window.ShowRefresh(0, manual: true);
    var described = window.DescribeSettings();
    Assert.That(described.ManualRefresh, Is.True);
    Assert.That(described.IntervalSeconds, Is.EqualTo(5), "the rate underneath is remembered");

    window.ShowRefresh(0, paused: true);
    Assert.That(window.DescribeSettings().ManualRefresh, Is.False, "a pause is not written down");
  }

  [Test]
  public void AWindowAskedToRefreshByHandStillOpensOnSomething() {
    var window = Window();
    window.ApplySettings(new() { ManualRefresh = true }, _ => true);
    window.Start();

    Assert.That(window.ManualRefresh, Is.True);
    Assert.That(window.DescribeForCapture(), Does.Contain("refresh:      by hand"));
    // The stub machine has no processes, so what proves a sample was taken is the status line: a
    // window that had waited for the first keystroke would have no sample to time.
    Assert.That(window.DescribeForCapture(), Does.Contain("processes"));
    Assert.That(window.DescribeForCapture(), Does.Contain("·  refreshed by hand"));
  }

  #endregion

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
