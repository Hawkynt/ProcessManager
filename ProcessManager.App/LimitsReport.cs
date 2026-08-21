using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// What a process's cgroup allows it, and what it is using of that (PRD §38).
/// </summary>
/// <remarks>
/// The answer to "why is this slow when the machine is idle". A container or a systemd unit can be
/// throttled to a fraction of a core or capped well below the machine's memory, and nothing in a
/// process table shows that — the process simply appears to be doing less than it should.
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
      // Not an error. A machine on cgroup v1, or a process in no cgroup at all, is an ordinary
      // situation and the honest answer is that there are no limits to report (PRD §5.3).
      Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)}) is in no cgroup this build can read.");
      Console.WriteLine("Only the unified hierarchy (cgroup v2) is read; v1 splits a process across several.");
      return 0;
    }

    Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)})");
    Image(probe, key);
    Console.WriteLine($"  cgroup               {cgroup.Path}");
    Console.WriteLine($"  controllers          {(cgroup.Controllers.Count > 0 ? string.Join(", ", cgroup.Controllers) : "none enabled here")}");

    // A limit and what is being used against it, on one line each: either alone answers half the
    // question somebody asked.
    Console.WriteLine($"  processor            {Cores(cgroup.CpuQuotaCores)}");
    Console.WriteLine($"  throttled            {Humanize.Count(cgroup.ThrottledCount)}");
    Console.WriteLine($"  memory               {Humanize.Bytes(cgroup.MemoryCurrentBytes)} of {Limit(cgroup.MemoryMaxBytes)}");
    Console.WriteLine($"  memory, soft cap     {Limit(cgroup.MemoryHighBytes)}");
    Console.WriteLine($"  processes            {Humanize.Count(cgroup.PidsCurrent)} of {Limit(cgroup.PidsMax)}");
    Console.WriteLine();
    Console.WriteLine($"  stalled on CPU       {Pressure(cgroup.CpuPressure)}");
    Console.WriteLine($"  stalled on memory    {Pressure(cgroup.MemoryPressure)}");
    Console.WriteLine($"  stalled on I/O       {Pressure(cgroup.IoPressure)}");
    return 0;
  }

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
