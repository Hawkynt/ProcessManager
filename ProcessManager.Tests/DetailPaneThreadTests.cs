using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The thread tab of the detail pane, filled from a recorded <c>/proc</c> (PRD §29).
/// </summary>
/// <remarks>
/// Every column reads its cell out of the row's tag array by index, so a row with fewer cells than
/// the list has columns throws while painting — in the window, on a machine with a display, long
/// after the tests went green. Rendering every column of every row here is what catches it.
/// </remarks>
[TestFixture]
public sealed class DetailPaneThreadTests {

  private static string FixtureRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");

  private static LinuxProbe Probe() => new(new() {
    ProcRoot = FixtureRoot,
    PasswdPath = Path.Combine(FixtureRoot, "passwd"),
    ClockTicksPerSecond = 100,
    PageSize = 4096,
    EffectiveUserId = 0,
  });

  /// <summary>The filled thread list, and the pane keeping it alive.</summary>
  private static (DetailPane Pane, TreeListView List) Threads(int pid) {
    var probe = Probe();
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    var key = ProcessKey.None;
    foreach (var process in snapshot.Processes)
      if (process.Pid == pid)
        key = process.Key;

    Assert.That(key.IsNone, Is.False, $"pid {pid} is not in the fixture");

    var pane = new DetailPane(probe);
    var tabs = (TabControl)pane.Control;
    pane.Select(key);

    for (var i = 0; i < tabs.TabPages.Count; ++i)
      if (tabs.TabPages[i].Text == "Threads") {
        // Setting the index raises the change the pane listens for, which is what fills the list.
        tabs.SelectedIndex = i;
        return (pane, (TreeListView)tabs.TabPages[i].Controls[0]);
      }

    Assert.Fail("the pane has no thread tab");
    return default;
  }

  private static string Cell(TreeListView list, int row, string header) {
    for (var i = 0; i < list.Columns.Count; ++i)
      if (list.Columns[i].Text == header)
        return list.Columns[i].TextSelector!(list.Nodes[row]);

    Assert.Fail($"there is no '{header}' column");
    return string.Empty;
  }

  private static int Row(TreeListView list, string tid) {
    for (var i = 0; i < list.Nodes.Count; ++i)
      if (Cell(list, i, "TID") == tid)
        return i;

    Assert.Fail($"tid {tid} is not in the list");
    return -1;
  }

  [Test]
  public void EveryColumnOfEveryRowRenders() {
    var (_, list) = Threads(1001);

    Assert.That(list.Nodes, Has.Count.EqualTo(4));
    for (var row = 0; row < list.Nodes.Count; ++row)
      for (var column = 0; column < list.Columns.Count; ++column)
        Assert.That(
          list.Columns[column].TextSelector!(list.Nodes[row]),
          Is.Not.Null,
          $"row {row}, column '{list.Columns[column].Text}'"
        );
  }

  /// <summary>
  /// The placeholder row has to be as wide as the list is: it used to carry a fixed five cells, so
  /// every column past the fifth threw the moment somebody opened an empty tab.
  /// </summary>
  [Test]
  public void TheNothingToShowRowFillsEveryColumnToo() {
    var (_, list) = Threads(1000);

    Assert.That(list.Nodes, Has.Count.EqualTo(1));
    for (var column = 0; column < list.Columns.Count; ++column)
      Assert.That(list.Columns[column].TextSelector!(list.Nodes[0]), Is.Not.Null);

    Assert.That(Cell(list, 0, "TID"), Does.StartWith("nothing to show"));
  }

  [Test]
  public void TheNewSchedulingColumnsShowWhatTheProbeRead() {
    var (_, list) = Threads(1001);
    var main = Row(list, "1001");
    var realtime = Row(list, "1017");

    Assert.That(Cell(list, main, "Base"), Is.EqualTo("-5"));
    Assert.That(Cell(list, main, "Policy"), Is.EqualTo("SCHED_OTHER"));
    Assert.That(Cell(list, main, "Affinity"), Is.EqualTo("0-3"));
    Assert.That(Cell(list, main, "Vol / invol"), Is.EqualTo("90 / 900"));
    Assert.That(Cell(list, realtime, "Policy"), Is.EqualTo("SCHED_FIFO"));
  }

  /// <summary>
  /// A reading nobody could take shows the reason rather than a number. "n/a" beside a real total is
  /// the honest cell for a kernel that counts switches but does not split them (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnreadableCountShowsAPlaceholderAndNotZero() {
    var (_, list) = Threads(1001);
    var noSchedStats = Row(list, "1017");
    var vanished = Row(list, "1027");

    Assert.That(Cell(list, noSchedStats, "Vol / invol"), Is.EqualTo("n/a / n/a"));
    Assert.That(Cell(list, noSchedStats, "Ctx switches"), Is.EqualTo("n/a"));
    Assert.That(Cell(list, vanished, "Ctx switches"), Is.EqualTo("×"), "the thread ended, it did not switch zero times");
    Assert.That(Cell(list, vanished, "Affinity"), Is.EqualTo("—"));
  }

}
