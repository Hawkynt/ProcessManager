using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What the machine is (PRD §46, §96), from the processor itself and from the kernel's topology.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsHostReader {

  public static HostInfo Read() {
    var topology = ReadTopology();
    var brand = CpuBrand.Read();
    var memory = ReadMemoryHardware();

    return new() {
      HostName = Environment.MachineName,
      OperatingSystem = RuntimeInformation.OSDescription,
      OperatingSystemVersion = Environment.OSVersion.Version.ToString(),
      Architecture = RuntimeInformation.OSArchitecture.ToString(),

      CpuModel = brand.Model,
      CpuVendor = brand.Vendor,
      CpuBaseHertz = CpuBrand.BaseHertzFrom(brand.Model),
      CpuFeatures = ReadFeatures(),
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

      // The firmware's own table, through GetSystemFirmwareTable — which, unlike the file Linux keeps
      // the same bytes in, needs no elevation at all (PRD §47).
      InstalledMemoryBytes = memory.InstalledBytes,
      MemoryTransfersPerSecond = memory.TransfersPerSecond,
      MemoryFormFactor = memory.FormFactor,
      MemorySlotsUsed = memory.SlotsUsed,
      MemorySlotsTotal = memory.SlotsTotal,

      // Not in a type-17 record and not anywhere else Windows publishes: the record describes a
      // device and its slot, never the controller's interleave. Refused rather than inferred from
      // the locator strings, which are vendor-formatted text (PRD §47, §5.3).
      MemoryChannels = Counter.NotSupported,
    };
  }

  /// <summary>
  /// Where each logical processor sits, for the heat map (PRD §46).
  /// </summary>
  /// <remarks>
  /// The same buffer <see cref="ReadTopology"/> counts, read for placement instead. Two calls rather
  /// than one because the counts are wanted at startup and the map only when somebody opens the
  /// processor page, and the buffer is a few kilobytes either way.
  /// </remarks>
  public static CpuTopology ReadPlacement() => WithBuffer(LogicalProcessorInformation.ParseTopology, CpuTopology.Empty);

  /// <summary>
  /// What the processor can do — from <c>CPUID</c> where there is one, and from Windows' own list of
  /// questions where there is not (PRD §46).
  /// </summary>
  /// <remarks>
  /// <c>X86Base.CpuId</c> is the same implementation the Linux side uses and the runtime emits it as
  /// the instruction, so the x86 answer costs no interop at all. ARM64 has no such instruction
  /// reachable from user code and no auxiliary vector to read instead, which leaves
  /// <c>IsProcessorFeaturePresent</c>: one call per feature, asked once.
  /// </remarks>
  private static IReadOnlyList<Query.CpuFeature> ReadFeatures() {
    if (Query.CpuId.IsSupported)
      return Query.CpuId.Features;

    return RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
      ? Query.WindowsProcessorFeatures.Decode(Native.IsProcessorFeaturePresent)
      : [];
  }

  /// <summary>
  /// The memory modules, out of the firmware's own SMBIOS table (PRD §47).
  /// </summary>
  /// <remarks>
  /// The <c>RSMB</c> provider hands back a four-byte version header and a length before the table
  /// itself, and the table after it is byte for byte the one Linux keeps in
  /// <c>/sys/firmware/dmi/tables/DMI</c> — so both operating systems hand the same bytes to the same
  /// parser, and the parser is tested on machines that have neither.
  /// </remarks>
  private static Smbios.MemoryHardware ReadMemoryHardware() {
    const int headerLength = 8;
    const int lengthOffset = 4;

    var length = Native.GetSystemFirmwareTable(Native.RawSmbiosProvider, 0, 0, 0);
    if (length <= headerLength)
      return Smbios.MemoryHardware.Unreadable(UnknownReason.NotSupportedOnPlatform);

    var buffer = Marshal.AllocHGlobal((int)length);
    try {
      if (Native.GetSystemFirmwareTable(Native.RawSmbiosProvider, 0, buffer, length) is 0 or > int.MaxValue)
        return Smbios.MemoryHardware.Unreadable(UnknownReason.NotSupportedOnPlatform);

      var raw = Span(buffer, (int)length);
      // The header's own length field, clamped to what actually arrived: firmware that overstates it
      // would otherwise walk the parser off the end of the allocation.
      var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[lengthOffset..]);
      var table = raw[headerLength..];
      if (declared > 0 && declared < (uint)table.Length)
        table = table[..(int)declared];

      return Smbios.ReadMemory(table);
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// Calls the topology API twice: once to be told the size, once to fill it.
  /// </summary>
  /// <remarks>
  /// The buffer is variable-length and there is no useful upper bound — a large server reports
  /// hundreds of records — so guessing a size and hoping is not an option.
  /// </remarks>
  private static LogicalProcessorInformation.Topology ReadTopology()
    => WithBuffer(LogicalProcessorInformation.Parse, default);

  /// <summary>A reader of the buffer, which cannot be a <see cref="Func{T, TResult}"/> over a span.</summary>
  private delegate T FromBuffer<out T>(ReadOnlySpan<byte> buffer);

  private static T WithBuffer<T>(FromBuffer<T> read, T whenAbsent) {
    uint length = 0;
    Native.GetLogicalProcessorInformationEx(Native.RelationAll, 0, ref length);
    if (length == 0)
      return whenAbsent;

    var buffer = Marshal.AllocHGlobal((int)length);
    try {
      return Native.GetLogicalProcessorInformationEx(Native.RelationAll, buffer, ref length)
        ? read(Span(buffer, (int)length))
        : whenAbsent;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static unsafe ReadOnlySpan<byte> Span(nint pointer, int length)
    => new((void*)pointer, length);

}
