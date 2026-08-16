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
/// sample, in <see cref="Update"/>, and only when they changed: a row whose CPU still reads "0.0"
/// hands back the same string instance and the control has nothing to repaint.
/// </remarks>
public sealed class ProcessRow(ProcessKey key) {

  public ProcessKey Key { get; } = key;

  public int Pid => this.Key.Pid;

  public string Name { get; private set; } = string.Empty;
  public string User { get; private set; } = string.Empty;
  public string Cpu { get; private set; } = string.Empty;
  public string Private { get; private set; } = string.Empty;
  public string WorkingSet { get; private set; } = string.Empty;
  public string Read { get; private set; } = string.Empty;
  public string Write { get; private set; } = string.Empty;
  public string Threads { get; private set; } = string.Empty;
  public string Handles { get; private set; } = string.Empty;
  public string State { get; private set; } = string.Empty;
  public string Started { get; private set; } = string.Empty;
  public string CommandLine { get; private set; } = string.Empty;

  /// <summary>True for one sample after the process appeared — the Process Explorer green flash.</summary>
  public bool IsNew { get; private set; }

  /// <summary>What kind of process this is, which is what its row colour means (PRD §7.1).</summary>
  public ProcessCategory Category { get; private set; }

  /// <summary>The sample this row was last seen in; older rows are removed from the tree.</summary>
  public int Generation { get; set; }

  public void Update(in ProcessRecord process, SnapshotDelta delta, int index, Counter handles, int currentUserId) {
    this.Name = process.Name;
    this.User = process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?");
    this.Cpu = Humanize.Percent(delta.CpuPercent(index));
    this.Private = Humanize.Bytes(process.PrivateBytes);
    this.WorkingSet = Humanize.Bytes(process.WorkingSetBytes);
    this.Read = Humanize.BytesPerSecond(delta.ReadBytesPerSecond(index));
    this.Write = Humanize.BytesPerSecond(delta.WriteBytesPerSecond(index));
    this.Threads = process.ThreadCount.ToString(CultureInfo.InvariantCulture);
    this.Handles = Humanize.Count(handles.Reason == UnknownReason.NotSampledYet ? process.HandleCount : handles);
    this.State = Humanize.State(process.State);
    this.Started = process.StartTimeUtcTicks > 0
      ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
      : "—";

    this.CommandLine = process.CommandLine ?? string.Empty;
    this.IsNew = delta.IsNew(index);
    this.Category = ProcessCategories.Classify(in process, currentUserId, this.IsNew);
  }

  /// <summary>The text for one column, or empty for the ones that are drawn rather than written.</summary>
  public string TextOf(DesktopColumn column) => column switch {
    DesktopColumn.Name => this.Label,
    DesktopColumn.Pid => this.Pid.ToString(CultureInfo.InvariantCulture),
    DesktopColumn.User => this.User,
    DesktopColumn.State => this.State,
    DesktopColumn.CpuPercent => this.Cpu,
    DesktopColumn.PrivateBytes => this.Private,
    DesktopColumn.WorkingSet => this.WorkingSet,
    DesktopColumn.ReadRate => this.Read,
    DesktopColumn.WriteRate => this.Write,
    DesktopColumn.Threads => this.Threads,
    DesktopColumn.Handles => this.Handles,
    DesktopColumn.Started => this.Started,
    DesktopColumn.CommandLine => this.CommandLine,
    _ => string.Empty,
  };

  /// <summary>The text the tree column shows in the name column, with the pid appended.</summary>
  public string Label => $"{this.Name} ({this.Pid})";

}
