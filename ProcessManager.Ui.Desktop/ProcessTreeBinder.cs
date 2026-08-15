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
  private readonly Dictionary<ProcessKey, ProcessRow> _rows = [];
  private readonly Dictionary<int, ProcessKey> _byPid = [];
  private readonly List<ProcessKey> _stale = [];
  private int _generation;

  public ProcessTreeBinder(TreeListView tree) {
    ArgumentNullException.ThrowIfNull(tree);
    this._tree = tree;
  }

  /// <summary>Handle counts filled on demand for the visible rows (PRD §3.5).</summary>
  public Dictionary<ProcessKey, Counter> HandleCounts { get; } = [];

  /// <summary>The row behind the selected node, or null.</summary>
  public ProcessRow? SelectedRow => this._tree.SelectedNode?.Tag as ProcessRow;

  public void Sync(SystemSnapshot snapshot, SnapshotDelta delta, ProcessView view) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(view);

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
      row.Update(in process, delta, index, handles);
      row.Generation = this._generation;

      if (!this._nodes.TryGetValue(key, out var node)) {
        node = new(row.Label) { Tag = row };
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

      if (ReferenceEquals(node.Parent, desiredParent))
        continue;

      node.Parent?.Nodes.Remove(node);
      if (this._tree.Nodes.Contains(node))
        this._tree.Nodes.Remove(node);

      if (desiredParent is null)
        this._tree.Nodes.Add(node);
      else {
        desiredParent.Nodes.Add(node);
        // A new child under a collapsed parent would be invisible with no hint that anything
        // happened; expanding the parent is how Process Explorer shows a process that just forked.
        if (!desiredParent.IsExpanded)
          desiredParent.Expand();
      }
    }

    this.RemoveStale();
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
        }

        node.Parent?.Nodes.Remove(node);
        if (this._tree.Nodes.Contains(node))
          this._tree.Nodes.Remove(node);
      }

      this._rows.Remove(key);
      this.HandleCounts.Remove(key);
    }
  }

}
