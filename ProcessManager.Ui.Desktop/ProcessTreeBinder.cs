using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Keeps a <see cref="TreeListView"/> in step with the snapshot, incrementally.
/// </summary>
/// <remarks>
/// <para>
/// Rebuilding the node collection every second would be simpler and is wrong twice over: it throws
/// away which branches the user expanded and which row was selected — the two pieces of state the
/// user created by hand — and it allocates a node per process per second.
/// </para>
/// <para>
/// So nodes are matched by <see cref="ProcessKey"/> and reused. A process that appeared gets a node,
/// one that exited loses its, one that was reparented moves, and everything else keeps the node it
/// had with new text in it. Expansion and selection survive because the objects do (PRD §7.3).
/// </para>
/// </remarks>
public sealed class ProcessTreeBinder {

  private readonly TreeListView _tree;
  private readonly Dictionary<ProcessKey, TreeNode> _nodes = [];
  // Where each node currently hangs: absent means "not in the tree at all", present with a null
  // value means "a root". Without this the reparenting pass below cannot tell a brand-new root node
  // (Parent null, wanted null) from one already attached at the root — and skipping it left the
  // whole window showing an empty list while the status line counted the processes it was not
  // showing.
  private readonly Dictionary<ProcessKey, TreeNode?> _attachedTo = [];
  private readonly Dictionary<ProcessKey, ProcessRow> _rows = [];
  private readonly Dictionary<int, ProcessKey> _byPid = [];
  private readonly List<ProcessKey> _stale = [];
  // Roots kept apart from children rather than under a null key: a Dictionary will not take one.
  private readonly List<TreeNode> _desiredRoots = [];
  private readonly Dictionary<TreeNode, List<TreeNode>> _desiredChildren = [];
  private int _generation;

  public ProcessTreeBinder(TreeListView tree) {
    ArgumentNullException.ThrowIfNull(tree);
    this._tree = tree;
  }

  /// <summary>Handle counts filled on demand for the visible rows (PRD §3.5).</summary>
  public Dictionary<ProcessKey, Counter> HandleCounts { get; } = [];

  /// <summary>The row for one process, or null once it has gone.</summary>
  public ProcessRow? RowFor(ProcessKey key) => this._rows.TryGetValue(key, out var row) ? row : null;

  /// <summary>The tree node a process occupies, for a caller that wants to select it.</summary>
  public TreeNode? NodeFor(ProcessKey key) => this._nodes.TryGetValue(key, out var node) ? node : null;

  /// <summary>The row behind the selected node, or null.</summary>
  public ProcessRow? SelectedRow => this._tree.SelectedNode?.Tag as ProcessRow;

  /// <summary>The uid of whoever is running this, so rows can be coloured "mine" (PRD §7.1).</summary>
  public int CurrentUserId { get; set; } = -1;

  /// <summary>
  /// Whether a subtree the user collapsed may be reopened by the program.
  /// </summary>
  /// <remarks>
  /// Off, and it is about an <em>existing</em> parent. A parent used to be expanded whenever a child
  /// appeared under it, which on a machine that forks steadily meant the tree reopening a subtree
  /// somebody had closed every second, sliding everything below it down the screen — including
  /// whatever was being read (PRD §87).
  /// <para>
  /// A node is still expanded when it is created, which is what makes the tree open showing the
  /// machine. That argues with nobody: a node that has just come into existence is one nobody has
  /// had the chance to collapse.
  /// </para>
  /// </remarks>
  public bool ExpandOnNewChild { get; set; }

  public void Sync(SystemSnapshot snapshot, SnapshotDelta delta, ProcessView view) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(view);

    // The node under the top of the viewport, and the one that is selected. Both are put back at the
    // end: the scroll position is an index into a list that is about to be rewritten, so keeping the
    // number keeps a different row (PRD §12).
    var anchor = this._tree.NodeAt(this._tree.TopIndex);

    var selected = this._tree.SelectedNode;

    ++this._generation;
    var processes = snapshot.Processes;
    var rows = view.Rows;

    this._byPid.Clear();
    for (var i = 0; i < rows.Length; ++i)
      this._byPid[processes[rows[i].Index].Pid] = processes[rows[i].Index].Key;

    // First pass: every visible process has a node with current text in it.
    for (var i = 0; i < rows.Length; ++i) {
      var index = rows[i].Index;
      ref readonly var process = ref processes[index];
      var key = process.Key;

      if (!this._rows.TryGetValue(key, out var row)) {
        row = new(key);
        this._rows[key] = row;
      }

      this.HandleCounts.TryGetValue(key, out var handles);
      row.Update(in process, delta, index, handles, this.CurrentUserId);
      row.Generation = this._generation;

      if (!this._nodes.TryGetValue(key, out var node)) {
        node = new(row.Label) { Tag = row };
        // Expanded from the moment it exists, so the tree opens showing the machine rather than two
        // roots. This is not the same as expanding a parent when a child appears under it: a node
        // being created is one nobody has had the chance to collapse, so opening it argues with
        // nobody. Reopening one somebody closed is what made the list jump (PRD §87).
        node.Expand();
        this._nodes[key] = node;
      } else if (!string.Equals(node.Text, row.Label, StringComparison.Ordinal))
        node.Text = row.Label;
    }

