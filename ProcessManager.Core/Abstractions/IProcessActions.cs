using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>Why an action did not happen. Every one of these is shown to the user verbatim.</summary>
public enum ActionOutcome : byte {
  Succeeded = 0,
  NotPermitted,
  ProcessExited,

  /// <summary>The pid still exists but is no longer the process that was asked about (PRD §8.2).</summary>
  IdentityMismatch,

  NotSupportedOnPlatform,
  Refused,
  Failed,
}

/// <summary>The result of an action, with something the UI can put in front of a person.</summary>
public readonly record struct ActionResult(ActionOutcome Outcome, string? Detail = null) {

  public bool Succeeded => this.Outcome == ActionOutcome.Succeeded;

  public static readonly ActionResult Ok = new(ActionOutcome.Succeeded);

  public static ActionResult Fail(ActionOutcome outcome, string detail) => new(outcome, detail);

}

/// <summary>
/// Everything that changes the state of the machine. Separate from <see cref="ISystemProbe"/> so
/// that a read-only front-end (or a test) can hold the one without the other.
/// </summary>
/// <remarks>
/// Every method takes a <see cref="ProcessKey"/>, never a bare pid, and every implementation
/// re-validates the identity immediately before acting. A pid recycled between the click and the
/// syscall must be refused, not acted on — this is the whole reason the key is a pair (PRD §8.2).
/// </remarks>
public interface IProcessActions {

  ActionResult Terminate(ProcessKey key);

  ActionResult Suspend(ProcessKey key);

  ActionResult Resume(ProcessKey key);

  /// <summary>Nice value on Unix, priority class on Windows; the caller passes the platform's scale.</summary>
  ActionResult SetPriority(ProcessKey key, int priority);

  /// <summary>Affinity as a bit mask of logical cores.</summary>
  ActionResult SetAffinity(ProcessKey key, ulong mask);

  /// <summary>Sends a signal. Unix only; returns <see cref="ActionOutcome.NotSupportedOnPlatform"/> elsewhere.</summary>
  ActionResult SendSignal(ProcessKey key, int signal);

  /// <summary>
  /// Which class the process's disk requests are scheduled in (PRD §26).
  /// </summary>
  /// <remarks>
  /// The control that makes a backup or an indexer stop making a machine unusable without slowing it
  /// down much — moved to idle I/O it keeps running at full speed and yields the disk to anything
  /// else that wants it. Raising into the real-time class needs privilege; lowering does not.
  /// </remarks>
  ActionResult SetIoPriority(ProcessKey key, IoPriority priority)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no I/O priority");

  /// <summary>
  /// One thread's scheduling priority, rather than the whole process's.
  /// </summary>
  /// <remarks>
  /// The process key is still required and still re-validated: a tid is only meaningful inside the
  /// process that owns it, and a recycled pid would otherwise let a click land on a thread of
  /// something else entirely (PRD §8.2).
  /// </remarks>
  ActionResult SetThreadPriority(ProcessKey key, int threadId, int priority)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no per-thread priority");

  /// <summary>One thread's CPU affinity, as a bit mask of logical cores.</summary>
  ActionResult SetThreadAffinity(ProcessKey key, int threadId, ulong mask)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no per-thread affinity");

  /// <summary>
  /// Starts a process (PRD §54).
  /// </summary>
  /// <remarks>
  /// The scheduling parts of the request are applied after the process exists, because there is no
  /// portable way to start one that is already niced — so a launch can succeed while its priority
  /// does not, and the result says which happened rather than reporting a failure for a process
  /// that is now running.
  /// </remarks>
  LaunchResult Launch(LaunchRequest request)
    => LaunchResult.Failed(ActionOutcome.NotSupportedOnPlatform, "this platform cannot start processes here");

}
