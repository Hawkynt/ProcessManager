using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Every ceiling on a process: its own, its cgroup's, and its standing with the out-of-memory
/// killer (PRD §25.2, §25.5, §38).
/// </summary>
/// <remarks>
/// <para>
/// The answer to "why is this slow when the machine is idle", and to "why did this die". A container
/// or a systemd unit can be throttled to a fraction of a core or capped well below the machine's
/// memory, and nothing in a process table shows that — the process simply appears to be doing less
/// than it should.
/// </para>
/// <para>
/// The two kinds of ceiling are printed under separate headings and never merged, because different
/// parts of the kernel enforce them against different things: <c>RLIMIT_NPROC</c> is a limit on this
/// <em>user</em>, <c>pids.max</c> is a limit on this cgroup, and presenting them as one number would
/// be exactly the false equivalence §5.3 forbids.
/// </para>
/// </remarks>
internal static class LimitsReport {

  public static int Run(Sampler sampler, ISystemProbe probe, int pid) {
    sampler.Sample();

    var snapshot = sampler.Current;
    var key = default(ProcessKey);
    var name = string.Empty;
    foreach (var process in snapshot.Processes)
      if (process.Key.Pid == pid) {
        key = process.Key;
        name = process.Name;
      }

    if (key.Pid == 0) {
      Console.Error.WriteLine($"procman: there is no process {pid.ToString(CultureInfo.InvariantCulture)}.");
      return 1;
    }

    if (probe.DescribeCgroup(key) is not { } cgroup) {
      Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)})");
      Image(probe, key);
      Console.WriteLine();
      OwnLimits(probe, key);
      // Not an error. A machine on cgroup v1, or a process in no cgroup at all, is an ordinary
      // situation and the honest answer is that there are no limits to report (PRD §5.3).
      Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)}) is in no cgroup this build can read.");
      Console.WriteLine("Only the unified hierarchy (cgroup v2) is read; v1 splits a process across several.");
      return 0;
    }

    Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)})");
    Image(probe, key);
    OwnLimits(probe, key);
    Console.WriteLine("its cgroup");
    Console.WriteLine($"  cgroup               {cgroup.Path}");
    Console.WriteLine($"  controllers          {(cgroup.Controllers.Count > 0 ? string.Join(", ", cgroup.Controllers) : "none enabled here")}");

    // A limit and what is being used against it, on one line each: either alone answers half the
    // question somebody asked.
    Console.WriteLine($"  processor            {Cores(cgroup.CpuQuotaCores)}");
    Console.WriteLine($"  throttled            {Humanize.Count(cgroup.ThrottledCount)}");
    Console.WriteLine($"  memory               {Humanize.Bytes(cgroup.MemoryCurrentBytes)} of {Limit(cgroup.MemoryMaxBytes)}");
    Console.WriteLine($"  memory, soft cap     {Limit(cgroup.MemoryHighBytes)}");
    // Tasks and not processes. `pids.current` counts threads, so a cgroup with 58 processes in it
    // routinely reports 892 — and printing that under a heading of "processes" is a figure wrong by
    // an order of magnitude with nothing to say so. It is what systemd calls TasksMax (PRD §5.3).
    Console.WriteLine($"  tasks                {Humanize.Count(cgroup.PidsCurrent)} of {Limit(cgroup.PidsMax)}");
    Console.WriteLine("  a task is a thread, so a process with eight threads counts as eight");
    Console.WriteLine();
    Console.WriteLine($"  stalled on CPU       {Pressure(cgroup.CpuPressure)}");
    Console.WriteLine($"  stalled on memory    {Pressure(cgroup.MemoryPressure)}");
    Console.WriteLine($"  stalled on I/O       {Pressure(cgroup.IoPressure)}");
    Console.WriteLine($"  frozen               {Frozen(cgroup.Freezer)}");
    return 0;
  }

  /// <summary>
  /// Whether the cgroup is stopped — which nothing in the process table will say, because the
  /// kernel has no process state for frozen (PRD §38).
  /// </summary>
  private static string Frozen(CgroupFreezer? freezer) => freezer switch {
    { Supported: false } or null => "this kernel's cgroups have no freezer",
    { Frozen: true } => "yes — every process in it is stopped, and each still reports itself as sleeping",
    _ => "no",
  };

  /// <summary>
  /// The ceilings that belong to the process itself, and its standing with the out-of-memory killer
  /// (PRD §25.2, §25.5).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Each ceiling carries what running into it actually does, because that is never the same thing
  /// twice — <c>RLIMIT_CPU</c> sends a signal, <c>RLIMIT_NOFILE</c> fails an <c>open</c>, and
  /// <c>RLIMIT_AS</c> fails an allocation the program probably does not check. A column of numbers
  /// without them is a sheet nobody can act on.
  /// </para>
  /// <para>
  /// The two out-of-memory figures are printed separately on purpose: the adjustment is what
  /// somebody asked for and the score is what the kernel would actually do about it.
  /// </para>
  /// </remarks>
  private static void OwnLimits(ISystemProbe probe, ProcessKey key) {
    if (probe.DescribeResourceLimits(key) is not { } limits) {
      Console.WriteLine("its own limits are not readable — another user's process, or one that has ended.");
      Console.WriteLine();
      return;
    }

    if (limits.Limits.Count > 0) {
      Console.WriteLine("its own limits");
      foreach (var limit in limits.Limits) {
        if (ResourceLimits.Of(limit.Kind) is not { } definition)
          continue;

        Console.WriteLine($"  {definition.Name,-22}{ResourceLimits.Format(in limit),-28}{definition.Consequence}");
      }

      Console.WriteLine();
    }

    Console.WriteLine("out of memory");
    Console.WriteLine($"  adjustment           {Adjustment(limits.OomScoreAdjustment)}");
    Console.WriteLine($"  badness now          {limits.OomScore?.ToString(CultureInfo.InvariantCulture) ?? Humanize.Placeholder(UnknownReason.NotPermitted)}");
    Console.WriteLine("  the killer picks the highest badness on the machine; the adjustment is added to it");
    Console.WriteLine();
  }

  /// <summary>
  /// The adjustment, with the one value that is not a number on the same scale said in words.
  /// </summary>
  private static string Adjustment(int? value) => value switch {
    null => Humanize.Placeholder(UnknownReason.NotPermitted),
    ProcessLimits.OomAdjustmentMinimum => $"{ProcessLimits.OomAdjustmentMinimum.ToString(CultureInfo.InvariantCulture)} — exempt; the killer will never choose it",
    0 => "0 — untouched",
    _ => value.Value.ToString(CultureInfo.InvariantCulture),
  };

  /// <summary>
  /// What the running program actually is (PRD §14).
  /// </summary>
  /// <remarks>
  /// The architecture especially: on a machine that runs more than one, the machine's answer is not
  /// the program's, and a 32-bit process on a 64-bit kernel is worth seeing.
  /// </remarks>
  private static void Image(ISystemProbe probe, ProcessKey key) {
    if (probe.DescribeImage(key) is not { } image)
      return;

    if (image.Path is { Length: > 0 } path)
      Console.WriteLine($"  image                {path}");

    if (image.Architecture is { Length: > 0 } architecture)
      Console.WriteLine($"  built for            {architecture}{(image.Bits > 0 ? $", {image.Bits.ToString(CultureInfo.InvariantCulture)}-bit" : string.Empty)}{(image.IsPositionIndependent == true ? ", position independent" : string.Empty)}");

    // "No interpreter" and "nobody could look" are different answers, and saying the first when the
    // second is true claims a program is statically linked because we lacked permission to check.
    if (image.HeaderRead)
      Console.WriteLine($"  loaded by            {image.Interpreter ?? "nothing — statically linked"}");
    else
      Console.WriteLine($"  loaded by            {Humanize.Placeholder(UnknownReason.NotPermitted)}");

    if (image.SizeBytes.HasValue)
      Console.WriteLine($"  size                 {Humanize.Bytes(image.SizeBytes)}{(image.ModifiedUtc is { } when ? $", last written {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z" : string.Empty)}");

    // Only where the file system carries a birth time. Most do not, and a line saying so would be a
    // line on most rows of most machines.
    if (image.CreatedUtc is { } created)
      Console.WriteLine($"  created              {created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z");

    // From the modules the process has mapped, not from what it is called (PRD §5.3).
    if (image.Runtime != ProcessRuntime.Unknown)
      Console.WriteLine($"  running              {image.Runtime.Text()}");

    if (image.WorkingDirectory is { Length: > 0 } directory)
      Console.WriteLine($"  running in           {directory}");

    if (image.Namespaces.Count > 0) {
      var names = new List<string>();
      foreach (var (kind, inode) in image.Namespaces)
        names.Add($"{kind}:{inode}");

      Console.WriteLine($"  namespaces           {string.Join("  ", names)}");
    }

    Console.WriteLine();
  }

  /// <summary>
  /// A quota as a number of cores, because that is the sentence somebody wants.
  /// </summary>
  /// <remarks>
  /// No quota is "unlimited" rather than a very large number: unlimited is not a quantity, and this
  /// is the difference between a process that is being held back and one that simply is not busy.
  /// </remarks>
  private static string Cores(double? quota)
    => quota is { } cores
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.##} core{1}", cores, cores == 1 ? string.Empty : "s")
      : "unlimited";

  private static string Limit(Counter counter)
    => counter.HasValue ? Humanize.Bytes(counter) : "unlimited";

  private static string Pressure(PressureReading reading)
    => reading.Some.HasValue
      ? $"{Humanize.Percent(reading.Some.Average10)} % · {Humanize.Percent(reading.Some.Average60)} % · {Humanize.Percent(reading.Some.Average300)} %"
      : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

}
