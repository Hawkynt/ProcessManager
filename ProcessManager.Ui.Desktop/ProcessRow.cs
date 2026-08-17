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
  public string PidHex { get; private set; } = string.Empty;
  public string ParentPid { get; private set; } = string.Empty;
  public string CpuPerCore { get; private set; } = string.Empty;
  public string CpuTime { get; private set; } = string.Empty;
  public string CyclesDelta { get; private set; } = string.Empty;
  public string ContextSwitchDelta { get; private set; } = string.Empty;
  public string PrivateDelta { get; private set; } = string.Empty;
  public string PrivateWorkingSet { get; private set; } = string.Empty;
  public string PeakWorkingSet { get; private set; } = string.Empty;
  public string VirtualBytes { get; private set; } = string.Empty;
  public string PeakVirtualBytes { get; private set; } = string.Empty;
  public string PagedPool { get; private set; } = string.Empty;
  public string PeakPagedPool { get; private set; } = string.Empty;
  public string NonPagedPool { get; private set; } = string.Empty;
  public string PeakNonPagedPool { get; private set; } = string.Empty;
  public string PageFaultDelta { get; private set; } = string.Empty;
  public string Swap { get; private set; } = string.Empty;
  public string IoTotal { get; private set; } = string.Empty;
  public string Priority { get; private set; } = string.Empty;
  public string Session { get; private set; } = string.Empty;
  public string Container { get; private set; } = string.Empty;
  public string ImagePath { get; private set; } = string.Empty;

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
    this.PidHex = "0x" + process.Pid.ToString("X", CultureInfo.InvariantCulture);
    this.ParentPid = process.ParentPid > 0 ? process.ParentPid.ToString(CultureInfo.InvariantCulture) : "—";
    this.CpuPerCore = Humanize.Percent(delta.CpuPercentPerCore(index));
    this.CpuTime = Humanize.Duration(process.CpuTimeNs);
    this.CyclesDelta = Humanize.Rate(delta.CyclesPerSecond(index));
    this.ContextSwitchDelta = Humanize.Rate(delta.ContextSwitchesPerSecond(index));
    this.PrivateDelta = Humanize.SignedBytesPerSecond(delta.PrivateBytesDelta(index));
    this.PrivateWorkingSet = Humanize.Bytes(process.PrivateWorkingSetBytes);
    this.PeakWorkingSet = Humanize.Bytes(process.PeakWorkingSetBytes);
    this.VirtualBytes = Humanize.Bytes(process.VirtualBytes);
    this.PeakVirtualBytes = Humanize.Bytes(process.PeakVirtualBytes);
    this.PagedPool = Humanize.Bytes(process.PagedPoolBytes);
    this.PeakPagedPool = Humanize.Bytes(process.PeakPagedPoolBytes);
    this.NonPagedPool = Humanize.Bytes(process.NonPagedPoolBytes);
    this.PeakNonPagedPool = Humanize.Bytes(process.PeakNonPagedPoolBytes);
    this.PageFaultDelta = Humanize.Rate(delta.PageFaultsPerSecond(index));
    this.Swap = Humanize.Bytes(process.SwapBytes);
    this.IoTotal = Humanize.BytesPerSecond(delta.IoTotalBytesPerSecond(index));
    this.Priority = process.Priority.ToString(CultureInfo.InvariantCulture);
    this.Session = process.SessionId >= 0 ? process.SessionId.ToString(CultureInfo.InvariantCulture) : "—";
    this.Container = process.ContainerPath ?? "—";
    this.ImagePath = process.ImagePath ?? "—";
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
    DesktopColumn.PidHex => this.PidHex,
    DesktopColumn.ParentPid => this.ParentPid,
    DesktopColumn.CpuPerCore => this.CpuPerCore,
    DesktopColumn.CpuTime => this.CpuTime,
    DesktopColumn.CyclesDelta => this.CyclesDelta,
    DesktopColumn.ContextSwitchDelta => this.ContextSwitchDelta,
    DesktopColumn.PrivateBytesDelta => this.PrivateDelta,
    DesktopColumn.PrivateWorkingSet => this.PrivateWorkingSet,
    DesktopColumn.PeakWorkingSet => this.PeakWorkingSet,
    DesktopColumn.VirtualBytes => this.VirtualBytes,
    DesktopColumn.PeakVirtualBytes => this.PeakVirtualBytes,
    DesktopColumn.PagedPool => this.PagedPool,
    DesktopColumn.PeakPagedPool => this.PeakPagedPool,
    DesktopColumn.NonPagedPool => this.NonPagedPool,
    DesktopColumn.PeakNonPagedPool => this.PeakNonPagedPool,
    DesktopColumn.PageFaultDelta => this.PageFaultDelta,
    DesktopColumn.Swap => this.Swap,
    DesktopColumn.IoTotalRate => this.IoTotal,
    DesktopColumn.Priority => this.Priority,
    DesktopColumn.Session => this.Session,
    DesktopColumn.Container => this.Container,
    DesktopColumn.ImagePath => this.ImagePath,
    _ => string.Empty,
  };

  /// <summary>The text the tree column shows in the name column, with the pid appended.</summary>
  public string Label => $"{this.Name} ({this.Pid})";

}
