using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>Every column the process list can show.</summary>
public enum DesktopColumn : byte {
  Name = 0,
  Pid,
  User,
  State,
  CpuPercent,
  CpuHistory,
  PrivateBytes,
  MemoryHistory,
  WorkingSet,
  ReadRate,
  WriteRate,
  IoHistory,
  Threads,
  Handles,
  Started,
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

  /// <summary>Every column, in the order the chooser lists them.</summary>
  public static readonly ColumnInfo[] All = [
    new(DesktopColumn.Name, "Process", 260, false, ProcessColumn.Name, null),
    new(DesktopColumn.Pid, "PID", 70, true, ProcessColumn.Pid, null),
    new(DesktopColumn.User, "User", 130, false, ProcessColumn.UserName, null),
    new(DesktopColumn.State, "State", 62, false, ProcessColumn.State, null),
    // Wide enough for the header *and* its sort arrow: at 62 the caption clipped to "PU %".
    new(DesktopColumn.CpuPercent, "CPU %", 78, true, ProcessColumn.CpuPercent, null),
    new(DesktopColumn.CpuHistory, "CPU history", 90, false, null, HistorySeries.Cpu),
    new(DesktopColumn.PrivateBytes, "Private", 82, true, ProcessColumn.PrivateBytes, null),
    new(DesktopColumn.MemoryHistory, "Memory history", 90, false, null, HistorySeries.Memory),
    new(DesktopColumn.WorkingSet, "Working set", 92, true, ProcessColumn.WorkingSetBytes, null),
    new(DesktopColumn.ReadRate, "Read/s", 78, true, ProcessColumn.ReadBytesPerSecond, null),
    new(DesktopColumn.WriteRate, "Write/s", 78, true, ProcessColumn.WriteBytesPerSecond, null),
    new(DesktopColumn.IoHistory, "I/O history", 90, false, null, HistorySeries.Io),
    new(DesktopColumn.Threads, "Threads", 64, true, ProcessColumn.ThreadCount, null),
    new(DesktopColumn.Handles, "Handles", 66, true, ProcessColumn.HandleCount, null),
    new(DesktopColumn.Started, "Started", 140, false, ProcessColumn.StartTime, null),
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
