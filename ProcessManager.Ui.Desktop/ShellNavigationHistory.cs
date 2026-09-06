using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One location in the desktop shell's session history.
/// </summary>
/// <remarks>
/// A process is identified by its complete <see cref="ProcessKey"/>. A pid alone is deliberately
/// not stored here: after a process exits the operating system may reuse that pid, and replaying a
/// history entry must never select the new process by accident.
/// </remarks>
internal readonly record struct ShellLocation(string View, ProcessKey? Process = null);

/// <summary>
/// Browser-shaped chronological history for the desktop shell.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Push"/> is the equivalent of entering a new location: it appends after the current
/// entry and discards a forward branch when navigation had first gone back. <see cref="Replace"/>
/// changes only the state attached to the current location. The latter is what process-row
/// selection uses, so pressing Down twenty times does not turn Back into twenty undo steps.
/// </para>
/// <para>
/// The history is intentionally independent of the breadcrumb. History answers "where did I go?";
/// a breadcrumb answers "where am I in the hierarchy?". Keeping those as separate models prevents
/// a visited-list from accidentally being rendered as structural navigation.
/// </para>
/// </remarks>
internal sealed class ShellNavigationHistory {
  private readonly List<ShellLocation> _entries = [];
  private int _index = -1;

  public int Count => this._entries.Count;
  public bool CanGoBack => this._index > 0;
  public bool CanGoForward => this._index >= 0 && this._index + 1 < this._entries.Count;

  public ShellLocation? Current
    => this._index >= 0 && this._index < this._entries.Count
      ? this._entries[this._index]
      : null;

  /// <summary>Adds a newly visited location, unless it is already current.</summary>
  public void Push(ShellLocation location) {
    if (this.Current == location)
      return;

    if (this._index + 1 < this._entries.Count)
      this._entries.RemoveRange(this._index + 1, this._entries.Count - this._index - 1);

    this._entries.Add(location);
    this._index = this._entries.Count - 1;
  }

  /// <summary>
  /// Replaces state belonging to the current chronological location without adding another visit.
  /// </summary>
  public void Replace(ShellLocation location) {
    if (this._index < 0) {
      this.Push(location);
      return;
    }

    this._entries[this._index] = location;
  }

  public ShellLocation? Back() => this.Move(-1);
  public ShellLocation? Forward() => this.Move(1);

  private ShellLocation? Move(int delta) {
    var target = this._index + delta;
    if (target < 0 || target >= this._entries.Count)
      return null;

    this._index = target;
    return this._entries[target];
  }
}

/// <summary>The structural path shown for the current shell location.</summary>
internal readonly record struct ShellBreadcrumb(string Root, string? Leaf = null) {
  public bool HasAncestor => !string.IsNullOrEmpty(this.Leaf);

  public override string ToString()
    => string.IsNullOrEmpty(this.Leaf)
      ? this.Root
      : $"{this.Root} › {this.Leaf}";

  public static ShellBreadcrumb For(ShellLocation location, string? selectedProcessName = null) {
    if (!string.Equals(location.View, "Processes", StringComparison.Ordinal)
      || location.Process is not { } process)
      return new(location.View);

    var name = string.IsNullOrWhiteSpace(selectedProcessName) ? "Process" : selectedProcessName.Trim();
    return new("Processes", $"{name} ({process.Pid})");
  }
}
