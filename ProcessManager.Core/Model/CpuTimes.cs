namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Cumulative CPU time for one core (or for the machine), in nanoseconds since boot.
/// </summary>
/// <remarks>
/// Nanoseconds, not the platform's native unit: Linux counts in <c>USER_HZ</c> jiffies whose size is
/// <c>sysconf(_SC_CLK_TCK)</c> and is not always 100, and Windows counts in 100 ns FILETIME units.
/// Normalising in the probe means every calculation above it is the same arithmetic on every OS
/// (PRD §2, §5.1).
/// </remarks>
public struct CpuTimes {

  public ulong UserNs;
  public ulong NiceNs;
  public ulong KernelNs;
  public ulong IdleNs;
  public ulong IoWaitNs;
  public ulong IrqNs;
  public ulong SoftIrqNs;
  public ulong StealNs;

  /// <summary>Everything, idle included — the denominator of a CPU percentage.</summary>
  public readonly ulong TotalNs
    => this.UserNs + this.NiceNs + this.KernelNs + this.IdleNs
     + this.IoWaitNs + this.IrqNs + this.SoftIrqNs + this.StealNs;

  /// <summary>Everything except idle. I/O wait is *not* busy: nothing is running during it.</summary>
  public readonly ulong BusyNs
    => this.UserNs + this.NiceNs + this.KernelNs + this.IrqNs + this.SoftIrqNs + this.StealNs;

}
