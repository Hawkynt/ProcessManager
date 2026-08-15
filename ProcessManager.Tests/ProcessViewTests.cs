using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Tree building, filtering and sorting — the part both front-ends share, so a bug here is a bug in
/// both (PRD §1.1, §7.3).
/// </summary>
[TestFixture]
public sealed class ProcessViewTests {

  [Test]
  public void AFlatViewShowsEveryProcessOnce() {
    var (snapshot, delta) = Build((1, 0), (2, 1), (3, 2));
    var view = new ProcessView { TreeMode = false, SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(3));
    Assert.That(Pids(snapshot, view), Is.EqualTo(new[] { 1, 2, 3 }));
  }

  [Test]
  public void ATreeNestsChildrenUnderParents() {
    var (snapshot, delta) = Build((1, 0), (2, 1), (3, 2), (4, 1));
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    Assert.That(Pids(snapshot, view), Is.EqualTo(new[] { 1, 2, 3, 4 }));
    Assert.That(Depths(view), Is.EqualTo(new[] { 0, 1, 2, 1 }));
    Assert.That(view.Rows[0].HasChildren, Is.True);
    Assert.That(view.Rows[2].HasChildren, Is.False);
  }

  [Test]
  public void AProcessWhoseParentIsGoneBecomesARoot() {
    // Its parent exited and it was reparented, or it lives in another pid namespace. Either way it
    // is still running and must still be listed.
    var (snapshot, delta) = Build((1, 0), (5, 999));
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(2));
    Assert.That(Depths(view), Is.EqualTo(new[] { 0, 0 }));
  }

  [Test]
  public void ACycleDoesNotHangTheWalk() {
    // Should be impossible; observed anyway across namespace boundaries. The link that closes the
    // cycle is cut, and every process still appears exactly once.
    var (snapshot, delta) = Build((10, 11), (11, 10));
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessColumn.Pid, SortDescending = false };

    Assert.That(() => view.Rebuild(snapshot, delta), Throws.Nothing);
    Assert.That(view.RowCount, Is.EqualTo(2));
  }

  [Test]
  public void AProcessThatIsItsOwnParentIsARoot() {
    var (snapshot, delta) = Build((1, 1));
    var view = new ProcessView { TreeMode = true };
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(1));
    Assert.That(view.Rows[0].Depth, Is.Zero);
  }

  [Test]
  public void FilteringInTreeModeKeepsTheAncestorsOfAMatch() {
    // Without this the filter hides the very rows it found, because their parents did not match.
    var (snapshot, delta) = Build((1, 0), (2, 1), (3, 2));
    Rename(snapshot, 3, "needle");

    var view = new ProcessView { TreeMode = true, TextFilter = "needle", SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    Assert.That(Pids(snapshot, view), Is.EqualTo(new[] { 1, 2, 3 }));
    Assert.That(view.RowCount, Is.EqualTo(3));
  }

  [Test]
  public void FilteringInFlatModeShowsOnlyTheMatches() {
    var (snapshot, delta) = Build((1, 0), (2, 1), (3, 2));
    Rename(snapshot, 3, "needle");

    var view = new ProcessView { TreeMode = false, TextFilter = "needle" };
    view.Rebuild(snapshot, delta);

    Assert.That(view.RowCount, Is.EqualTo(1));
    Assert.That(snapshot.Processes[view.Rows[0].Index].Pid, Is.EqualTo(3));
  }

  [Test]
  public void ATieIsBrokenByPidSoRowsDoNotJumpBetweenSamples() {
    // Everything below has the same sort key. If the order were not pinned, a re-sort could move a
    // row under the pointer between hover and click — which is how the wrong process gets killed.
    var (snapshot, delta) = Build((30, 0), (10, 0), (20, 0));
    var view = new ProcessView { SortColumn = ProcessColumn.ThreadCount, SortDescending = true };
    view.Rebuild(snapshot, delta);

    Assert.That(Pids(snapshot, view), Is.EqualTo(new[] { 10, 20, 30 }));
  }

  [Test]
  public void AValueThatIsNotThereSortsBelowEveryValueThatIs() {
    var (snapshot, delta) = Build((1, 0), (2, 0), (3, 0));
    SetPrivate(snapshot, 1, Counter.Of(100ul));
    SetPrivate(snapshot, 2, Counter.NotPermitted);
    SetPrivate(snapshot, 3, Counter.Of(500ul));

    var view = new ProcessView { SortColumn = ProcessColumn.PrivateBytes, SortDescending = true };
    view.Rebuild(snapshot, delta);

    Assert.That(Pids(snapshot, view), Is.EqualTo(new[] { 3, 1, 2 }));
  }

  [Test]
  public void FindRowLocatesAProcessByIdentityRatherThanByPosition() {
    var (snapshot, delta) = Build((1, 0), (2, 1), (3, 1));
    var view = new ProcessView { SortColumn = ProcessColumn.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);

    var key = snapshot.Processes[1].Key;
    Assert.That(view.FindRow(key), Is.EqualTo(1));
    Assert.That(view.FindRow(new(9999, 0)), Is.EqualTo(-1));
  }

  #region helpers

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta) Build(params (int Pid, int ParentPid)[] processes) {
    var snapshot = new SystemSnapshot { TimestampTicks = 0 };
    snapshot.System.CoreCount = 1;
    var buffer = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      buffer[i] = default;
      buffer[i].Key = new(processes[i].Pid, 1000ul);
      buffer[i].ParentPid = processes[i].ParentPid;
      buffer[i].Name = $"p{processes[i].Pid}";
      buffer[i].UserId = 1000;
      buffer[i].CpuTimeNs = Counter.Of(0ul);
      buffer[i].PrivateBytes = Counter.Of(0ul);
    }

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.PerCore);
    return (snapshot, delta);
  }

  private static void Rename(SystemSnapshot snapshot, int pid, string name) {
    var buffer = snapshot.PrepareProcesses(snapshot.ProcessCount);
    for (var i = 0; i < buffer.Length; ++i)
      if (buffer[i].Pid == pid)
        buffer[i].Name = name;
  }

  private static void SetPrivate(SystemSnapshot snapshot, int pid, Counter value) {
    var buffer = snapshot.PrepareProcesses(snapshot.ProcessCount);
    for (var i = 0; i < buffer.Length; ++i)
      if (buffer[i].Pid == pid)
        buffer[i].PrivateBytes = value;
  }

  private static int[] Pids(SystemSnapshot snapshot, ProcessView view) {
    var result = new int[view.RowCount];
    for (var i = 0; i < result.Length; ++i)
      result[i] = snapshot.Processes[view.Rows[i].Index].Pid;

    return result;
  }

  private static int[] Depths(ProcessView view) {
    var result = new int[view.RowCount];
    for (var i = 0; i < result.Length; ++i)
      result[i] = view.Rows[i].Depth;

    return result;
  }

  #endregion

}
