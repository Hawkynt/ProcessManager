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
    this._group = group;
  }

  private ProcessGroup _group;

  public string Label { get; }

  public int Count => this._group.Count;

  /// <summary>
  /// Takes a fresh heading, so one kept across samples is never a stale number.
  /// </summary>
  /// <remarks>
  /// The whole heading and not only its count, because the sums under it move every sample where the
  /// count does not — a group of twelve Chrome processes is twelve of them all afternoon and its
  /// processor total is a different number every second (PRD §82).
  /// </remarks>
  public void Update(ProcessGroup group) => this._group = group;

  /// <summary>The sample this heading was last seen in; older ones leave the tree.</summary>
  public int Generation { get; set; }

  /// <summary>
  /// What the heading reads, in the first column.
  /// </summary>
  /// <remarks>
  /// Worded in Core, so this and the terminal cannot come to describe a group differently and neither
  /// can present an aggregate as anything but an aggregate (PRD §58, §82).
  /// </remarks>
  public string Text => this._group.Describe();

}
