using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a cgroup's <c>cpu.stat</c> says about being held back (PRD §15, §38).
/// </summary>
/// <remarks>
/// <para>
/// One reading of the file rather than two. The column of §15 and the cgroup panel of §38 ask the
/// same question of the same line, and a second parser is a second place for the answer to drift.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class CgroupCpuStatParser {

  private const string _THROTTLED = "nr_throttled ";

  /// <summary>
  /// How many times the group has been stopped for using up its CPU quota.
  /// </summary>
  /// <remarks>
  /// A group with a quota it never reaches writes a real nought here, and a group whose kernel has
  /// no CPU controller on it writes no such line at all. Reporting the second as the first would
  /// turn "nothing is limiting this" and "nobody is counting" into the same cell (PRD §72.3), so an
  /// absent line is <see cref="UnknownReason.NotSupportedOnPlatform"/> and never zero.
  /// </remarks>
  public static Counter Throttled(ReadOnlySpan<char> text) {
    while (!text.IsEmpty) {
      var newline = text.IndexOf('\n');
      var line = newline < 0 ? text : text[..newline];
      text = newline < 0 ? default : text[(newline + 1)..];

      // The kernel writes no \r, but a fixture edited on Windows might.
      if (line.EndsWith("\r"))
        line = line[..^1];

      // Anchored at the start of the line and including the space: "nr_throttled" is a prefix of
      // nothing else here, but "throttled_usec" is a different number in the same file and a search
      // for the word anywhere in the line would read microseconds as a count of periods.
      if (!line.StartsWith(_THROTTLED, StringComparison.Ordinal))
        continue;

      return ulong.TryParse(
        line[_THROTTLED.Length..].Trim(),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out var value
      )
        ? Counter.Of(value)
        : Counter.Unknown(UnknownReason.CounterInvalid);
    }

    return Counter.NotSupported;
  }

}
