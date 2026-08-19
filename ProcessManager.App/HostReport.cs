using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--host</c>: what this machine is, rather than what it is doing (PRD §96, §46, §47).
/// </summary>
/// <remarks>
/// The sections come from <see cref="PerformanceReport"/>, which is also what the desktop's
/// performance view draws. The two therefore cannot disagree about the machine, and the content is
/// unit-tested in a way a window is not (PRD §58).
/// </remarks>
internal static class HostReport {

  public static int Run(Sampler sampler, ISystemProbe probe) {
    var host = probe.DescribeHost();

    // Twice, an interval apart: utilisation is a rate, and a page reporting "…" for it is not what
    // anybody meant by asking what the machine is doing (PRD §3.2).
    sampler.Sample();
    Thread.Sleep(250);
    sampler.Sample();

    var first = true;
    var sections = PerformanceReport.Build(
      host,
      sampler.Current,
      sampler.Delta,
      probe.DescribeDisk,
      probe.DescribeInterface
    );

    foreach (var section in sections) {
      if (!first)
        Console.WriteLine();

      first = false;
      Console.WriteLine(section.Title);
      foreach (var row in section.Rows)
        Console.WriteLine($"  {row.Label,-20} {row.Value}");
    }

    Console.WriteLine();
    Console.WriteLine($"  {"Probe",-20} {probe.Description}");

    // The firmware facts need root on every distribution that ships them, so say why rather than
    // leaving three dashes for the reader to interpret (PRD §7).
    if (host.MemoryTransfersPerSecond.Reason == UnknownReason.NotPermitted) {
      Console.WriteLine();
      Console.WriteLine("Memory speed, form factor and slot counts come from the firmware's SMBIOS");
      Console.WriteLine("tables, which are readable only by root.");
    }

    return 0;
  }

}
