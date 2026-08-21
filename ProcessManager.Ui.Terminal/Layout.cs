using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>How much terminal there is to lay a table out in (PRD §57.1).</summary>
public enum TerminalBreakpoint : byte {

  /// <summary>A phone-sized window or a serial console: identity and the two figures that matter.</summary>
  Narrow,

  /// <summary>The eighty-column default, and most SSH sessions.</summary>
  Medium,

  /// <summary>A maximised desktop terminal, where the histories fit as well as the numbers.</summary>
  Full,

}

/// <summary>
/// Which columns the terminal opens with at a given width.
/// </summary>
/// <remarks>
/// <para>
/// The headers, widths and alignments come from <see cref="FieldRegistry"/>, not from here: the
/// terminal used to keep its own list, which is how it ended up one field behind the window
/// (PRD §5.1). What is decided here is only which of them are worth the space at this size.
/// </para>
/// <para>
/// Dropping columns rather than shrinking them. A narrow terminal that keeps all thirteen gives each
/// of them four characters, and thirteen truncated values answer nothing; four whole ones answer
/// what is running, whose it is, and what it costs (PRD §57.1).
/// </para>
/// </remarks>
internal static class Layout {

  /// <summary>The columns a full-width terminal opens with, in order.</summary>
  public static readonly ProcessField[] Columns = [
    ProcessField.Pid,
    ProcessField.UserName,
    ProcessField.State,
    ProcessField.CpuPercent,
    ProcessField.CpuHistory,
    ProcessField.PrivateBytes,
    ProcessField.MemoryHistory,
    ProcessField.ReadBytesPerSecond,
    ProcessField.WriteBytesPerSecond,
    ProcessField.IoHistory,
    ProcessField.ThreadCount,
    ProcessField.HandleCount,
    ProcessField.Name,
  ];

  /// <summary>An SSH window: one history, and the numbers that pay for their width.</summary>
  public static readonly ProcessField[] MediumColumns = [
    ProcessField.Pid,
    ProcessField.UserName,
    ProcessField.State,
    ProcessField.CpuPercent,
    ProcessField.CpuHistory,
    ProcessField.PrivateBytes,
    ProcessField.MemoryHistory,
    ProcessField.ThreadCount,
    ProcessField.Name,
  ];

  /// <summary>The five things a process is: which it is, whose it is, and what it is spending.</summary>
  public static readonly ProcessField[] NarrowColumns = [
    ProcessField.Pid,
    ProcessField.UserName,
    ProcessField.CpuPercent,
    ProcessField.PrivateBytes,
    ProcessField.Name,
  ];

  /// <summary>
  /// Which size this width is.
  /// </summary>
  /// <remarks>
  /// Both boundaries are measurements rather than preferences. The full set of thirteen columns and
  /// their separators is 126 characters before the process name has a single one, so it needs 140;
  /// the medium set is 70, which leaves an eighty-column terminal ten characters of name — and a name
  /// column ten wide shows "kthread" for every row. So eighty columns is a narrow terminal, whatever
  /// the window manager calls it.
  /// </remarks>
  public static TerminalBreakpoint BreakpointFor(int width)
    => width < 100 ? TerminalBreakpoint.Narrow
     : width < 140 ? TerminalBreakpoint.Medium
     : TerminalBreakpoint.Full;

  public static ProcessField[] ColumnsFor(int width) => BreakpointFor(width) switch {
    TerminalBreakpoint.Narrow => NarrowColumns,
    TerminalBreakpoint.Medium => MediumColumns,
    _ => Columns,
  };

  public static FieldDescriptor Info(ProcessField field) => FieldRegistry.Get(field);

}
