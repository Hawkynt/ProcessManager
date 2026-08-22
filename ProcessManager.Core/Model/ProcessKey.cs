namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What makes a process the same process across two samples.
/// </summary>
/// <remarks>
/// A PID on its own does not: Linux wraps at <c>/proc/sys/kernel/pid_max</c> and Windows recycles
/// eagerly, so the same number can be two different programs one second apart. Pairing it with the
/// process's own start time closes that — the kernel will not hand out the same (pid, start) twice
/// at the resolution anyone can observe. Every delta, every selection, and every privileged request
/// is keyed on this pair rather than on the number (PRD §3.2, §8.2).
/// </remarks>
/// <param name="Pid">The process id.</param>
/// <param name="StartTicks">
/// Process start time in the platform's own raw unit — clock ticks since boot on Linux, the
/// <c>KernelUserTimes.CreateTime</c> FILETIME on Windows. It is never converted for comparison, only
/// for display, because a converted value is a rounded value and a rounded value collides.
/// </param>
public readonly record struct ProcessKey(int Pid, ulong StartTicks) {

  /// <summary>The key that matches nothing.</summary>
  public static readonly ProcessKey None = new(0, 0);

  public bool IsNone => this == None;

  public override string ToString() => $"{this.Pid}@{this.StartTicks}";

}

/// <summary>
/// What makes a thread the same thread across two readings (PRD §104).
/// </summary>
/// <remarks>
/// <para>
/// The same argument as <see cref="ProcessKey"/>, one level down and with one more part. A thread id
/// recycles exactly as freely as a process id — a pool that ends a worker and starts another gets
/// the same number back — so the id is paired with the thread's own start time. And the pair is
/// unique only <em>inside</em> a process: every machine has a dozen threads numbered near its lowest
/// free ids, so the owning process is part of the identity rather than context the caller is
/// expected to remember.
/// </para>
/// <para>
/// This was the shape <see cref="Sampling.ThreadDelta"/> had already arrived at and kept in its own
/// dictionary key, which meant a <see cref="ThreadRecord"/> handed to anything else could not say
/// which process it belonged to. Naming it puts the identity on the record, where the process's is.
/// </para>
/// </remarks>
/// <param name="Owner">The process the thread runs in.</param>
/// <param name="Tid">The thread id, which is unique only within <paramref name="Owner"/>.</param>
/// <param name="StartTimeUtcTicks">
/// When the thread started, in UTC ticks. Zero where the platform would not say — which makes the
/// key weaker rather than wrong, since the owner and the id still separate it from every thread of
/// every other process.
/// </param>
public readonly record struct ThreadKey(ProcessKey Owner, int Tid, long StartTimeUtcTicks) {

  /// <summary>The key that matches nothing.</summary>
  public static readonly ThreadKey None = new(ProcessKey.None, 0, 0);

  public bool IsNone => this == None;

  public override string ToString() => $"{this.Owner}/{this.Tid}@{this.StartTimeUtcTicks}";

}
