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
    var (pane, list, _) = Pane(pid);
    return (pane, list);
  }

  /// <summary>The same, keeping the probe so a test can ask for a stack as well.</summary>
  private static (DetailPane Pane, TreeListView List, LinuxProbe Probe) Pane(int pid) {
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
        return (pane, (TreeListView)tabs.TabPages[i].Controls[0], probe);
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

    Assert.That(Cell(list, main, "Prio / base"), Is.EqualTo("15 / -5"), "the effective priority and the given one");
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

  /// <summary>
  /// A rate needs two readings, and the first fill has one. "…" says so; "0.0" would say the thread
  /// used no processor at all, which nobody measured (PRD §3.4).
  /// </summary>
  [Test]
  public void TheRateColumnsSayUnsampledUntilTheSecondFill() {
    var (pane, list) = Threads(1001);
    var main = Row(list, "1001");

    Assert.That(Cell(list, main, "CPU %"), Is.EqualTo("…"));
    Assert.That(Cell(list, main, "Ctx/s"), Is.EqualTo("…"));

    // The thread tab is refilled on every tick rather than once per selection, which is what makes
    // the rates arrive at all. Against a recorded /proc the counters do not move, so the honest
    // second reading is zero — a measurement, and no longer a hole.
    pane.Refresh();
    Assert.That(Cell(list, Row(list, "1001"), "CPU %"), Is.EqualTo("0.0"));
    Assert.That(Cell(list, Row(list, "1001"), "Ctx/s"), Is.EqualTo("0"));
  }

  /// <summary>
  /// The kernel/user indicator, with the call number folded in where the machine gave one (PRD §29).
  /// </summary>
  [Test]
  public void TheModeColumnSaysWhichSideOfTheBoundaryEachThreadIsOn() {
    var (_, list) = Threads(1001);

    Assert.That(Cell(list, Row(list, "1001"), "Mode"), Is.EqualTo("user"));
    Assert.That(Cell(list, Row(list, "1007"), "Mode"), Is.EqualTo("kernel · 202"));
    Assert.That(Cell(list, Row(list, "1017"), "Mode"), Is.EqualTo("kernel"), "in the kernel, not in a call");
    Assert.That(Cell(list, Row(list, "1027"), "Mode"), Is.EqualTo("n/a"), "and never guessed");
  }

  /// <summary>
  /// The columns §29 added, in the cells a reader sees. An address that was refused must never render
  /// as <c>0x0</c>, which is what the old <c>ulong</c> start address did for every thread on Linux.
  /// </summary>
  [Test]
  public void TheAddressColumnsShowAPlaceMoreOftenThanANumberAndNeverAZero() {
    var (_, list) = Threads(1001);

    Assert.That(Cell(list, Row(list, "1007"), "Instruction"), Is.EqualTo("0x7f1000012345"));
    Assert.That(Cell(list, Row(list, "1007"), "Stack"), Is.EqualTo("8.0K"));
    Assert.That(Cell(list, Row(list, "1001"), "Stack"), Is.EqualTo("n/a"), "a running thread has no register snapshot");

    foreach (var tid in (string[])["1001", "1007", "1017", "1027"])
      Assert.That(Cell(list, Row(list, tid), "Start address"), Is.Not.EqualTo("0x0"), $"tid {tid}");

    Assert.That(Cell(list, Row(list, "1007"), "Start address"), Is.EqualTo("n/a"), "Linux records no entry point for it");
    Assert.That(Cell(list, Row(list, "1001"), "Start address"), Is.EqualTo("—"), "the fixture has no readable exe link");
  }

  [Test]
  public void TheQueuedColumnIsUnknownWhereTheSchedulerStatisticsAreOff() {
    var (_, list) = Threads(1001);

    Assert.That(Cell(list, Row(list, "1001"), "Queued"), Is.EqualTo("0:04"), "seconds keep the h:mm:ss the other durations use");
    // A hundred and twenty milliseconds of waiting is what this column exists to show, and "0:00" is
    // what it looked like before it had a scale of its own.
    Assert.That(Cell(list, Row(list, "1007"), "Queued"), Is.EqualTo("120 ms"));
    Assert.That(Cell(list, Row(list, "1017"), "Queued"), Is.EqualTo("n/a"));
  }

  /// <summary>
  /// The list is refilled every tick. A refill that cleared the selection would take the row out from
  /// under whoever was about to right-click it, once a second, forever.
  /// </summary>
  [Test]
  public void RefillingTheListKeepsTheSelectedThreadSelected() {
    var (pane, list) = Threads(1001);
    list.SelectedNode = list.Nodes[2];
    var selected = list.SelectedNode!.Text;

    pane.Refresh();

    Assert.That(list.SelectedNode, Is.Not.Null);
    Assert.That(list.SelectedNode!.Text, Is.EqualTo(selected));
  }

  /// <summary>
  /// The stack viewer's own list, filled from the same recorded <c>/proc</c> — every column of every
  /// row, for the reason this fixture's class comment gives (PRD §30).
  /// </summary>
  [Test]
  public void EveryColumnOfEveryStackFrameRenders() {
    var (_, _, probe) = Pane(1001);
    var window = new StackWindow(probe, new(1001, 100500), 1007, "worker");
    window.Reload(resolveSymbols: false);

    var frames = (TreeListView)window.Controls[0];
    Assert.That(frames.Nodes, Has.Count.EqualTo(7), "six kernel frames and the one user frame");
    for (var row = 0; row < frames.Nodes.Count; ++row)
      for (var column = 0; column < frames.Columns.Count; ++column)
        Assert.That(
          frames.Columns[column].TextSelector!(frames.Nodes[row]),
          Is.Not.Null,
          $"row {row}, column '{frames.Columns[column].Text}'"
        );

    Assert.That(window.Description, Does.Contain("6 frame(s) read"));
    Assert.That(window.Description, Does.Contain("not unwound"));
  }

  /// <summary>
  /// A thread whose stack could not be taken shows one row saying why, not an empty list — the two
  /// look identical otherwise, and only one of them is a thing that happens (PRD §1.5).
  /// </summary>
  [Test]
  public void AStackThatCouldNotBeTakenFillsEveryColumnOfItsOneRow() {
    var (_, _, probe) = Pane(1001);
    var window = new StackWindow(probe, new(1001, 100500), 1001);
    window.Reload(resolveSymbols: false);

    var frames = (TreeListView)window.Controls[0];
    Assert.That(frames.Nodes, Has.Count.EqualTo(1));
    for (var column = 0; column < frames.Columns.Count; ++column)
      Assert.That(frames.Columns[column].TextSelector!(frames.Nodes[0]), Is.Not.Null);
  }

}
