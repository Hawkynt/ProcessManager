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
    Console.WriteLine($"  container            {Container(cgroup.Path)}");
    Console.WriteLine($"  controllers          {(cgroup.Controllers.Count > 0 ? string.Join(", ", cgroup.Controllers) : "none enabled here")}");

    // A limit and what is being used against it, on one line each: either alone answers half the
    // question somebody asked.
    Console.WriteLine($"  processor            {Cores(cgroup.CpuQuotaCores, cgroup.Has("cpu"))}");
    Console.WriteLine($"  throttled            {Humanize.Count(cgroup.ThrottledCount)}");
    Console.WriteLine($"  memory               {Humanize.Bytes(cgroup.MemoryCurrentBytes)} of {Limit(cgroup.MemoryMaxBytes)}");
    Console.WriteLine($"  memory, soft cap     {Limit(cgroup.MemoryHighBytes)}");
    // Tasks and not processes. `pids.current` counts threads, so a cgroup with 58 processes in it
    // routinely reports 892 — and printing that under a heading of "processes" is a figure wrong by
    // an order of magnitude with nothing to say so. It is what systemd calls TasksMax (PRD §5.3).
    Console.WriteLine($"  tasks                {Humanize.Count(cgroup.PidsCurrent)} of {TaskLimit(cgroup.PidsMax)}");
    Console.WriteLine("  a task is a thread, so a process with eight threads counts as eight");
    Console.WriteLine();
    Console.WriteLine($"  stalled on CPU       {Pressure(cgroup.CpuPressure)}");
    Console.WriteLine($"  stalled on memory    {Pressure(cgroup.MemoryPressure)}");
    Console.WriteLine($"  stalled on I/O       {Pressure(cgroup.IoPressure)}");
    Console.WriteLine($"  frozen               {Frozen(cgroup.Freezer)}");
    Disk(cgroup);
    Hierarchy(cgroup);
    return 0;
  }

  /// <summary>
  /// What each block device is allowed, from <c>io.max</c> (PRD §38).
  /// </summary>
  /// <remarks>
  /// Per device, because that is what the limit is. And an absent file is not an absent limit: where
  /// the controller is not enabled here, whatever an ancestor throttles is what governs, and saying
  /// "unlimited" would send somebody looking anywhere but at the thing holding their process up.
  /// </remarks>
  private static void Disk(CgroupInfo cgroup) {
    switch (cgroup.IoLimitsReason) {
      case UnknownReason.NotSupportedOnPlatform:
        Console.WriteLine("  disk                 no such controller here — an ancestor's throttling applies");
        return;
      case UnknownReason.NoLimit:
        Console.WriteLine("  disk                 the io controller is on here and nothing is capped");
        return;
    }

    // On IsLimited and not on the number of lines: the kernel writes a line for a device with all
    // four directions set to max, which is a device it is not throttling. A bare "disk" heading with
    // nothing under it is what counting lines produced.
    var capped = 0;
    foreach (var limit in cgroup.Io)
      if (limit.IsLimited)
        ++capped;

    if (capped == 0) {
      Console.WriteLine("  disk                 the io controller is on here and nothing is capped");
      return;
    }

    Console.WriteLine("  disk");
    foreach (var limit in cgroup.Io) {
      if (!limit.IsLimited)
        continue;

      Console.WriteLine(
        $"    {limit.Name,-16} read {Throughput(limit.ReadBytesPerSecond)}, write {Throughput(limit.WriteBytesPerSecond)}"
        + $", read {Operations(limit.ReadOperationsPerSecond)}, write {Operations(limit.WriteOperationsPerSecond)}"
      );
    }
  }

  /// <summary>
  /// Every cgroup between the root and this one, and which of them sets each ceiling (PRD §38).
  /// </summary>
  /// <remarks>
  /// The half of the answer that a single cgroup cannot give. A quota on an ancestor governs
  /// everything below it, and the group a process is actually in very often sets nothing at all — so
  /// reading only that group reports "unlimited" about a process being held to a tenth of a core two
  /// levels up, which is precisely the situation somebody runs this for.
  /// </remarks>
  private static void Hierarchy(CgroupInfo cgroup) {
    if (cgroup.Chain.Count == 0)
      return;

    Console.WriteLine();
    Console.WriteLine("what is in force, and which cgroup sets it");

    var (cores, quotaReason, path, unit) = cgroup.TightestCpuQuota();
    Console.WriteLine($"  processor            {InForceCores(cores, quotaReason, path, unit)}");

    Console.WriteLine($"  memory               {InForce(cgroup.TightestMemoryLimit(), static counter => Humanize.Bytes(counter))}");
    Console.WriteLine($"  tasks                {InForce(cgroup.TightestTaskLimit(), static counter => Humanize.Count(counter))}");
    Console.WriteLine();
    Console.WriteLine("the chain, outermost first — each level's limit applies to everything below it");
    foreach (var level in cgroup.Chain)
      Console.WriteLine($"  {level.Path,-52} {Level(level)}");
  }

  /// <summary>
  /// The quota in force, and which of the four kinds of "none" it is when there is none.
  /// </summary>
  /// <remarks>
  /// A file that would not parse is not a chain with no quota in it. Saying "no quota anywhere"
  /// about a <c>cpu.max</c> nobody could read is the reassuring half of §72.3's mistake, and it is
  /// the half somebody acts on by going to look somewhere else.
  /// </remarks>
  private static string InForceCores(double? cores, UnknownReason reason, string? path, string? unit) {
    if (cores is { } value)
      return string.Format(
        CultureInfo.InvariantCulture,
        "{0:0.##} core{1} — set by {2}",
        value,
        value == 1 ? string.Empty : "s",
        unit ?? path
      );

    return reason switch {
      UnknownReason.NoLimit => "unlimited all the way up",
      UnknownReason.NotSupportedOnPlatform => "no cgroup in the chain has the cpu controller on",
      _ => $"{Humanize.Placeholder(reason)} — a cpu.max in the chain could not be read",
    };
  }

  private static string InForce(CgroupCeiling ceiling, Func<Counter, string> format) {
    if (ceiling.Path is not null)
      return $"{format(ceiling.Value)} — set by {ceiling.Unit ?? ceiling.Path}";

    return ceiling.Value.Reason switch {
      UnknownReason.NoLimit => "unlimited all the way up",
      UnknownReason.NotSupportedOnPlatform => "no cgroup in the chain has that controller on",
      // A limit file that was there and would not read. Not the same as there being none, and the
      // difference is what somebody would act on (PRD §72.3).
      var reason => $"{Humanize.Placeholder(reason)} — a limit in the chain could not be read",
    };
  }

  /// <summary>What one level sets, and nothing about what it does not.</summary>
  private static string Level(CgroupLevel level) {
    var parts = new List<string>();
    if (level.CpuQuotaCores is { } cores)
      parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.##} core{1}", cores, cores == 1 ? string.Empty : "s"));

    if (level.MemoryMaxBytes.HasValue)
      parts.Add(Humanize.Bytes(level.MemoryMaxBytes) + " memory");

    if (level.PidsMax.HasValue)
      parts.Add(Humanize.Count(level.PidsMax) + " tasks");

    foreach (var limit in level.IoLimits)
      if (limit.IsLimited)
        parts.Add(limit.Name + " capped");

    return parts.Count == 0 ? "sets no limit" : string.Join(", ", parts);
  }

  /// <summary>
  /// A throughput ceiling, or which kind of "no ceiling" this is.
  /// </summary>
  /// <remarks>
  /// Only the literal word <c>max</c> is "unlimited". A value the parser could not read is a hole
  /// and says so: printing "unlimited" over it would turn a file nobody could read into a promise
  /// that nothing is holding the group back, which is the confident answer §72.3 exists to stop —
  /// and the inverted form of it, since the wrong answer here is reassuring rather than alarming.
  /// </remarks>
  private static string Throughput(Counter counter) => counter.Reason switch {
    UnknownReason.None => Humanize.Bytes(counter) + "/s",
    UnknownReason.NoLimit => "unlimited",
    _ => Humanize.Placeholder(counter.Reason),
  };

  private static string Operations(Counter counter) => counter.Reason switch {
    UnknownReason.None => Humanize.Count(counter) + " ops/s",
    UnknownReason.NoLimit => "unlimited",
    _ => Humanize.Placeholder(counter.Reason),
  };

  /// <summary>
  /// What is running the process, where something other than the machine is (PRD §38).
  /// </summary>
  /// <remarks>
  /// The name only where the machine itself knows it. LXC and <c>machined</c> put it in the cgroup
  /// path; Docker and its relatives put an id there and keep the name in their own daemon, and the
  /// line says which of those it is rather than leaving a blank that reads as "it has none".
  /// </remarks>
  private static string Container(string? path) {
    var container = ContainerDetector.Of(path);
    if (!container.IsIdentified)
      return container.Runtime == ContainerRuntime.None
        ? "none — the cgroup path names no container"
        : "not known";

    if (container.Name is { Length: > 0 } name)
      return $"{container.RuntimeName}, {name}";

    return container.Id is { Length: > 0 } id
      ? $"{container.RuntimeName}, {id} — its name is in the runtime's own daemon rather than on this machine"
      : container.RuntimeName;
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
  /// <summary>
  /// A quota as a number of cores, or which kind of "no quota" this is.
  /// </summary>
  /// <remarks>
  /// Absent and unlimited read the same out of the file — there is simply no <c>cpu.max</c> either
  /// way — so the controller list is what tells them apart. A group with the processor controller
  /// switched off is governed by an ancestor's quota, and calling that "unlimited" sends somebody
  /// looking in the wrong place when their process is plainly being held back.
  /// </remarks>
  private static string Cores(double? quota, bool controllerEnabled) {
    if (quota is { } cores)
      return string.Format(CultureInfo.InvariantCulture, "{0:0.##} core{1}", cores, cores == 1 ? string.Empty : "s");

    return controllerEnabled ? "unlimited" : "no such controller here — an ancestor's limit applies";
  }

  /// <summary>
  /// A ceiling, or which kind of "no ceiling" this is.
  /// </summary>
  /// <remarks>
  /// The two were one word until recently and they are not the same thing. A group whose controller
  /// is switched off is governed by whatever an ancestor sets, and somebody reading "unlimited"
  /// against it would go looking for the wrong thing when the process hit a wall.
  /// </remarks>
  private static string Limit(Counter counter) => counter.Reason switch {
    UnknownReason.None => Humanize.Bytes(counter),
    UnknownReason.NoLimit => "unlimited",
    UnknownReason.NotSupportedOnPlatform => "no such controller here — an ancestor's limit applies",
    _ => Humanize.Placeholder(counter.Reason),
  };

  /// <summary>
  /// The same three answers, for a ceiling that is a count of things rather than a size.
  /// </summary>
  /// <remarks>
  /// <c>pids.max</c> went through the byte formatter, which divides by 1024 and appends a binary
  /// suffix: a limit of 153 425 tasks printed as <c>150K</c>, which is both the wrong number and a
  /// unit the thing being counted does not have. Tasks are counted one at a time (PRD §5.3).
  /// </remarks>
  private static string TaskLimit(Counter counter) => counter.Reason switch {
    UnknownReason.None => Humanize.Count(counter),
    UnknownReason.NoLimit => "unlimited",
    UnknownReason.NotSupportedOnPlatform => "no such controller here — an ancestor's limit applies",
    _ => Humanize.Placeholder(counter.Reason),
  };

  private static string Pressure(PressureReading reading)
    => reading.Some.HasValue
      ? $"{Humanize.Percent(reading.Some.Average10)} % · {Humanize.Percent(reading.Some.Average60)} % · {Humanize.Percent(reading.Some.Average300)} %"
      : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

}
