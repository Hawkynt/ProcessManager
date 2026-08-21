using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Reads <c>/proc/[pid]/limits</c> (PRD §25.2).
/// </summary>
/// <remarks>
/// <para>
/// The text rather than <c>prlimit</c>, deliberately, and the setting side uses the syscall. Reading
/// the file is one code path that answers for a live process and for a recorded tree alike, which is
/// what lets this be tested without a machine (PRD §9.1); the syscall answers only for a process
/// that exists on this kernel right now. Writing has no such choice — there is no file to write —
/// so the two halves use different mechanisms on purpose rather than by neglect.
/// </para>
/// <para>
/// The columns are found from the header line rather than hard-coded. The kernel formats the file
/// with fixed widths, and a name like "Max locked memory" contains spaces, so splitting on
/// whitespace loses the name; taking the header's own column positions cannot drift from the file
/// it is reading.
/// </para>
/// </remarks>
public static class ProcLimitsParser {

  /// <summary>
  /// The kernel's own label for each limit, in the order the file prints them.
  /// </summary>
  /// <remarks>
  /// Matched by name rather than by position, even though the order is fixed: a kernel that adds a
  /// limit in the middle would otherwise shift every value below it onto the wrong name, which is a
  /// wrong answer rather than a missing one.
  /// </remarks>
  private static readonly (string Label, ResourceLimitKind Kind)[] _Labels = [
    ("Max cpu time", ResourceLimitKind.CpuTime),
    ("Max file size", ResourceLimitKind.FileSize),
    ("Max data size", ResourceLimitKind.DataSize),
    ("Max stack size", ResourceLimitKind.StackSize),
    ("Max core file size", ResourceLimitKind.CoreFileSize),
    ("Max resident set", ResourceLimitKind.ResidentSet),
    ("Max processes", ResourceLimitKind.Processes),
    ("Max open files", ResourceLimitKind.OpenFiles),
    ("Max locked memory", ResourceLimitKind.LockedMemory),
    ("Max address space", ResourceLimitKind.AddressSpace),
    ("Max file locks", ResourceLimitKind.FileLocks),
    ("Max pending signals", ResourceLimitKind.PendingSignals),
    ("Max msgqueue size", ResourceLimitKind.MessageQueueBytes),
    ("Max nice priority", ResourceLimitKind.NiceCeiling),
    ("Max realtime priority", ResourceLimitKind.RealTimePriority),
    ("Max realtime timeout", ResourceLimitKind.RealTimeTimeout),
  ];

  /// <summary>
  /// Every limit the file names, in the file's own order. Empty when it is not that file at all.
  /// </summary>
  public static IReadOnlyList<ResourceLimit> Parse(string? text) {
    if (text is not { Length: > 0 })
      return [];

    var lines = text.Split('\n');
    if (lines.Length == 0)
      return [];

    var header = lines[0];
    var soft = header.IndexOf("Soft Limit", StringComparison.Ordinal);
    var hard = header.IndexOf("Hard Limit", StringComparison.Ordinal);
    // No header, no columns. Guessing widths from the first data line would work until a value grew
    // wide enough to touch its neighbour, and then it would be silently wrong.
    if (soft <= 0 || hard <= soft)
      return [];

    var limits = new List<ResourceLimit>(_Labels.Length);
    for (var i = 1; i < lines.Length; ++i) {
      var line = lines[i].TrimEnd('\r');
      if (line.Length <= soft)
        continue;

      var name = line[..soft].Trim();
      if (Kind(name) is not { } kind)
        continue;

      var softText = line[soft..Math.Min(line.Length, hard)].Trim();
      var hardText = hard < line.Length ? Column(line[hard..]) : string.Empty;
      // A row whose values are neither a number nor the word "unlimited" is left out rather than
      // reported as unlimited: those are opposite answers, and the wrong one of the two would say a
      // process has no ceiling where the file simply could not be read (PRD §72.3).
      if (TryValue(softText, out var softValue) && TryValue(hardText, out var hardValue))
        limits.Add(new(kind, softValue, hardValue));
    }

    return limits;
  }

  /// <summary>
  /// The hard limit out of the tail of a line, which still has the units column after it.
  /// </summary>
  /// <remarks>
  /// Split on whitespace rather than on another column position: the units column's offset is in the
  /// header too, but two of the sixteen rows have no unit at all and a fixed slice would then reach
  /// past the end of the line for exactly those two.
  /// </remarks>
  private static string Column(string tail) {
    var trimmed = tail.TrimStart();
    var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
    return space < 0 ? trimmed.Trim() : trimmed[..space];
  }

  private static ResourceLimitKind? Kind(string label) {
    foreach (var (name, kind) in _Labels)
      if (string.Equals(name, label, StringComparison.Ordinal))
        return kind;

    return null;
  }

  /// <summary>
  /// One value, where <c>unlimited</c> is null and anything else is a refusal.
  /// </summary>
  /// <remarks>
  /// Unlimited is not a quantity. The cgroup reader draws the same line for the same reason (PRD
  /// §38) — printing the number the kernel spells infinity with would put 18446744073709551615 in
  /// front of somebody looking for a real ceiling.
  /// </remarks>
  private static bool TryValue(string text, out ulong? value) {
    value = null;
    if (string.Equals(text, "unlimited", StringComparison.Ordinal))
      return true;

    if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
      return false;

    value = parsed;
    return true;
  }

}
