using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>Every column the process list can show.</summary>
public enum DesktopColumn : byte {
  Name = 0,
  Pid,
  PidHex,
  ParentPid,
  User,
  State,
  CpuPercent,
  CpuPerCore,
  CpuTime,
  CyclesDelta,
  ContextSwitchDelta,
  CpuHistory,
  PrivateBytes,
  PrivateBytesDelta,
  PrivateWorkingSet,
  MemoryHistory,
  WorkingSet,
  PeakWorkingSet,
  VirtualBytes,
  PeakVirtualBytes,
  PagedPool,
  PeakPagedPool,
  NonPagedPool,
  PeakNonPagedPool,
  PageFaultDelta,
  Swap,
  IoTotalRate,
  ReadRate,
  WriteRate,
  IoHistory,
  Threads,
  Handles,
  Priority,
  Session,
  Started,
  Container,
  ImagePath,
  CommandLine,
}

/// <summary>
/// What each column is: its header, its width, whether it is a graph, and how to sort by it.
/// </summary>
/// <remarks>
/// The three history columns are the reason the whole set is data rather than a hard-coded list of
/// <c>Columns.Add</c> calls — they are drawn rather than written, they need a series from
/// <see cref="ProcessHistory"/>, and they have no text to sort by.
/// </remarks>
public readonly record struct ColumnInfo(
  DesktopColumn Column,
  string Header,
  int Width,
  bool RightAligned,
  ProcessColumn? SortBy,
  HistorySeries? Series
);

public static class ColumnSet {

  /// <summary>
  /// Every column, in the order the chooser lists them.
  /// </summary>
  /// <remarks>
  /// A column exists here when the engine can actually fill it on at least one platform. Where the
  /// other platform cannot, the cell says which reason applies rather than showing a zero (§3.4) —
  /// cycles and the pool quotas are Windows-only, and read as <c>n/a</c> on Linux.
  /// </remarks>
  public static readonly ColumnInfo[] All = [
    new(DesktopColumn.Name, "Process", 260, false, ProcessColumn.Name, null),
    new(DesktopColumn.Pid, "PID", 70, true, ProcessColumn.Pid, null),
    new(DesktopColumn.PidHex, "PID (hex)", 78, true, ProcessColumn.Pid, null),
    new(DesktopColumn.ParentPid, "Parent PID", 88, true, ProcessColumn.ParentPid, null),
    new(DesktopColumn.User, "User", 130, false, ProcessColumn.UserName, null),
    new(DesktopColumn.State, "State", 62, false, ProcessColumn.State, null),
    // Wide enough for the header *and* its sort arrow: at 62 the caption clipped to "PU %".
    new(DesktopColumn.CpuPercent, "CPU %", 78, true, ProcessColumn.CpuPercent, null),
    new(DesktopColumn.CpuPerCore, "CPU % (per core)", 118, true, ProcessColumn.CpuPercent, null),
    new(DesktopColumn.CpuTime, "CPU time", 88, true, ProcessColumn.CpuPercent, null),
    new(DesktopColumn.CyclesDelta, "Cycles delta", 100, true, null, null),
    new(DesktopColumn.ContextSwitchDelta, "Ctx switch delta", 116, true, null, null),
    new(DesktopColumn.CpuHistory, "CPU history", 90, false, null, HistorySeries.Cpu),
    new(DesktopColumn.PrivateBytes, "Private bytes", 96, true, ProcessColumn.PrivateBytes, null),
    new(DesktopColumn.PrivateBytesDelta, "Private delta", 100, true, null, null),
    new(DesktopColumn.PrivateWorkingSet, "Private WS", 88, true, null, null),
    new(DesktopColumn.MemoryHistory, "Memory history", 90, false, null, HistorySeries.Memory),
    new(DesktopColumn.WorkingSet, "Working set", 92, true, ProcessColumn.WorkingSetBytes, null),
    new(DesktopColumn.PeakWorkingSet, "Peak WS", 84, true, null, null),
    new(DesktopColumn.VirtualBytes, "Virtual size", 92, true, ProcessColumn.VirtualBytes, null),
    new(DesktopColumn.PeakVirtualBytes, "Peak virtual", 96, true, null, null),
    new(DesktopColumn.PagedPool, "Paged pool", 90, true, null, null),
    new(DesktopColumn.PeakPagedPool, "Peak paged pool", 116, true, null, null),
    new(DesktopColumn.NonPagedPool, "Non-paged pool", 110, true, null, null),
    new(DesktopColumn.PeakNonPagedPool, "Peak non-paged", 112, true, null, null),
    new(DesktopColumn.PageFaultDelta, "Page fault delta", 116, true, null, null),
    new(DesktopColumn.Swap, "Swap", 78, true, null, null),
    new(DesktopColumn.IoTotalRate, "I/O total rate", 104, true, null, null),
    new(DesktopColumn.ReadRate, "I/O read rate", 100, true, ProcessColumn.ReadBytesPerSecond, null),
    new(DesktopColumn.WriteRate, "I/O write rate", 104, true, ProcessColumn.WriteBytesPerSecond, null),
    new(DesktopColumn.IoHistory, "I/O history", 90, false, null, HistorySeries.Io),
    new(DesktopColumn.Threads, "Threads", 64, true, ProcessColumn.ThreadCount, null),
    new(DesktopColumn.Handles, "Handles", 66, true, ProcessColumn.HandleCount, null),
    new(DesktopColumn.Priority, "Priority", 74, true, ProcessColumn.Priority, null),
    new(DesktopColumn.Session, "Session", 74, true, ProcessColumn.SessionId, null),
    new(DesktopColumn.Started, "Start time", 140, false, ProcessColumn.StartTime, null),
    new(DesktopColumn.Container, "Container / cgroup", 240, false, null, null),
    new(DesktopColumn.ImagePath, "Image path", 320, false, null, null),
    new(DesktopColumn.CommandLine, "Command line", 420, false, ProcessColumn.CommandLine, null),
  ];

  /// <summary>
  /// What the window opens with: the Process Explorer set plus the three graphs, which are the point
  /// of having them.
  /// </summary>
  public static readonly DesktopColumn[] Default = [
    DesktopColumn.Name,
    DesktopColumn.Pid,
    DesktopColumn.User,
    DesktopColumn.CpuPercent,
    DesktopColumn.CpuHistory,
    DesktopColumn.PrivateBytes,
    DesktopColumn.MemoryHistory,
    DesktopColumn.IoHistory,
    DesktopColumn.Threads,
  ];

  public static ColumnInfo Info(DesktopColumn column) {
    foreach (var info in All)
      if (info.Column == column)
        return info;

    return All[0];
  }

}
