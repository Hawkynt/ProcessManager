using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

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

  /// <summary>
  /// A terminal column: either one of the engine's sortable columns, or one of the three drawn
  /// histories, which have no text and no sort order.
  /// </summary>
  public readonly record struct TerminalColumn(string Header, int Width, ProcessColumn? Sortable, HistorySeries? Series) {
    public bool IsGraph => this.Series is not null;
  }

  public static readonly TerminalColumn[] Columns = [
    new("PID", 7, ProcessColumn.Pid, null),
    new("User", 10, ProcessColumn.UserName, null),
    new("S", 5, ProcessColumn.State, null),
    new("CPU%", 5, ProcessColumn.CpuPercent, null),
    new("CPU hist", 12, null, HistorySeries.Cpu),
    new("Private", 7, ProcessColumn.PrivateBytes, null),
    new("Mem hist", 12, null, HistorySeries.Memory),
    new("Read/s", 8, ProcessColumn.ReadBytesPerSecond, null),
    new("Write/s", 8, ProcessColumn.WriteBytesPerSecond, null),
    new("I/O hist", 12, null, HistorySeries.Io),
    new("Thr", 4, ProcessColumn.ThreadCount, null),
    new("Hnd", 5, ProcessColumn.HandleCount, null),
    new("Process", 120, ProcessColumn.Name, null),
  ];

  /// <summary>The columns a user can cycle the sort through — the graphs are not among them.</summary>
  public static ProcessColumn[] Sortable {
    get {
      var result = new List<ProcessColumn>();
      foreach (var column in Columns)
        if (column.Sortable is { } sortable)
          result.Add(sortable);

      return [.. result];
    }
  }

  /// <summary>Numbers right, text left — so the digits of a column line up under each other.</summary>
  public static bool IsRightAligned(in TerminalColumn column) => column.Sortable switch {
    ProcessColumn.Name or ProcessColumn.UserName or ProcessColumn.CommandLine or ProcessColumn.State => false,
    null => false,
    _ => true,
  };

}
