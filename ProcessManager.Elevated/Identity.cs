using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Elevated;

/// <summary>
/// The check that runs before every privileged action.
/// </summary>
/// <remarks>
/// The helper is asked to act on a <see cref="ProcessKey"/> — a pid <em>and</em> the moment that
/// process started. It re-reads the start time from <c>/proc</c> itself rather than trusting the
/// caller, and refuses when they differ. Without this, a pid recycled between the user's click and
/// this syscall means the helper ends somebody else's program with root's authority, which is the
/// single worst thing this component could do (PRD §8.2).
/// </remarks>
internal static class Identity {

  public static bool Matches(ProcessKey key, out ElevatedStatus status) {
    status = ElevatedStatus.Ok;
    if (key.Pid <= 0) {
      status = ElevatedStatus.Malformed;
      return false;
    }

    string stat;
    try {
      stat = File.ReadAllText($"/proc/{key.Pid}/stat");
    } catch (IOException) {
      status = ElevatedStatus.ProcessExited;
      return false;
    } catch (UnauthorizedAccessException) {
      status = ElevatedStatus.NotPermitted;
      return false;
    }

    if (!TryReadStartTime(stat, out var startTicks)) {
      status = ElevatedStatus.Failed;
      return false;
    }

    if (startTicks == key.StartTicks)
      return true;

    status = ElevatedStatus.IdentityMismatch;
    return false;
  }

  /// <summary>
  /// Field 22 of <c>/proc/[pid]/stat</c>. Everything before it is positional and the command name in
  /// field 2 may contain spaces and brackets, so the scan starts after the last <c>)</c>.
  /// </summary>
  internal static bool TryReadStartTime(string stat, out ulong startTicks) {
    startTicks = 0;
    var close = stat.LastIndexOf(')');
    if (close < 0)
      return false;

    var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
    // After the command name, field 3 (state) is index 0 — so field 22 is index 19.
    return fields.Length > 19 && ulong.TryParse(fields[19], out startTicks);
  }

}
