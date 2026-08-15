using System.Diagnostics;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Benchmarks;

/// <summary>
/// Attributes the sampling cost to the files it comes from, in CPU time. Run with --breakdown when a
/// budget regresses: the total on its own says the sample got slower, this says which read did.
/// </summary>
internal static class Breakdown {

  public static void Run() {
    Measure("full", new LinuxProbeOptions());
    Measure("no fd count", new LinuxProbeOptions { CountFileDescriptors = false });
    Measure("no fd, no cgroup", new LinuxProbeOptions { CountFileDescriptors = false, ReadCgroups = false });
    Measure("with PSS", new LinuxProbeOptions { UseProportionalSetSize = true });
  }

  private static void Measure(string label, LinuxProbeOptions options) {
    using var probe = new LinuxProbe(options);
    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    probe.Sample(snapshot);

    const int iterations = 10;
    var cpuBefore = Environment.CpuUsage.TotalTime;
    var wall = Stopwatch.GetTimestamp();
    for (var i = 0; i < iterations; ++i)
      probe.Sample(snapshot);

    var cpu = (Environment.CpuUsage.TotalTime - cpuBefore).TotalMilliseconds / iterations;
    var wallMs = Stopwatch.GetElapsedTime(wall).TotalMilliseconds / iterations;
    Console.WriteLine($"{label,-22} cpu={cpu,7:0.0} ms  wall={wallMs,7:0.0} ms  ({snapshot.ProcessCount} procs, {cpu * 1000 / snapshot.ProcessCount:0} us/proc)");
  }

}
