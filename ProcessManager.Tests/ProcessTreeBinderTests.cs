using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Desktop;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The desktop tree binder, without a display.
/// </summary>
/// <remarks>
/// Written after the window shipped showing an empty list while its status line counted the
/// processes it was not showing. The cause was one line: a brand-new root node has a null parent and
/// wants a null parent, so the "has its parent changed?" check said no and never attached it. Every
/// test here would have failed on that.
/// </remarks>
[TestFixture]
public sealed class ProcessTreeBinderTests {

  [Test]
  public void EveryProcessGetsANode() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (2, 1), (3, 1));

    binder.Sync(snapshot, delta, view);

    Assert.That(tree.Nodes.Count, Is.EqualTo(1), "one root");
    Assert.That(CountNodes(tree), Is.EqualTo(3), "every process is in the tree exactly once");
  }

  [Test]
  public void AFlatViewPutsEveryProcessAtTheRoot() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (2, 1), (3, 1));
    view.TreeMode = false;
    view.Rebuild(snapshot, delta);

    binder.Sync(snapshot, delta, view);

    Assert.That(tree.Nodes.Count, Is.EqualTo(3));
  }

  [Test]
  public void SyncingTwiceDoesNotDuplicateAnything() {
    // The binder is incremental; the second call must be a no-op rather than a second tree.
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (2, 1));

    binder.Sync(snapshot, delta, view);
    binder.Sync(snapshot, delta, view);

    Assert.That(CountNodes(tree), Is.EqualTo(2));
  }

  [Test]
  public void TheSameNodeObjectSurvivesASample() {
    // Node identity is what carries expansion state and selection across a refresh (PRD §7.3).
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (2, 1));

    binder.Sync(snapshot, delta, view);
    var before = tree.Nodes[0];
    binder.Sync(snapshot, delta, view);

    Assert.That(tree.Nodes[0], Is.SameAs(before));
  }

  [Test]
  public void AProcessThatExitedLosesItsNode() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (first, firstDelta, firstView) = Build((1, 0), (2, 1));
    binder.Sync(first, firstDelta, firstView);
    Assert.That(CountNodes(tree), Is.EqualTo(2));

    var (second, secondDelta, secondView) = Build((1, 0));
    binder.Sync(second, secondDelta, secondView);

    Assert.That(CountNodes(tree), Is.EqualTo(1));
  }

  [Test]
  public void ChildrenOfAnExitedProcessAreKeptAtTheRoot() {
    // They are still running — reparented to init — so deleting them with their parent would remove
    // rows for processes that are very much alive.
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (first, firstDelta, firstView) = Build((1, 0), (2, 1), (3, 2));
    binder.Sync(first, firstDelta, firstView);

    // 2 exits; 3 remains and now reports 1 as its parent.
    var (second, secondDelta, secondView) = Build((1, 0), (3, 1));
    binder.Sync(second, secondDelta, secondView);

    Assert.That(CountNodes(tree), Is.EqualTo(2));
  }

  [Test]
  public void SwitchingToFlatModeMovesChildrenToTheRoot() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (2, 1));
    binder.Sync(snapshot, delta, view);
    Assert.That(tree.Nodes.Count, Is.EqualTo(1));

    view.TreeMode = false;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);

    Assert.That(tree.Nodes.Count, Is.EqualTo(2), "both are roots now");
    Assert.That(CountNodes(tree), Is.EqualTo(2), "and neither was duplicated");
  }

  [Test]
  public void TheTreeIsReorderedToMatchTheSort() {
    // The bug this pins: nodes are reused across samples so that expansion and selection survive,
    // and a node added on the first sample kept its position for the life of the process. Clicking a
    // header changed the arrow in the caption and nothing else — the window's sort did nothing at
    // all after the first frame.
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((10, 0), (20, 0), (30, 0));

    view.SortColumn = ProcessField.Pid;
    view.SortDescending = false;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);
    Assert.That(Pids(tree), Is.EqualTo(new[] { 10, 20, 30 }));

    view.SortDescending = true;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);

    Assert.That(Pids(tree), Is.EqualTo(new[] { 30, 20, 10 }), "the same nodes, in the new order");
  }

  [Test]
  public void ReorderingKeepsTheSameNodeObjects() {
    // Which is what carries expansion state and the selection across the reorder.
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((10, 0), (20, 0));
    view.SortColumn = ProcessField.Pid;
    view.SortDescending = false;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);

    var first = tree.Nodes[0];
    var second = tree.Nodes[1];

    view.SortDescending = true;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);

    Assert.That(tree.Nodes[0], Is.SameAs(second));
    Assert.That(tree.Nodes[1], Is.SameAs(first));
  }

  [Test]
  public void ChildrenAreOrderedWithinTheirParent() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);
    var (snapshot, delta, view) = Build((1, 0), (10, 1), (20, 1), (30, 1));
    view.SortColumn = ProcessField.Pid;
    view.SortDescending = true;
    view.Rebuild(snapshot, delta);
    binder.Sync(snapshot, delta, view);

    var root = tree.Nodes[0];
    var children = new List<int>();
    foreach (TreeNode child in root.Nodes)
      children.Add(((ProcessRow)child.Tag!).Pid);

    Assert.That(children, Is.EqualTo(new[] { 30, 20, 10 }));
  }

  private static int[] Pids(TreeListView tree) {
    var result = new List<int>();
    foreach (TreeNode node in tree.Nodes)
      result.Add(((ProcessRow)node.Tag!).Pid);

    return [.. result];
  }

  private static int CountNodes(TreeListView tree) {
    var count = 0;
    foreach (TreeNode node in tree.Nodes)
      count += Count(node);

    return count;

    static int Count(TreeNode node) {
      var total = 1;
      foreach (TreeNode child in node.Nodes)
        total += Count(child);

      return total;
    }
  }

  #region holding the view still

  /// <summary>
  /// The complaint the anchoring was written for: the list scrolled away under whoever was reading
  /// it. A row's index is not a place — twenty processes exiting above the viewport slides twenty
  /// rows of different content under the same number, once a second (PRD §12).
  /// </summary>
  [Test]
  public void ProcessesExitingAboveTheViewportDoNotSlideTheListUnderTheReader() {
    var tree = new TreeListView { Bounds = new(0, 0, 400, 100) };
    var binder = new ProcessTreeBinder(tree);

    var all = new (int, int)[30];
    for (var i = 0; i < all.Length; ++i)
      all[i] = (i + 1, 0);

    var (snapshot, delta, view) = Build(all);
    binder.Sync(snapshot, delta, view);

    tree.TopIndex = 20;
    var watched = tree.NodeAt(20);
    Assert.That(watched, Is.Not.Null);

    // Ten of the processes above it exit.
    var survivors = new (int, int)[20];
    for (var i = 0; i < survivors.Length; ++i)
      survivors[i] = (i + 11, 0);

    var (after, afterDelta, afterView) = Build(survivors);
    binder.Sync(after, afterDelta, afterView);

    Assert.That(tree.NodeAt(tree.TopIndex), Is.SameAs(watched), "the same row is still under the reader's eyes");
    Assert.That(tree.TopIndex, Is.EqualTo(10), "even though it is ten rows further up the list");
  }

  [Test]
  public void TheSelectionSurvivesARebuildThatMovesIt() {
    var tree = new TreeListView { Bounds = new(0, 0, 400, 400) };
    var binder = new ProcessTreeBinder(tree);

    var (snapshot, delta, view) = Build((1, 0), (2, 0), (3, 0), (4, 0));
    binder.Sync(snapshot, delta, view);

    var chosen = tree.NodeAt(3);
    tree.SelectedNode = chosen;

    var (after, afterDelta, afterView) = Build((3, 0), (4, 0));
    binder.Sync(after, afterDelta, afterView);

    Assert.That(tree.SelectedNode, Is.SameAs(chosen));
  }

  /// <summary>
  /// A watched process exiting leaves the view wherever the rebuild put it, because there is nowhere
  /// better for it to be — and, above all, without throwing.
  /// </summary>
  [Test]
  public void AnAnchorThatExitsIsNotAnError() {
    var tree = new TreeListView { Bounds = new(0, 0, 400, 100) };
    var binder = new ProcessTreeBinder(tree);

    var (snapshot, delta, view) = Build((1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (6, 0), (7, 0), (8, 0));
    binder.Sync(snapshot, delta, view);
    tree.TopIndex = 5;
    tree.SelectedNode = tree.NodeAt(5);

    var (after, afterDelta, afterView) = Build((1, 0), (2, 0));
    Assert.That(() => binder.Sync(after, afterDelta, afterView), Throws.Nothing);
    Assert.That(tree.TopIndex, Is.InRange(0, 1), "clamped into a list that is now two rows long");
    Assert.That(CountNodes(tree), Is.EqualTo(2), "and the rebuild itself still happened");
  }

  /// <summary>
  /// A subtree somebody collapsed stays collapsed. It used to reopen whenever a child appeared under
  /// it, which on a machine that forks steadily is every second, sliding everything below it down the
  /// screen (PRD §87).
  /// </summary>
  [Test]
  public void ANewChildDoesNotReopenASubtreeSomebodyClosed() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree);

    var (snapshot, delta, view) = Build((1, 0), (2, 1));
    binder.Sync(snapshot, delta, view);
    tree.Nodes[0].Collapse();

    var (after, afterDelta, afterView) = Build((1, 0), (2, 1), (3, 1));
    binder.Sync(after, afterDelta, afterView);

    Assert.That(tree.Nodes[0].IsExpanded, Is.False);
    Assert.That(CountNodes(tree), Is.EqualTo(3), "the child is there, it is just not shown");
  }

  /// <summary>The old behaviour is still available, for anybody who wants it.</summary>
  [Test]
  public void ExpandOnNewChildRestoresTheOldBehaviour() {
    var tree = new TreeListView();
    var binder = new ProcessTreeBinder(tree) { ExpandOnNewChild = true };

    var (snapshot, delta, view) = Build((1, 0), (2, 1));
    binder.Sync(snapshot, delta, view);
    tree.Nodes[0].Collapse();

    var (after, afterDelta, afterView) = Build((1, 0), (2, 1), (3, 1));
    binder.Sync(after, afterDelta, afterView);

    Assert.That(tree.Nodes[0].IsExpanded, Is.True);
  }

  #endregion

  private static (SystemSnapshot Snapshot, SnapshotDelta Delta, ProcessView View) Build(
    params (int Pid, int ParentPid)[] processes
  ) {
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
    }

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    var view = new ProcessView { TreeMode = true, SortColumn = ProcessField.Pid, SortDescending = false };
    view.Rebuild(snapshot, delta);
    return (snapshot, delta, view);
  }

}
