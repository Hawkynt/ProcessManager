using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What one row shows, already formatted.
/// </summary>
/// <remarks>
/// The tree's column selectors run on every paint, several times per row — so they read strings that
/// are already made rather than formatting a number each time. The strings are refreshed once per
/// sample, in <see cref="Update"/>.
/// <para>
/// One array indexed by <see cref="ProcessField"/> rather than a property per column: there are
/// thirty-eight of them, a property each meant a switch to match, and the switch is exactly the kind
/// of thing that silently loses a field when the thirty-ninth is added (PRD §5.1).
/// </para>
/// </remarks>
public sealed class ProcessRow(ProcessKey key) {

  private static readonly int _slots = CountSlots();

  private readonly string[] _text = new string[_slots];

  /// <summary>Indexed by the enum value, so the array must be as long as the largest one plus one.</summary>
  private static int CountSlots() {
    var highest = 0;
    foreach (var descriptor in FieldRegistry.All)
      highest = Math.Max(highest, (int)descriptor.Id);

    return highest + 1;
  }

  public ProcessKey Key { get; } = key;

  public int Pid => this.Key.Pid;

  /// <summary>True for one sample after the process appeared — the Process Explorer green flash.</summary>
  public bool IsNew { get; private set; }

  /// <summary>What kind of process this is, which is what its row colour means (PRD §7.1).</summary>
  public ProcessCategory Category { get; private set; }

  /// <summary>The sample this row was last seen in; older rows are removed from the tree.</summary>
  public int Generation { get; set; }

  public void Update(in ProcessRecord process, SnapshotDelta delta, int index, Counter handles, int currentUserId) {
    foreach (var descriptor in FieldRegistry.All) {
      if (descriptor.IsGraph)
        continue;

      this._text[(int)descriptor.Id] = FieldAccessor.Text(descriptor.Id, in process, delta, index);
    }

    // Two exceptions to the shared formatter, both because the window knows something the engine
    // does not. The handle count is sampled on its own schedule because it is expensive (PRD §5.4),
    // so the row prefers the freshly measured one and falls back to the snapshot's; and a missing
    // user name falls back to the numeric id, which is more use in a window than a dash.
    if (handles.Reason != UnknownReason.NotSampledYet)
      this._text[(int)ProcessField.HandleCount] = Humanize.Count(handles);

    if (process.UserName is null)
      this._text[(int)ProcessField.UserName] =
        process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?";

    this.Name = process.Name;
    this.IsNew = delta.IsNew(index);
    this.Category = ProcessCategories.Classify(in process, currentUserId, this.IsNew);
  }

  /// <summary>The text for one column, or empty for the ones that are drawn rather than written.</summary>
  public string TextOf(ProcessField field) {
    // The name column carries the pid alongside the name, which is a window convention rather than a
    // property of the field, so it does not belong in the shared accessor.
    if (field == ProcessField.Name)
      return this.Label;

    var index = (int)field;
    return (uint)index < (uint)this._text.Length ? this._text[index] ?? string.Empty : string.Empty;
  }

  public string Name { get; private set; } = string.Empty;

  public string User => this.TextOf(ProcessField.UserName);
  public string Cpu => this.TextOf(ProcessField.CpuPercent);
  public string Private => this.TextOf(ProcessField.PrivateBytes);
  public string WorkingSet => this.TextOf(ProcessField.WorkingSetBytes);
  public string Read => this.TextOf(ProcessField.ReadBytesPerSecond);
  public string Write => this.TextOf(ProcessField.WriteBytesPerSecond);
  public string Threads => this.TextOf(ProcessField.ThreadCount);
  public string Handles => this.TextOf(ProcessField.HandleCount);
  public string State => this.TextOf(ProcessField.State);
  public string Started => this.TextOf(ProcessField.StartTime);
  public string CommandLine => this.TextOf(ProcessField.CommandLine);

  /// <summary>
  /// What the tree column shows.
  /// </summary>
  /// <remarks>
  /// The name alone. It used to carry the pid as well, which put the same number on the row twice
  /// whenever the PID column was visible — and it is visible by default, as it is in every tool
  /// this imitates (PRD §93).
  /// </remarks>
  public string Label => this.Name;

}
