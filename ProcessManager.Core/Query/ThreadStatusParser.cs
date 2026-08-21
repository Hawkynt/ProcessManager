using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The three things a thread's <c>status</c> says that its <c>stat</c> line does not (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// <c>stat</c> carries no context-switch counts and no affinity, so the thread view has to open a
/// second file per thread. That is affordable here and nowhere else: threads are enumerated for one
/// process when somebody opens the tab, never on the sampling tick (PRD §5.4).
/// </para>
/// <para>
/// The lines are <c>Key:\tvalue</c> with a TAB, not a space. Trimming only spaces is how the
/// capability mask in this same file was once read as zero for every process on the machine, so the
/// separator is whitespace of either kind here.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ThreadStatusParser {

  /// <summary>
  /// Parses one thread's <c>status</c>. A line the kernel did not write leaves its field unknown.
  /// </summary>
  /// <remarks>
  /// Absent lines report <see cref="UnknownReason.NotSupportedOnPlatform"/> rather than zero:
  /// <c>voluntary_ctxt_switches</c> only exists when the kernel was built with
  /// <c>CONFIG_SCHEDSTATS</c> or <c>CONFIG_TASK_DELAY_ACCT</c>, and a kernel without it has not told
  /// us the thread never switched — it has told us nothing (PRD §72.3).
  /// </remarks>
  public static ThreadStatus Parse(ReadOnlySpan<char> text) {
    var voluntary = Counter.NotSupported;
    var involuntary = Counter.NotSupported;
    string? affinity = null;

    while (!text.IsEmpty) {
      var newline = text.IndexOf('\n');
      var line = newline < 0 ? text : text[..newline];
      text = newline < 0 ? default : text[(newline + 1)..];

      // The kernel writes no \r, but a fixture edited on Windows might.
      if (line.EndsWith("\r"))
        line = line[..^1];

      if (TryValue(line, "voluntary_ctxt_switches:", out var value))
        voluntary = Parse(value);
      else if (TryValue(line, "nonvoluntary_ctxt_switches:", out value))
        involuntary = Parse(value);
      else if (TryValue(line, "Cpus_allowed_list:", out value) && !value.IsEmpty)
        affinity = value.ToString();
    }

    return new(voluntary, involuntary, affinity);

    static Counter Parse(ReadOnlySpan<char> value)
      => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? Counter.Of(parsed)
        : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// The value after a <c>Key:</c> label, when this line carries that label and no other.
  /// </summary>
  /// <remarks>
  /// Anchored at the start of the line, because <c>nonvoluntary_ctxt_switches:</c> contains the whole
  /// of <c>voluntary_ctxt_switches:</c> — a search for the label anywhere in the line reads the
  /// involuntary count into both halves and reports a total twice the truth.
  /// </remarks>
  private static bool TryValue(ReadOnlySpan<char> line, ReadOnlySpan<char> label, out ReadOnlySpan<char> value) {
    if (!line.StartsWith(label, StringComparison.Ordinal)) {
      value = default;
      return false;
    }

    value = line[label.Length..].Trim();
    return true;
  }

}
