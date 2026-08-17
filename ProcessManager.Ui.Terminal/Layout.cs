using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// Which columns the terminal shows and how wide they are.
/// </summary>
/// <remarks>
/// Fixed widths rather than measured ones. A column that resizes itself to its widest value jitters
/// every second as processes come and go, and a table whose columns move is harder to read than one
/// whose values are occasionally clipped (PRD §11).
/// <para>
/// The widths, headers and alignments come from <see cref="FieldRegistry"/>, not from here: the
/// terminal used to keep its own list, which is how it ended up one field behind the window
/// (PRD §5.1).
/// </para>
/// </remarks>
internal static class Layout {

  /// <summary>The columns the terminal opens with, in order.</summary>
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

  public static FieldDescriptor Info(ProcessField field) => FieldRegistry.Get(field);

  /// <summary>The columns a user can cycle the sort through — the graphs are not among them.</summary>
  public static ProcessField[] Sortable {
    get {
      var result = new List<ProcessField>();
      // Not "field": in C# 14 that is a keyword inside a property accessor and binds to the
      // backing field instead of the loop variable.
      foreach (var candidate in Columns)
        if (FieldRegistry.Get(candidate).IsSortable)
          result.Add(candidate);

      return [.. result];
    }
  }

}
