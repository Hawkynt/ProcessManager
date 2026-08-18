namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Why a value is not there. PRD §3.4: a missing number is never rendered as zero, and the reason
/// travels with the hole so the UI can say <c>—</c> for "you may not read this" and <c>n/a</c> for
/// "this platform has no such thing".
/// </summary>
public enum UnknownReason : byte {

  /// <summary>The value is present.</summary>
  None = 0,

  /// <summary>The OS refused: another user's process, and no elevated helper is running.</summary>
  NotPermitted,

  /// <summary>This platform has no such counter. Not a failure — a different machine.</summary>
  NotSupportedOnPlatform,

  /// <summary>
  /// This platform could report it and we have not built that yet.
  /// </summary>
  /// <remarks>
  /// Deliberately distinct from <see cref="NotSupportedOnPlatform"/>. "Windows has no cgroups" and
  /// "we have not written the Windows token code yet" are different statements, and rendering the
  /// second as the first tells the reader the machine cannot do something it can (PRD §7). One is a
  /// fact about the operating system; the other is a fact about us.
  /// </remarks>
  NotImplementedHere,

  /// <summary>The process went away between two reads of its own files.</summary>
  ProcessExited,

  /// <summary>A rate that needs two samples, asked for after the first one.</summary>
  NotSampledYet,

  /// <summary>
  /// The arithmetic did not survive: a monotonic counter that decreased, a zero or negative
  /// interval, a value the kernel reported as garbage.
  /// </summary>
  CounterInvalid,

}
