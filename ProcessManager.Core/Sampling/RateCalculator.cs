using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Sampling;

/// <summary>Which convention a CPU percentage is expressed in (PRD §3.2).</summary>
public enum CpuPercentMode : byte {

  /// <summary>
  /// 100 % means the whole machine, the way Task Manager reports it. A process saturating four of
  /// eight cores reads 50 %.
  /// </summary>
  Normalized = 0,

  /// <summary>
  /// 100 % means one core, the way top and htop report it. The same process reads 400 %.
  /// </summary>
  PerCore,

}

/// <summary>
/// The arithmetic every derived number in the program goes through. Deliberately static, deliberately
/// tiny, and deliberately the only place a division happens: every interesting bug in a tool of this
/// kind is a division that should not have been performed (PRD §3.2).
/// </summary>
public static class RateCalculator {

  /// <summary>Nanoseconds between two <see cref="System.Diagnostics.Stopwatch"/> timestamps.</summary>
  public static double ElapsedNanoseconds(long fromTicks, long toTicks) {
    var ticks = toTicks - fromTicks;
    return ticks <= 0 ? double.NaN : ticks * (1_000_000_000d / System.Diagnostics.Stopwatch.Frequency);
  }

  /// <summary>
  /// A per-second figure from the growth of a cumulative counter.
  /// </summary>
  public static Rate PerSecond(Counter previous, Counter current, double elapsedNs) {
    var delta = current.Since(previous);
    if (!delta.HasValue)
      return Rate.Unknown(delta.Reason);

    return !double.IsFinite(elapsedNs) || elapsedNs <= 0
      ? Rate.Unknown(UnknownReason.CounterInvalid)
      : Rate.Of(delta.Value * 1_000_000_000d / elapsedNs);
  }

  /// <summary>
  /// A per-second figure from a counter that may move in either direction.
  /// </summary>
  /// <remarks>
  /// <see cref="PerSecond"/> refuses a counter that went backwards, because a monotonic counter that
  /// decreased has wrapped or been reused and its difference means nothing. Committed memory is not
  /// monotonic — it falls when a process frees — and the fall is the interesting half: a process
  /// whose private bytes only ever climb is the one leaking. So this one keeps the sign.
  /// </remarks>
  public static Rate SignedPerSecond(Counter previous, Counter current, double elapsedNs) {
    if (!previous.HasValue)
      return previous.Reason == UnknownReason.None ? Rate.NotSampledYet : Rate.Unknown(previous.Reason);
    if (!current.HasValue)
      return Rate.Unknown(current.Reason);
    if (!double.IsFinite(elapsedNs) || elapsedNs <= 0)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var difference = (double)current.Value - previous.Value;
    return Rate.Of(difference * 1_000_000_000d / elapsedNs);
  }

  /// <summary>
  /// CPU percent from the growth of a process's CPU-time counter.
  /// </summary>
  /// <remarks>
  /// The result is deliberately <em>not</em> clamped to 100. A multi-threaded process legitimately
  /// exceeds it in <see cref="CpuPercentMode.PerCore"/>, and in
  /// <see cref="CpuPercentMode.Normalized"/> a value above 100 means the sample was disturbed —
  /// clamping it would hide exactly the thing worth seeing.
  /// </remarks>
  public static Rate CpuPercent(
    Counter previousCpuNs,
    Counter currentCpuNs,
    double elapsedNs,
    int coreCount,
    CpuPercentMode mode
  ) {
    var delta = currentCpuNs.Since(previousCpuNs);
    if (!delta.HasValue)
      return Rate.Unknown(delta.Reason);

    if (!double.IsFinite(elapsedNs) || elapsedNs <= 0 || coreCount <= 0)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var divisor = mode == CpuPercentMode.Normalized ? elapsedNs * coreCount : elapsedNs;
    return Rate.Of(delta.Value * 100d / divisor);
  }

  /// <summary>
  /// Busy percentage of a core (or of the machine) from two readings of its jiffy counters. Unlike
  /// the per-process figure this one has its own denominator — the counters themselves account for
  /// every nanosecond, idle included — so it needs no wall clock and no core count.
  /// </summary>
  /// <summary>
  /// How much of the interval this core spent in the kernel: system time, hard IRQ and soft IRQ.
  /// </summary>
  /// <remarks>
  /// The three together and not system time alone. Interrupt handling is the kernel running on this
  /// core whether or not it is attributed to a process, and a machine drowning in soft IRQs — which
  /// is what a saturated network adapter looks like from here — would otherwise show a nearly idle
  /// kernel beside a fully busy core (PRD §46).
  /// </remarks>
  public static Rate KernelPercent(in CpuTimes previous, in CpuTimes current)
    => PortionPercent(
      previous, current,
      previous.KernelNs + previous.IrqNs + previous.SoftIrqNs,
      current.KernelNs + current.IrqNs + current.SoftIrqNs
    );

  /// <summary>How much of the interval this core spent running user code, nice time included.</summary>
  public static Rate UserPercent(in CpuTimes previous, in CpuTimes current)
    => PortionPercent(
      previous, current,
      previous.UserNs + previous.NiceNs,
      current.UserNs + current.NiceNs
    );

  /// <summary>
  /// One slice of a core's time as a percentage of the whole interval.
  /// </summary>
  /// <remarks>
  /// Both counters have to be checked, not just the slice: a counter that went backwards means the
  /// core was hot-unplugged or the machine was suspended, and the honest answer to that is that
  /// nothing can be computed — never a zero, which would read as an idle core (PRD §5.3).
  /// </remarks>
  private static Rate PortionPercent(in CpuTimes previous, in CpuTimes current, ulong before, ulong after) {
    var total = current.TotalNs;
    var previousTotal = previous.TotalNs;
    if (total < previousTotal || after < before)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var totalDelta = total - previousTotal;
    return totalDelta == 0
      ? Rate.Unknown(UnknownReason.CounterInvalid)
      : Rate.Of((after - before) * 100d / totalDelta);
  }

  public static Rate BusyPercent(in CpuTimes previous, in CpuTimes current) {
    var total = current.TotalNs;
    var previousTotal = previous.TotalNs;
    if (total < previousTotal)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var totalDelta = total - previousTotal;
    if (totalDelta == 0)
      return Rate.Unknown(UnknownReason.CounterInvalid);

    var busy = current.BusyNs;
    var previousBusy = previous.BusyNs;
    return busy < previousBusy
      ? Rate.Unknown(UnknownReason.CounterInvalid)
      : Rate.Of((busy - previousBusy) * 100d / totalDelta);
  }

}
