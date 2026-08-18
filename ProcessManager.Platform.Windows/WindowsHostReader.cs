using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What the machine is (PRD §46, §96), from the processor itself and from the kernel's topology.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHostReader {

  public static HostInfo Read() {
    var topology = ReadTopology();
    var brand = CpuBrand.Read();

    return new() {
      HostName = Environment.MachineName,
      OperatingSystem = RuntimeInformation.OSDescription,
      OperatingSystemVersion = Environment.OSVersion.Version.ToString(),
      Architecture = RuntimeInformation.OSArchitecture.ToString(),

      CpuModel = brand.Model,
      CpuVendor = brand.Vendor,
      CpuBaseHertz = CpuBrand.BaseHertzFrom(brand.Model),
      // Windows exposes the live frequency only through a performance counter or a processor power
      // interface, neither of which is worth opening on every call to describe the machine.
      CpuCurrentHertz = Counter.Unknown(UnknownReason.NotImplementedHere),

      PhysicalCores = topology.PhysicalCores,
      // The topology walk is authoritative; ProcessorCount is the fallback when it is not.
      LogicalProcessors = topology.LogicalProcessors.HasValue
        ? topology.LogicalProcessors
        : Counter.Of((ulong)Environment.ProcessorCount),

      Sockets = topology.Sockets,
      NumaNodes = topology.NumaNodes,
      L1DataBytes = topology.L1Data,
      L1InstructionBytes = topology.L1Instruction,
      L2Bytes = topology.L2,
      L3Bytes = topology.L3,

      // SMBIOS is reachable through GetSystemFirmwareTable, and parsing a type-17 record is a job of
      // its own. Said plainly rather than left blank (PRD §7).
      MemoryTransfersPerSecond = Counter.Unknown(UnknownReason.NotImplementedHere),
      MemorySlotsUsed = Counter.Unknown(UnknownReason.NotImplementedHere),
      MemorySlotsTotal = Counter.Unknown(UnknownReason.NotImplementedHere),
    };
  }

  /// <summary>
  /// Calls the topology API twice: once to be told the size, once to fill it.
  /// </summary>
  /// <remarks>
  /// The buffer is variable-length and there is no useful upper bound — a large server reports
  /// hundreds of records — so guessing a size and hoping is not an option.
  /// </remarks>
  private static LogicalProcessorInformation.Topology ReadTopology() {
    uint length = 0;
    Native.GetLogicalProcessorInformationEx(Native.RelationAll, 0, ref length);
    if (length == 0)
      return default;

    var buffer = Marshal.AllocHGlobal((int)length);
    try {
      return Native.GetLogicalProcessorInformationEx(Native.RelationAll, buffer, ref length)
        ? LogicalProcessorInformation.Parse(Span(buffer, (int)length))
        : default;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static unsafe ReadOnlySpan<byte> Span(nint pointer, int length)
    => new((void*)pointer, length);

}
