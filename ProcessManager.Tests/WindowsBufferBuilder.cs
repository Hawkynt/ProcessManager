using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// One <c>SYSTEM_PROCESS_INFORMATION</c> entry, laid out the way the kernel lays one out.
/// </summary>
/// <remarks>
/// The same caveat the fuller builder in <see cref="WindowsProcessInformationReplayTests"/> carries:
/// the buffer is synthesised from the struct definition the parser reads, so it cannot catch the
/// definition itself being wrong. What it does catch is the mapping from a field of that structure
/// to a field of the record — which is what a new column is (PRD §9.4).
/// <para>
/// A single process rather than a chain, because every caller here is asking what one entry becomes.
/// The chain walk is tested where it belongs.
/// </para>
/// </remarks>
internal static class WindowsBufferBuilder {

  public static (byte[] Buffer, nint BaseAddress, GCHandle Handle) Build(
    int basePriority = 8,
    ulong privateBytes = 104_857_600,
    ulong peakPrivateBytes = 209_715_200
  ) {
    var size = Marshal.SizeOf<NtStructures.SystemProcessInformation>();
    var buffer = new byte[(size + 7) & ~7];
    var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

    var entry = new NtStructures.SystemProcessInformation {
      NextEntryOffset = 0,
      NumberOfThreads = 1,
      CreateTime = 133_100_000_000_000_000L,
      BasePriority = basePriority,
      UniqueProcessId = 1234,
      InheritedFromUniqueProcessId = 4,
      HandleCount = 42,
      SessionId = 1,
      PrivatePageCount = (nuint)privateBytes,
      PagefileUsage = (nuint)privateBytes,
      PeakPagefileUsage = (nuint)peakPrivateBytes,
      WorkingSetSize = (nuint)privateBytes,
      WorkingSetPrivateSize = (long)privateBytes,
      VirtualSize = (nuint)(privateBytes * 4),
      PageFaultCount = 17,
      CycleTime = 999,
    };

    MemoryMarshal.Write(buffer.AsSpan(), in entry);
    return (buffer, handle.AddrOfPinnedObject(), handle);
  }

}