    // Second pass: parents. Done after the first so a child seen before its parent still finds it.
    for (var i = 0; i < rows.Length; ++i) {
      var index = rows[i].Index;
      ref readonly var process = ref processes[index];
      var node = this._nodes[process.Key];
      var desiredParent = view.TreeMode
          && this._byPid.TryGetValue(process.ParentPid, out var parentKey)
          && parentKey != process.Key
          && this._nodes.TryGetValue(parentKey, out var found)
        ? found
        : null;

      if (this._attachedTo.TryGetValue(process.Key, out var currentParent)
          && ReferenceEquals(currentParent, desiredParent))
        continue;

      if (currentParent is not null)
        currentParent.Nodes.Remove(node);
      else if (this._attachedTo.ContainsKey(process.Key))
        this._tree.Nodes.Remove(node);

      if (desiredParent is null)
        this._tree.Nodes.Add(node);
      else {
        desiredParent.Nodes.Add(node);
        // A new child under a collapsed parent is invisible, which is a real cost — but reopening a
        // subtree somebody closed, every second, is a larger one.
        if (this.ExpandOnNewChild && !desiredParent.IsExpanded)
          desiredParent.Expand();
      }

      this._attachedTo[process.Key] = desiredParent;
    }

    this.RemoveStale();
    this.ReorderToMatch(processes, rows, view);
    Restore(this._tree, anchor, selected);
  }

  /// <summary>
  /// Puts the view back where it was, by node rather than by number.
  /// </summary>
  /// <remarks>
  /// The selection first: a node that survived the rebuild is still the same object, so re-assigning
  /// it costs nothing and re-establishes it when the control dropped it. Then the scroll, last,
  /// because assigning a selection can scroll to it.
  /// <para>
  /// A node that is gone — its process exited, or its parent collapsed around it — leaves the view
  /// where the rebuild put it. There is nowhere better to be.
  /// </para>
  /// </remarks>
  private static void Restore(TreeListView tree, TreeNode? anchor, TreeNode? selected) {
    if (selected is not null && !ReferenceEquals(tree.SelectedNode, selected) && tree.RowOf(selected) >= 0)
      tree.SelectedNode = selected;

    if (anchor is null)
      return;

    var row = tree.RowOf(anchor);
    if (row >= 0)
      tree.TopIndex = row;
  }


  /// <summary>
  /// Puts each sibling group into the order the view says.
  /// </summary>
  /// <remarks>
  /// Without this the window's sort does nothing after the first frame. Nodes are reused across
  /// samples — which is what makes expansion and selection survive — so a node added on the first
  /// sample keeps its position in its parent's collection for as long as the process lives, whatever
  /// the sort says. The list came up ordered by whatever the very first sample happened to produce
  /// and stayed that way: clicking a header changed the arrow in the caption and nothing else.
  /// <para>
  /// The reorder is skipped entirely when the order already matches, which is the common case — a
  /// sort by name or pid barely moves between samples, and even a sort by CPU only swaps neighbours.
  /// Only the groups that actually changed are rewritten.
  /// </para>
  /// </remarks>
  private void ReorderToMatch(ReadOnlySpan<ProcessRecord> processes, ReadOnlySpan<ViewRow> rows, ProcessView view) {
    // The view is a depth-first walk, so a parent is always followed by its own children: collecting
    // by parent in one pass yields each sibling group already in the right order.
    this._desiredRoots.Clear();
    this._desiredChildren.Clear();
    for (var i = 0; i < rows.Length; ++i) {
      var key = processes[rows[i].Index].Key;
      if (!this._nodes.TryGetValue(key, out var node))
        continue;

      if (this._attachedTo.GetValueOrDefault(key) is not { } parent) {
        this._desiredRoots.Add(node);
        continue;
      }

      if (!this._desiredChildren.TryGetValue(parent, out var siblings)) {
        siblings = [];
        this._desiredChildren[parent] = siblings;
      }

      siblings.Add(node);
    }

    Apply(this._tree.Nodes, this._desiredRoots);
    foreach (var (parent, siblings) in this._desiredChildren)
      Apply(parent.Nodes, siblings);

    static void Apply(TreeNodeCollection collection, List<TreeNode> desired) {
      if (AlreadyInOrder(collection, desired))
        return;

      // Removed and re-added rather than sorted in place: the collection has no sort, and the node
      // objects are the same ones either way, so expansion state and the selected node survive.
      foreach (var node in desired)
        collection.Remove(node);

      foreach (var node in desired)
        collection.Add(node);
    }
  }

  private static bool AlreadyInOrder(TreeNodeCollection collection, List<TreeNode> desired) {
    if (collection.Count != desired.Count)
      return false;

    for (var i = 0; i < desired.Count; ++i)
      if (!ReferenceEquals(collection[i], desired[i]))
        return false;

    return true;
  }

  private void RemoveStale() {
    this._stale.Clear();
    foreach (var (key, row) in this._rows)
      if (row.Generation != this._generation)
        this._stale.Add(key);

    foreach (var key in this._stale) {
      if (this._nodes.Remove(key, out var node)) {
        // Children of a process that exited are reparented to the tree root rather than deleted with
        // it: on Linux they really are still running, reparented to init, and deleting them here
        // would make them vanish from a list they are still in.
        while (node.Nodes.Count > 0) {
          var child = node.Nodes[0];
          node.Nodes.Remove(child);
          this._tree.Nodes.Add(child);
          foreach (var (childKey, childNode) in this._nodes)
            if (ReferenceEquals(childNode, child)) {
              this._attachedTo[childKey] = null;
              break;
            }
        }

        if (this._attachedTo.TryGetValue(key, out var parent)) {
          if (parent is not null)
            parent.Nodes.Remove(node);
          else
            this._tree.Nodes.Remove(node);
        }
      }

      this._attachedTo.Remove(key);
      this._rows.Remove(key);
      this.HandleCounts.Remove(key);
    }
  }

}
