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

  /// <summary>
  /// Asks the program to close itself, the way its window's close button would (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Deliberately not the same thing as <see cref="Terminate"/>, and the difference is somebody's
  /// unsaved work. This one <i>asks</i>, and may be refused: an editor with a modified buffer puts
  /// up its own dialog and carries on running, which is the correct outcome and not a failure of the
  /// action. Terminate does not ask. A front-end that blurs the two eventually loses a file.
  /// </para>
  /// <para>
  /// A process with no window has nothing to ask, so the request falls back to the platform's polite
  /// signal — <c>SIGTERM</c> on Unix — which is what a daemon's own handler is written for. The
  /// default here is that fallback, for a platform whose windows cannot be reached.
  /// </para>
  /// </remarks>
  ActionResult EndTask(ProcessKey key) => this.Terminate(key);

  ActionResult Suspend(ProcessKey key);

  ActionResult Resume(ProcessKey key);

  /// <summary>
  /// Ends a process and everything descended from it (PRD §25.1).
  /// </summary>
  /// <param name="descendantsFirst">
  /// The subtree in the order it must be ended, deepest first — what
  /// <c>Query.ProcessTree.DescendantsFirst</c> produces. The order is not the caller's convenience:
  /// ending the root first reparents its children to init, after which they are no longer findable
  /// as its descendants and the rest of the tree survives.
  /// </param>
  /// <remarks>
  /// <para>
  /// The list is passed in rather than walked here because the walk needs a snapshot and this
  /// interface deliberately holds none. Every key in it is still re-validated one at a time by
  /// <see cref="Terminate"/>, so a pid recycled midway through a large tree is refused rather than
  /// signalled (PRD §8.2).
  /// </para>
  /// <para>
  /// A member that has already gone counts as ended, because it has: killing a parent routinely
  /// takes its children with it, and reporting that race as a failure would make the ordinary case
  /// look broken.
  /// </para>
  /// </remarks>
  ActionResult TerminateTree(IReadOnlyList<ProcessKey> descendantsFirst) {
    ArgumentNullException.ThrowIfNull(descendantsFirst);
    if (descendantsFirst.Count == 0)
      return ActionResult.Fail(ActionOutcome.Refused, "there is no process tree to end");

    var ended = 0;
    var first = default(ActionResult?);
    foreach (var key in descendantsFirst) {
      var result = this.Terminate(key);
      if (result.Succeeded || result.Outcome == ActionOutcome.ProcessExited) {
        ++ended;
        continue;
      }

      first ??= result;
    }

    if (first is not { } failure)
      return ActionResult.Ok;

    return ActionResult.Fail(
      failure.Outcome,
      $"ended {ended} of {descendantsFirst.Count}; {failure.Detail ?? failure.Outcome.ToString()}"
    );
  }

  /// <summary>
  /// Ends a process and starts it again as it was (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The argument vector, the executable and the working directory are read back from the running
  /// process by the implementation rather than supplied here, because only the platform knows where
  /// they live and a caller reconstructing them from a displayed command line would be splitting a
  /// string that was joined for a person to read.
  /// </remarks>
  LaunchResult Restart(ProcessKey key)
    => LaunchResult.Failed(ActionOutcome.NotSupportedOnPlatform, "this platform cannot restart a process here");

  /// <summary>Nice value on Unix, priority class on Windows; the caller passes the platform's scale.</summary>
  ActionResult SetPriority(ProcessKey key, int priority);

  /// <summary>Affinity as a bit mask of logical cores.</summary>
  ActionResult SetAffinity(ProcessKey key, ulong mask);

  /// <summary>Sends a signal. Unix only; returns <see cref="ActionOutcome.NotSupportedOnPlatform"/> elsewhere.</summary>
  /// <remarks>
  /// A number rather than a name, because the number is what the kernel takes and the mapping from
  /// one to the other is the architecture's — see <c>Query.Signals</c>, which owns that table and
  /// refuses to guess it on an architecture it does not know (PRD §5.3).
  /// </remarks>
  ActionResult SendSignal(ProcessKey key, int signal);

  /// <summary>
  /// How likely the kernel is to choose this process when the machine runs out of memory
  /// (PRD §25.5).
  /// </summary>
  /// <param name="adjustment">
  /// -1000 to 1000, added to the badness score the out-of-memory killer ranks by. -1000 exempts the
  /// process from being chosen at all; 1000 volunteers it first.
  /// </param>
  /// <remarks>
  /// <b>Not a memory limit and not a reservation.</b> It changes nothing about what the process may
  /// allocate — it changes which process dies when something has to, and lowering one process's
  /// score raises somebody else's chance of being picked instead. That is why the confirmation says
  /// so and why lowering it is the half that needs privilege.
  /// </remarks>
  ActionResult SetOomScoreAdjustment(ProcessKey key, int adjustment)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no out-of-memory score");

  /// <summary>
  /// One of the kernel's per-process ceilings (PRD §25.2).
  /// </summary>
  /// <param name="soft">What is enforced now, or null for no limit.</param>
  /// <param name="hard">The ceiling on the soft one, or null for no limit.</param>
  /// <remarks>
  /// Both halves are set together because the call sets both together: <c>prlimit</c> takes one
  /// structure, and an interface that offered them separately would have to read the other half back
  /// first and would race with anything the process did to itself in between.
  /// </remarks>
  ActionResult SetResourceLimit(ProcessKey key, ResourceLimitKind kind, ulong? soft, ulong? hard)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no per-process resource limits");

  /// <summary>
  /// Stops, or restarts, every process in the cgroup this one belongs to (PRD §25.1, §38).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <b>Deliberately not a suspend of the process.</b> <see cref="Suspend"/> stops one process and
  /// leaves everything it started running; this stops the whole unit — the cgroup, every cgroup
  /// below it, and any process either of them starts while it is frozen. On Linux that is what
  /// pausing a container or a service actually means, and calling it "suspend" would be the false
  /// equivalence §5.3 forbids.
  /// </para>
  /// <para>
  /// Because the target is the cgroup, a caller must name the cgroup and the number of processes in
  /// it before asking (PRD §5.5): the row somebody selected is one member of it and usually not the
  /// interesting one.
  /// </para>
  /// </remarks>
  ActionResult FreezeCgroup(ProcessKey key, bool frozen)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no cgroup freezer");

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
  /// Which scheduler class runs the process, rather than where it sits inside one (PRD §25.2).
  /// </summary>
  /// <param name="priority">
  /// The static priority the class takes: 1–99 for the real-time classes, and 0 for every other
  /// class, which does not have one. It is not a nice value and does not share its scale — nice
  /// orders processes <i>within</i> <see cref="SchedulingPolicy.Other"/>, and a class change is a
  /// change of the rules those processes are ordered by.
  /// </param>
  /// <remarks>
  /// The control nice cannot express. A batch job left at <see cref="SchedulingPolicy.Idle"/> runs
  /// only when nothing else wants a processor at all — not "less often", but not at all — which is
  /// what makes a compile or a re-index invisible on a machine somebody is using.
  /// </remarks>
  ActionResult SetSchedulingClass(ProcessKey key, SchedulingPolicy policy, int priority)
    => ActionResult.Fail(ActionOutcome.NotSupportedOnPlatform, "this platform has no scheduler classes");

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
