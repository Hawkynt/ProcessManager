using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// Which columns the terminal shows and how wide they are.
/// </summary>
/// <remarks>
/// Fixed widths rather than measured ones. A column that resizes itself to its widest value jitters
/// every second as processes come and go, and a table whose columns move is harder to read than one
/// whose values are occasionally clipped (PRD §11).
/// </remarks>
internal static class Layout {

  public static readonly ProcessColumn[] Columns = [
    ProcessColumn.Pid,
    ProcessColumn.UserName,
    ProcessColumn.State,
    ProcessColumn.CpuPercent,
    ProcessColumn.PrivateBytes,
    ProcessColumn.WorkingSetBytes,
    ProcessColumn.ReadBytesPerSecond,
    ProcessColumn.WriteBytesPerSecond,
    ProcessColumn.ThreadCount,
    ProcessColumn.HandleCount,
    ProcessColumn.Name,
  ];

  public static int WidthOf(ProcessColumn column) => column switch {
    ProcessColumn.Pid => 7,
    ProcessColumn.UserName => 10,
    ProcessColumn.State => 6,
    ProcessColumn.CpuPercent => 5,
    ProcessColumn.PrivateBytes or ProcessColumn.WorkingSetBytes => 7,
    ProcessColumn.ReadBytesPerSecond or ProcessColumn.WriteBytesPerSecond => 8,
    ProcessColumn.ThreadCount => 4,
    ProcessColumn.HandleCount => 5,
    // The last column takes what is left; the screen clips it.
    ProcessColumn.Name => 120,
    _ => 8,
  };

  /// <summary>Numbers right, text left — so the digits of a column line up under each other.</summary>
  public static bool IsRightAligned(ProcessColumn column) => column switch {
    ProcessColumn.Name or ProcessColumn.UserName or ProcessColumn.CommandLine or ProcessColumn.State => false,
    _ => true,
  };

}
