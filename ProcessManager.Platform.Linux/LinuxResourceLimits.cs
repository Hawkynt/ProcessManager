using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The ceilings a process runs under, and how likely it is to be chosen when memory runs out
/// (PRD §25.2, §25.5).
/// </summary>
/// <remarks>
/// <para>
/// On demand, never from the sampling path: three small files per process is affordable once for the
/// process being looked at and indefensible four hundred times a second (PRD §5.4). The cgroup
/// reader beside it follows the same rule for the same reason.
/// </para>
/// <para>
/// Everything here is read as text out of <c>/proc</c>, so a recorded tree answers exactly as a live
/// machine does and the whole sheet is testable without one (PRD §9.1). Only the <em>setting</em>
/// side needs a syscall, and it lives in <see cref="LinuxProcessActions"/>.
/// </para>
/// </remarks>
internal static class LinuxResourceLimits {

  /// <summary>
  /// Reads one process's limits and out-of-memory standing, or null where there is nothing to read.
  /// </summary>
  /// <param name="procRoot">Where the process tree is mounted — <c>/proc</c>, or a recording of one.</param>
  public static ProcessLimits? Read(string procRoot, int pid) {
    var directory = Path.Combine(procRoot, pid.ToString(CultureInfo.InvariantCulture));
    if (!Directory.Exists(directory))
      return null;

    var limits = ProcLimitsParser.Parse(ReadText(directory, "limits"));

    // Both files, or neither, but not "0". oom_score_adj is unreadable for another user's process on
    // some hardened kernels, and reporting the default of nought for it would say somebody had left
    // a process at ordinary priority when in fact nobody could look (PRD §3.4).
    var adjustment = Number(ReadText(directory, "oom_score_adj"));
    var score = Number(ReadText(directory, "oom_score"));

    // Nothing at all was readable: the process has gone, or belongs to somebody else. A sheet of
    // sixteen dashes and two blanks is not an answer worth returning.
    return limits.Count == 0 && adjustment is null && score is null
      ? null
      : new(limits, adjustment, score);
  }

  private static int? Number(string? text)
    => text is { Length: > 0 } && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
      ? value
      : null;

  private static string? ReadText(string directory, string file) {
    try {
      var path = Path.Combine(directory, file);
      return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}
