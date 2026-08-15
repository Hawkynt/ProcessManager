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

}
