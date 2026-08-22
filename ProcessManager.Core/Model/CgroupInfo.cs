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
/// What one block device is allowed to do for a cgroup, from <c>io.max</c> (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// Per device, because the limit is: a group may be held to a megabyte a second on the disk its
/// database is on and left alone on the one its logs are on, and a single figure for "I/O" could not
/// say that. The device is a major and a minor number because that is what the kernel writes;
/// <see cref="Device"/> is the name it resolves to where the machine could be asked, and null where
/// it could not — a name that could not be looked up is not the same as a device that has none.
/// </para>
/// <para>
/// Every ceiling is a <see cref="Counter"/> so that "no ceiling in this direction" —
/// <see cref="UnknownReason.NoLimit"/>, which is what the literal word <c>max</c> means — cannot be
/// confused with a ceiling of nought, which would mean the device was closed to the group entirely.
/// </para>
/// </remarks>
/// <param name="ReadBytesPerSecond"><c>rbps</c>.</param>
/// <param name="WriteBytesPerSecond"><c>wbps</c>.</param>
/// <param name="ReadOperationsPerSecond"><c>riops</c>.</param>
/// <param name="WriteOperationsPerSecond"><c>wiops</c>.</param>
public sealed record CgroupIoLimit(
  int Major,
  int Minor,
  string? Device,
  Counter ReadBytesPerSecond,
  Counter WriteBytesPerSecond,
  Counter ReadOperationsPerSecond,
  Counter WriteOperationsPerSecond
) {

  /// <summary>Whether this device is capped in any direction at all.</summary>
  public bool IsLimited
    => this.ReadBytesPerSecond.HasValue
    || this.WriteBytesPerSecond.HasValue
    || this.ReadOperationsPerSecond.HasValue
    || this.WriteOperationsPerSecond.HasValue;

  /// <summary>The device as a reader would name it: its name where there is one, its numbers where there is not.</summary>
  public string Name => this.Device ?? $"{this.Major}:{this.Minor}";

}

/// <summary>
/// One cgroup on the way from the root to a process's own, and what it caps (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// A limit set on an ancestor governs every group below it, and the group a process is in very often
/// sets nothing at all. Reading only that group answers "no limit" about a process that is being
/// held to a tenth of a core two levels up — which is the exact question somebody opens this page
/// with, and the one a process table cannot answer.
/// </para>
/// <para>
/// A level is a reading of the files that are there, so an ancestor whose controllers are switched
/// off contributes <see cref="Counter.NotSupported"/> rather than "no limit". The tightest of the
/// levels that did answer is the ceiling the process actually runs under, and the level it came from
/// is the name worth showing beside it.
/// </para>
/// </remarks>
/// <param name="Path">The cgroup path of this level. The root is <c>/</c>.</param>
/// <param name="Unit">The systemd unit this level is, where it is one; null for a slice or the root.</param>
/// <param name="CpuQuotaReason">
/// Why there is no quota here, when <paramref name="CpuQuotaCores"/> is null. A quota is a number of
/// cores and cannot carry its own reason the way a <see cref="Counter"/> does, and this level has
/// four ways of having none: no <c>cpu.max</c> at all, the literal word <c>max</c>, a file that
/// could not be parsed, and a period of nought. The first two are answers and the last two are
/// holes, and a chain that reported all four as "no quota" would tell somebody nothing is holding
/// their process back on the strength of a file nobody could read (PRD §72.3).
/// </param>
public sealed record CgroupLevel(
  string Path,
  string? Unit,
  IReadOnlyList<string> Controllers,
  double? CpuQuotaCores,
  Counter MemoryMaxBytes,
  Counter MemoryHighBytes,
  Counter PidsMax,
  IReadOnlyList<CgroupIoLimit> IoLimits,
  UnknownReason IoLimitsReason,
  UnknownReason CpuQuotaReason = UnknownReason.NotSupportedOnPlatform
);

