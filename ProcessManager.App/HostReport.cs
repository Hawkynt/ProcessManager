using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--host</c>: what this machine is, rather than what it is doing (PRD §96, §46, §47).
/// </summary>
/// <remarks>
/// The same figures the Performance page will show when there is one. Written as a command first
/// because a command can be tested, scripted and pasted into a bug report, and a page cannot.
/// </remarks>
internal static class HostReport {

  public static int Run(Sampler sampler, ISystemProbe probe) {
    var host = probe.DescribeHost();
    sampler.Sample();
    var snapshot = sampler.Current;

    Write("Host", host.HostName);
    Write("OS", host.OperatingSystem);
    Write("Kernel", host.OperatingSystemVersion);
    Write("Architecture", host.Architecture);
    if (host.Virtualisation is { } virtualisation)
      Write("Virtualised", virtualisation);

    Write("Uptime", Uptime(snapshot.System.UptimeSeconds));
    Console.WriteLine();

    Write("Processor", host.CpuModel ?? "—");
    Write("Vendor", host.CpuVendor ?? "—");
    Write("Base speed", Hertz(host.CpuBaseHertz));
    Write("Current speed", Hertz(host.CpuCurrentHertz));
    Write("Sockets", Humanize.Count(host.Sockets));
    Write("Physical cores", Humanize.Count(host.PhysicalCores));
    Write("Logical processors", Humanize.Count(host.LogicalProcessors));
    Write("NUMA nodes", Humanize.Count(host.NumaNodes));
    Write("L1 data", Humanize.Bytes(host.L1DataBytes));
    Write("L1 instruction", Humanize.Bytes(host.L1InstructionBytes));
    Write("L2", Humanize.Bytes(host.L2Bytes));
    Write("L3", Humanize.Bytes(host.L3Bytes));
    Console.WriteLine();

    Write("Memory", Humanize.Bytes(snapshot.System.TotalMemoryBytes));
    Write("Available", Humanize.Bytes(snapshot.System.AvailableMemoryBytes));
    Write("Cached", Humanize.Bytes(snapshot.System.CachedMemoryBytes));
    Write("Swap", $"{Humanize.Bytes(snapshot.System.UsedSwapBytes)} of {Humanize.Bytes(snapshot.System.TotalSwapBytes)}");
    Write("Speed", Transfers(host.MemoryTransfersPerSecond));
    Write("Form factor", host.MemoryFormFactor ?? Humanize.Placeholder(UnknownReason.NotPermitted));
    Write("Slots used", Slots(host.MemorySlotsUsed, host.MemorySlotsTotal));
    Console.WriteLine();

    Write("Processes", snapshot.ProcessCount.ToString(CultureInfo.InvariantCulture));
    Write("Threads", snapshot.System.TotalThreads.ToString(CultureInfo.InvariantCulture));
    Write("Load average", $"{snapshot.System.LoadAverage1:0.00} {snapshot.System.LoadAverage5:0.00} {snapshot.System.LoadAverage15:0.00}");
    Write("Probe", probe.Description);

    // The firmware facts need root on every distribution that ships them, so say why rather than
    // leaving three dashes for the reader to interpret (PRD §7).
    if (host.MemoryTransfersPerSecond.Reason == UnknownReason.NotPermitted) {
      Console.WriteLine();
      Console.WriteLine("Memory speed, form factor and slot counts come from the firmware's SMBIOS");
      Console.WriteLine("tables, which are readable only by root.");
    }

    return 0;
  }

  private static void Write(string label, string value) => Console.WriteLine($"{label,-20} {value}");

  private static string Hertz(Counter counter) {
    if (!counter.HasValue)
      return Humanize.Placeholder(counter.Reason);

    var hertz = (double)counter.Value;
    return hertz >= 1_000_000_000
      ? (hertz / 1_000_000_000).ToString("0.00", CultureInfo.InvariantCulture) + " GHz"
      : (hertz / 1_000_000).ToString("0", CultureInfo.InvariantCulture) + " MHz";
  }

  private static string Transfers(Counter counter) => counter.HasValue
    ? (counter.Value / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + " MT/s"
    : Humanize.Placeholder(counter.Reason);

  private static string Slots(Counter used, Counter total) {
    if (!used.HasValue)
      return Humanize.Placeholder(used.Reason);

    return total.HasValue
      ? $"{used.Value} of {total.Value}"
      : used.Value.ToString(CultureInfo.InvariantCulture);
  }

  private static string Uptime(double seconds) {
    if (!double.IsFinite(seconds) || seconds <= 0)
      return "—";

    var span = TimeSpan.FromSeconds(seconds);
    return span.TotalDays >= 1
      ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
  }

}
