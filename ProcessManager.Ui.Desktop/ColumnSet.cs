using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Which columns the window shows, and which it offers.
/// </summary>
/// <remarks>
/// There is no list of columns here any more. Every column the window can show is a field in
/// <see cref="FieldRegistry"/>, with its header, width, alignment and sort order declared once and
/// shared with the terminal (PRD §5.1). What is left is the two things that really are the window's
/// own business: which columns it opens with, and in what order the chooser lists them.
/// </remarks>
internal static class ColumnSet {

  /// <summary>Every column the chooser offers, in registry order.</summary>
  public static FieldDescriptor[] All => FieldRegistry.All;

  /// <summary>
  /// What the window opens with: the Process Explorer set plus the three graphs, which are the point
  /// of having them.
  /// </summary>
  public static readonly ProcessField[] Default = [
    ProcessField.Name,
    ProcessField.Pid,
    ProcessField.UserName,
    ProcessField.CpuPercent,
    ProcessField.CpuHistory,
    ProcessField.PrivateBytes,
    ProcessField.MemoryHistory,
    ProcessField.IoHistory,
    ProcessField.ThreadCount,
  ];

  public static FieldDescriptor Info(ProcessField field) => FieldRegistry.Get(field);

  /// <summary>
  /// Whether a column is worth offering on this machine at all.
  /// </summary>
  /// <remarks>
  /// A column the platform cannot fill is still offered — it renders <c>n/a</c>, which is a true
  /// statement and occasionally the one the user wanted (PRD §72.3). What it is not is
  /// <em>default</em>-visible, which is what this decides.
  /// </remarks>
  public static bool IsUsefulHere(FieldDescriptor descriptor) => descriptor.IsSupportedHere;

}