/// <summary>
/// Which limit governs, and which cgroup imposes it (PRD §38).
/// </summary>
/// <remarks>
/// The point of reading the hierarchy at all. <see cref="Path"/> is null when nothing in the chain
/// set this limit, in which case <see cref="Value"/> carries why — a controller that is switched off
/// all the way up and a chain that deliberately set <c>max</c> are different answers (PRD §5.3).
/// </remarks>
public readonly record struct CgroupCeiling(Counter Value, string? Path, string? Unit);

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
  CgroupFreezer? Freezer = null,
  IReadOnlyList<CgroupIoLimit>? IoLimits = null,
  UnknownReason IoLimitsReason = UnknownReason.NotImplementedHere,
  IReadOnlyList<CgroupLevel>? Hierarchy = null
) {

  /// <summary>
  /// What each device is allowed here, from <c>io.max</c>. Empty where nothing is capped, and empty
  /// as well where the controller is off — <see cref="IoLimitsReason"/> is what tells those apart.
  /// </summary>
  public IReadOnlyList<CgroupIoLimit> Io => IoLimits ?? [];

  /// <summary>
  /// Every cgroup from the root down to and including this one, outermost first.
  /// </summary>
  /// <remarks>
  /// Empty where the chain was not read. The last entry is this cgroup itself, so a caller that
  /// wants "and its ancestors" takes all but the last rather than reading the same files twice.
  /// </remarks>
  public IReadOnlyList<CgroupLevel> Chain => Hierarchy ?? [];

  /// <summary>Whether a controller is switched on for this cgroup.</summary>
  public bool Has(string controller) {
    foreach (var enabled in this.Controllers)
      if (string.Equals(enabled, controller, StringComparison.Ordinal))
        return true;

    return false;
  }

  /// <summary>
  /// The smallest processor quota anywhere in the chain, and which cgroup set it.
  /// </summary>
  /// <remarks>
  /// A quota applies to the group that carries it and to everything below it, so several quotas in
  /// one chain all apply at once and the smallest is the one that bites. Null cores means nothing in
  /// the chain set one — including the case where the chain was never read, which is why a caller
  /// showing this must say which of the two it is looking at.
  /// </remarks>
  public (double? Cores, UnknownReason Reason, string? Path, string? Unit) TightestCpuQuota() {
    double? tightest = null;
    var reason = UnknownReason.NotSupportedOnPlatform;
    string? path = null;
    string? unit = null;
    foreach (var level in this.Chain) {
      if (level.CpuQuotaCores is not { } cores) {
        if (path is null && Beats(level.CpuQuotaReason, reason))
          reason = level.CpuQuotaReason;

        continue;
      }

      if (tightest is { } best && cores >= best)
        continue;

      tightest = cores;
      reason = UnknownReason.None;
      path = level.Path;
      unit = level.Unit;
    }

    return (tightest, reason, path, unit);
  }

  /// <summary>The smallest memory ceiling anywhere in the chain, and which cgroup set it.</summary>
  public CgroupCeiling TightestMemoryLimit() => Tightest(this.Chain, static level => level.MemoryMaxBytes);

  /// <summary>The smallest task ceiling anywhere in the chain, and which cgroup set it.</summary>
  public CgroupCeiling TightestTaskLimit() => Tightest(this.Chain, static level => level.PidsMax);

  /// <summary>
  /// The tightest of whichever ceilings in the chain are real numbers.
  /// </summary>
  /// <remarks>
  /// A level that has no such controller and a level that says <c>max</c> are both skipped, because
  /// neither is a ceiling — but the two are not interchangeable when <em>nothing</em> in the chain
  /// answered: a chain where somebody wrote <c>max</c> is deliberately unbounded, and one where the
  /// controller is off everywhere has simply never been asked to bound anything. The strongest
  /// opinion any level had is carried out so the difference survives (PRD §5.3).
  /// <para>
  /// And a level whose file was there and unreadable outranks both of them, which is
  /// <see cref="Beats"/>. It is the only one of the three that is a hole rather than an answer, and
  /// a chain that dropped it would report "no cgroup in the chain has that controller on" about a
  /// cgroup that plainly does — a confident negative built on a file nobody could read.
  /// </para>
  /// </remarks>
  private static CgroupCeiling Tightest(IReadOnlyList<CgroupLevel> chain, Func<CgroupLevel, Counter> pick) {
    var tightest = Counter.NotSupported;
    string? path = null;
    string? unit = null;
    foreach (var level in chain) {
      var value = pick(level);
      if (!value.HasValue) {
        if (path is null && Beats(value.Reason, tightest.Reason))
          tightest = value;

        continue;
      }

      if (path is not null && tightest.HasValue && value.Value >= tightest.Value)
        continue;

      tightest = value;
      path = level.Path;
      unit = level.Unit;
    }

    return new(tightest, path, unit);
  }

  /// <summary>
  /// Which of two "there is no ceiling here" answers a chain should report.
  /// </summary>
  /// <remarks>
  /// A file that could not be read wins, because it might have held a ceiling and nobody knows;
  /// then a deliberate <c>max</c>, which is a statement somebody made; and last a controller that is
  /// simply not on, which is the weakest thing a level can say. Ordered so that the most cautious
  /// answer in the chain is the one that reaches the reader.
  /// </remarks>
  private static bool Beats(UnknownReason candidate, UnknownReason current) => Rank(candidate) > Rank(current);

  private static int Rank(UnknownReason reason) => reason switch {
    UnknownReason.None => 3,
    UnknownReason.NoLimit => 1,
    UnknownReason.NotSupportedOnPlatform => 0,

    // Everything else is a hole: a file that would not parse, one this account may not read, one
    // whose cgroup went while it was being read.
    _ => 2,
  };

}
