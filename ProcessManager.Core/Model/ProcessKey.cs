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
