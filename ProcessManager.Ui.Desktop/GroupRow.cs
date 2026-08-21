using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A heading row in the process list (PRD §83).
/// </summary>
/// <remarks>
/// <para>
/// A separate type from <see cref="ProcessRow"/> and not a flag on it, which is the whole safeguard.
/// Everything that acts on a row in this window reaches it through
/// <c>SelectedNode.Tag as ProcessRow</c>; a heading's tag is one of these, so every such cast comes
/// back null and every action — end, suspend, restart, properties — declines because it has nothing
/// to act on. Ending a heading is impossible rather than merely discouraged.
/// </para>
/// <para>
/// The count is the group's whole membership, not what is on screen: a folded heading still says how
/// much it is hiding.
/// </para>
/// </remarks>
public sealed class GroupRow {

  public GroupRow(ProcessGroup group) {
    this.Label = group.Label;
    this.Count = group.Count;
  }

  public string Label { get; }

  public int Count { get; private set; }

  /// <summary>Takes a fresh count, so a heading kept across samples is never a stale number.</summary>
  public void Update(ProcessGroup group) => this.Count = group.Count;

  /// <summary>The sample this heading was last seen in; older ones leave the tree.</summary>
  public int Generation { get; set; }

  /// <summary>What the heading reads, in the first column.</summary>
  public string Text => $"{this.Label}  ({this.Count} process{(this.Count == 1 ? string.Empty : "es")})";

}
