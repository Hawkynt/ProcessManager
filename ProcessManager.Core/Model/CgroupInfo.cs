namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Whether the cgroup is frozen, and whether this kernel can freeze it at all (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// The freezer is the only thing on Linux that stops a whole unit the way a person means it. A
/// <c>SIGSTOP</c> stops one process and leaves everything it started running; freezing the cgroup
/// stops the cgroup and every cgroup below it, including processes that have not been born yet —
/// which is why it is described as an action on the cgroup and never as a suspend of the process
/// somebody happened to have selected (PRD §5.3).
/// </para>
/// <para>
/// <see cref="Supported"/> is false on a kernel before 5.2 and in a cgroup where the file is absent.
/// That is not the same as "not frozen", and the two are kept apart.
/// </para>
/// </remarks>
public sealed record CgroupFreezer(bool Supported, bool Frozen);

/// <summary>
/// What a process's cgroup allows it and what it is using (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// The answer to "why is this process slow when the machine is idle". A container or a systemd unit
/// can be throttled to a fraction of a core or capped well below the machine's memory, and nothing
/// in a process table shows that — the process simply appears to be doing less than it should.
/// </para>
/// <para>
/// cgroup v2 only. The v1 layout puts each controller in its own hierarchy with its own path, so a
/// process has several cgroups at once and no single one of them answers this; every distribution
/// this program targets has defaulted to v2 for years, and a v1 machine reports that rather than
/// half an answer (PRD §5.3).
/// </para>
/// </remarks>
/// <param name="Path">The cgroup, as it appears in <c>/proc/[pid]/cgroup</c>.</param>
/// <param name="Controllers">
/// Which controllers are actually enabled here. A limit file existing does not mean its controller
/// is on — a delegated cgroup may have <c>memory</c> and not <c>cpu</c>, in which case the CPU limit
/// is inherited from an ancestor rather than absent.
/// </param>
/// <param name="CpuQuotaCores">
/// The share of a processor this cgroup may use, as a number of cores — <c>cpu.max</c>'s quota over
/// its period. Expressed in cores rather than as the raw pair because "0.5 cores" is a sentence and
/// "50000 100000" is not.
/// </param>
public sealed record CgroupInfo(
  string Path,
  IReadOnlyList<string> Controllers,
  Counter MemoryCurrentBytes,
  Counter MemoryMaxBytes,
  Counter MemoryHighBytes,
  Counter PidsCurrent,
  Counter PidsMax,
  double? CpuQuotaCores,
  Counter ThrottledCount,
  PressureReading CpuPressure,
  PressureReading MemoryPressure,
  PressureReading IoPressure,
  CgroupFreezer? Freezer = null
) {

  /// <summary>Whether a controller is switched on for this cgroup.</summary>
  public bool Has(string controller) {
    foreach (var enabled in this.Controllers)
      if (string.Equals(enabled, controller, StringComparison.Ordinal))
        return true;

    return false;
  }

}
