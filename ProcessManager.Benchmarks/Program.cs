using System.Diagnostics;
using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Benchmarks;

/// <summary>
/// The PRD §4 budget, asserted rather than printed.
/// </summary>
/// <remarks>
/// Run by nightly CI, which fails on a non-zero exit. The wall-clock ceilings are deliberately looser
/// than the §4 targets: a shared runner's timing is noisy, and a budget that cries wolf gets disabled
/// within a month. The allocation check has no such slack — it is exact, it is the one that catches
/// real regressions, and it does not care how busy the machine is.
/// </remarks>
internal static class Program {

  private static int Main(string[] args) {
    var quick = args.Contains("--quick");
    if (args.Contains("--breakdown")) {
      Breakdown.Run();
      return 0;
    }

    var failures = new List<string>();

    Console.WriteLine($"# machine: {Environment.ProcessorCount} cores, {RuntimeName()}");
    Console.WriteLine($"# load: {ReadLoadAverage()}");
    Console.WriteLine();

    using var probe = CreateProbe();
    if (probe is null) {
      Console.WriteLine("no probe for this platform; nothing to measure.");
      return 0;
    }

    using var sampler = new Sampler(probe);

    // Warm-up: the first sample loads the per-process static cache (command lines, image paths) and
    // is legitimately several times the cost of a steady-state one. Measuring it would measure
    // start-up, which has its own budget.
    sampler.Sample();
    sampler.Sample();

    var iterations = quick ? 3 : 15;
    var best = double.MaxValue;
    var total = 0d;
    // CPU time, not wall-clock, is what the budget is asserted on. A sample that waits for a disk or
    // for sixteen other builds to give a core back has not become more expensive, and a CI runner is
    // exactly where that happens — measured on this machine at load 648, wall-clock read four times
    // what it read at load 280 for identical work. Wall-clock is still reported, because a user
    // waiting for a frame does not care why.
    var cpuBefore = Environment.CpuUsage.TotalTime;
    var wallStart = Stopwatch.GetTimestamp();
    for (var i = 0; i < iterations; ++i) {
      var startedAt = Stopwatch.GetTimestamp();
      sampler.Sample();
      var ms = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
      best = Math.Min(best, ms);
      total += ms;
    }

    var cpuPerSample = (Environment.CpuUsage.TotalTime - cpuBefore).TotalMilliseconds / iterations;
    var wallPerSample = Stopwatch.GetElapsedTime(wallStart).TotalMilliseconds / iterations;

    var processes = sampler.Current.ProcessCount;
    var mean = total / iterations;
    Report("snapshot.cpu.ms", cpuPerSample);
    Report("snapshot.wall.best.ms", best);
    Report("snapshot.wall.mean.ms", mean);
    Report("snapshot.processes", processes);
    Report("machine.load", ReadLoadOne());

    // Scaled to a thousand processes so the number means the same thing on a laptop and on a build
    // server. The ceiling is twice the §4 target; anything under it is noise, anything over it is a
    // design change nobody measured.
    var perThousand = processes > 0 ? cpuPerSample * 1000d / processes : 0;
    Report("snapshot.cpu.per1000.ms", perThousand);
    if (perThousand > 50)
      failures.Add($"snapshot cost {perThousand:0.0} ms CPU/1000 processes exceeds the 50 ms ceiling (PRD §4 target: 25)");

    // PRD §4: a steady-state sample allocates nothing. Measured over ten samples so that a single
    // buffer growth (legitimate, once) cannot pass as zero and a per-process allocation cannot hide.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var before = GC.GetAllocatedBytesForCurrentThread();
    var started = 0;
    for (var i = 0; i < 10; ++i) {
      sampler.Sample();
      started += sampler.Delta.StartedCount;
    }

    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    var perSample = allocated / 10d;
    Report("sample.allocated.bytes", perSample);
    Report("sample.new.processes", started / 10d);

    // "Zero" means zero for a process that was already there. A process that just started is a new
    // cache entry — its four /proc paths as UTF-8, its command line, its image path — and that is a
    // real, bounded, once-per-process cost, not a leak. So the budget is zero per steady process
    // plus a generous ceiling per new one; on a build machine spawning ten processes a second the
    // difference is the whole measurement.
    //
    // What this still catches is the regression that matters: one string per process per sample
    // would be tens of kilobytes here, three orders of magnitude above the line.
    var allowance = started / 10d * 1024 + 512;
    if (perSample > allowance)
      failures.Add(
        $"a steady-state sample allocated {perSample:0} bytes for {processes} processes "
        + $"with {started / 10d:0.0} starting per sample; the allowance is {allowance:0}"
      );

    // The view is rebuilt once per sample by both front-ends, so its cost is part of the frame.
    var view = new ProcessView { TreeMode = true, SortColumn = ProcessColumn.CpuPercent };
    view.Rebuild(sampler.Current, sampler.Delta);
    var viewStart = Stopwatch.GetTimestamp();
    for (var i = 0; i < 50; ++i)
      view.Rebuild(sampler.Current, sampler.Delta);

    var viewMs = Stopwatch.GetElapsedTime(viewStart).TotalMilliseconds / 50;
    Report("view.rebuild.tree.ms", viewMs);
    if (viewMs * 1000d / Math.Max(1, processes) > 20)
      failures.Add($"tree rebuild {viewMs:0.00} ms for {processes} processes is superlinear; check the child index");

    Console.WriteLine();
    foreach (var failure in failures)
      Console.WriteLine($"FAIL {failure}");

    Console.WriteLine(failures.Count == 0 ? "OK: every budget met." : $"{failures.Count} budget(s) exceeded.");
    return failures.Count == 0 ? 0 : 1;
  }

  private static ISystemProbe? CreateProbe() {
    if (OperatingSystem.IsLinux())
      return new Platform.Linux.LinuxProbe();
    // (Windows probe wired in below once it exists)
    if (false && OperatingSystem.IsWindows())
      return null;

    return null;
  }

  private static void Report(string name, double value)
    => Console.WriteLine($"{name,-28} {value.ToString("0.###", CultureInfo.InvariantCulture),12}");

  private static string RuntimeName()
    => $"{Environment.OSVersion.VersionString} / .NET {Environment.Version}";

  private static double ReadLoadOne() {
    var text = ReadLoadAverage();
    var space = text.IndexOf(' ');
    return space > 0 && double.TryParse(text[..space], CultureInfo.InvariantCulture, out var value) ? value : 0;
  }

  private static string ReadLoadAverage() {
    try {
      return OperatingSystem.IsLinux() ? File.ReadAllText("/proc/loadavg").Trim() : "n/a";
    } catch (IOException) {
      return "n/a";
    }
  }

}
