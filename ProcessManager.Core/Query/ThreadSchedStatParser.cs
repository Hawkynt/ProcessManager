using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/task/[tid]/schedstat</c> — three numbers, and the one that matters is the middle
/// one (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// The only per-thread scheduling file that answers for a thread the reader does not own. <c>stat</c>
/// masks its addresses, <c>syscall</c> and <c>stack</c> refuse outright, and this one is readable by
/// anybody who can see the task at all — which makes it the source for how long a thread has been
/// kept off a processor by other threads.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ThreadSchedStatParser {

  /// <summary>
  /// Parses one thread's <c>schedstat</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A kernel built without <c>CONFIG_SCHED_INFO</c> has no such file at all, which the caller sees
  /// as a failed read. A kernel that has it and was booted with <c>schedstats=disable</c> is the
  /// dangerous case: it writes the literal line <c>0 0 0</c>, and taking that at face value would
  /// report a thread that has never been given a processor — which is not something that can be true
  /// of a thread that exists to be read about. All three zeroes together are therefore read as the
  /// switch being off rather than as three measurements (PRD §72.3).
  /// </para>
  /// <para>
  /// Only all three. A thread that has genuinely never had to wait writes a real run time beside a
  /// zero delay, and that zero is a reading worth having.
  /// </para>
  /// </remarks>
  public static ThreadSchedStat Parse(ReadOnlySpan<byte> content) {
    var scanner = new AsciiScanner(content);
    var run = scanner.NextField();
    var queued = scanner.NextField();
    var timeslices = scanner.NextField();
    if (run.IsEmpty || queued.IsEmpty || timeslices.IsEmpty)
      return ThreadSchedStat.Unreadable(UnknownReason.CounterInvalid);

    var runNs = AsciiScanner.ParseUInt64(run);
    var queuedNs = AsciiScanner.ParseUInt64(queued);
    var count = AsciiScanner.ParseUInt64(timeslices);
    return runNs == 0 && queuedNs == 0 && count == 0
      ? ThreadSchedStat.Unreadable(UnknownReason.NotSupportedOnPlatform)
      : new(Counter.Of(runNs), Counter.Of(queuedNs), Counter.Of(count));
  }

}
