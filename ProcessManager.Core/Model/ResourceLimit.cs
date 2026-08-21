namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Which of the kernel's per-process ceilings a limit is (PRD §25.2).
/// </summary>
/// <remarks>
/// The names are the kernel's own, minus the <c>RLIMIT_</c>, so that what this program shows can be
/// checked against <c>prlimit</c> and against <c>/proc/[pid]/limits</c> without a translation table
/// in between (PRD §5.3).
/// </remarks>
public enum ResourceLimitKind : byte {
  CpuTime,
  FileSize,
  DataSize,
  StackSize,
  CoreFileSize,
  ResidentSet,
  Processes,
  OpenFiles,
  LockedMemory,
  AddressSpace,
  FileLocks,
  PendingSignals,
  MessageQueueBytes,
  NiceCeiling,
  RealTimePriority,
  RealTimeTimeout,
}

/// <summary>
/// What a limit is measured in, so a front-end can format it without a table of its own.
/// </summary>
public enum ResourceLimitUnit : byte {

  /// <summary>A plain count — of files, of processes, of locks.</summary>
  Count,

  Bytes,
  Seconds,
  Microseconds,

  /// <summary>A priority, which is a position rather than an amount of anything.</summary>
  Priority,

}

/// <summary>
/// One ceiling, in both of the two forms it has.
/// </summary>
/// <param name="Soft">
/// What the kernel enforces now. A process may raise it up to its hard limit whenever it likes,
/// without privilege, which is what makes the soft limit a working setting rather than a barrier.
/// </param>
/// <param name="Hard">
/// The ceiling on the soft one. <b>Lowering it cannot be undone</b> by anything short of
/// <c>CAP_SYS_RESOURCE</c>: an unprivileged process may lower its hard limit and may never raise it
/// again, which is the one irreversible thing in this whole sheet.
/// </param>
/// <remarks>
/// <see langword="null"/> is <c>RLIM_INFINITY</c> and means there is no limit, which is not a
/// quantity and is deliberately not stored as the very large number the kernel uses to spell it. The
/// cgroup reader makes the same distinction for the same reason (PRD §38): "no limit" and "a limit
/// of eighteen million terabytes" must not look alike.
/// </remarks>
public readonly record struct ResourceLimit(ResourceLimitKind Kind, ulong? Soft, ulong? Hard) {

  /// <summary>Whether the process is already at the ceiling it may not raise itself past.</summary>
  public bool IsAtItsHardLimit => this.Hard is { } hard && this.Soft is { } soft && soft >= hard;

}

/// <summary>
/// Every ceiling a process runs under, and how likely the kernel is to kill it when memory runs out
/// (PRD §25.2, §25.5).
/// </summary>
/// <param name="OomScoreAdjustment">
/// <c>/proc/[pid]/oom_score_adj</c>: -1000 to 1000, added to the badness score the out-of-memory
/// killer ranks processes by. -1000 exempts the process entirely. Null where it could not be read.
/// </param>
/// <param name="OomScore">
/// <c>/proc/[pid]/oom_score</c>: the badness the kernel currently gives it, adjustment included.
/// The number that says which process would actually be chosen, as opposed to which one somebody
/// nudged — the two are different questions and this program shows both (PRD §5.3).
/// </param>
/// <remarks>
/// Read together because they are asked about together: "why was this killed" and "why will it be
/// killed next time" are answered by the ceilings and the badness score respectively, and a sheet
/// that showed one without the other would send somebody looking in the wrong place.
/// </remarks>
public sealed record ProcessLimits(
  IReadOnlyList<ResourceLimit> Limits,
  int? OomScoreAdjustment,
  int? OomScore
) {

  /// <summary>The lowest and highest <c>oom_score_adj</c> the kernel accepts.</summary>
  public const int OomAdjustmentMinimum = -1000;

  public const int OomAdjustmentMaximum = 1000;

  /// <summary>This process's value for one kind, or null when it was not among those read.</summary>
  public ResourceLimit? Of(ResourceLimitKind kind) {
    foreach (var limit in this.Limits)
      if (limit.Kind == kind)
        return limit;

    return null;
  }

}
