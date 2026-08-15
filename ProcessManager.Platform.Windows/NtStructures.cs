using System.Runtime.InteropServices;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The layouts <c>NtQuerySystemInformation</c> writes.
/// </summary>
/// <remarks>
/// These are documented-by-observation rather than by contract, so they are pinned by explicit
/// offsets and by a replay test over a captured buffer (PRD §9.4) rather than trusted to survive a
/// Windows release unnoticed.
/// </remarks>
internal static class NtStructures {

  /// <summary>
  /// <c>SYSTEM_PROCESS_INFORMATION</c> on 64-bit Windows.
  /// </summary>
  /// <remarks>
  /// The whole process list arrives as a chain of these, each followed by its threads, with
  /// <see cref="NextEntryOffset"/> linking one to the next and 0 ending the chain. One call, no
  /// <c>OpenProcess</c> per process — which is why the Windows sample can be cheaper than the Linux
  /// one despite reporting more (PRD §5.2).
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  public struct SystemProcessInformation {
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public UnicodeString ImageName;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
  }

  /// <summary>One thread, of which <c>NumberOfThreads</c> follow each process block.</summary>
  [StructLayout(LayoutKind.Sequential)]
  public struct SystemThreadInformation {
    public long KernelTime;
    public long UserTime;
    public long CreateTime;
    public uint WaitTime;
    public nint StartAddress;
    public ClientId ClientId;
    public int Priority;
    public int BasePriority;
    public uint ContextSwitches;
    public uint ThreadState;
    public uint WaitReason;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct ClientId {
    public nint UniqueProcess;
    public nint UniqueThread;
  }

  /// <summary>
  /// A counted UTF-16 string that points into the same buffer. <see cref="Length"/> is in
  /// <em>bytes</em>, not characters — the single most common way to read one of these wrongly.
  /// </summary>
  [StructLayout(LayoutKind.Sequential)]
  public struct UnicodeString {
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
  }

  /// <summary>Per-core CPU times, in 100 ns units. <c>KernelTime</c> includes <c>IdleTime</c>.</summary>
  [StructLayout(LayoutKind.Sequential)]
  public struct SystemProcessorPerformanceInformation {
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct PerformanceInformation {
    public uint Size;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
  }

  /// <summary>
  /// One entry of <c>SystemExtendedHandleInformation</c>: a handle, and which process holds it.
  /// </summary>
  [StructLayout(LayoutKind.Sequential)]
  public struct SystemHandleTableEntryInfoEx {
    public nuint Object;
    public nuint UniqueProcessId;
    public nint HandleValue;
    public uint GrantedAccess;
    public ushort CreatorBackTraceIndex;
    public ushort ObjectTypeIndex;
    public uint HandleAttributes;
    public uint Reserved;
  }

  /// <summary>The header the entries follow.</summary>
  [StructLayout(LayoutKind.Sequential)]
  public struct SystemHandleInformationEx {
    public nuint NumberOfHandles;
    public nuint Reserved;
  }

  /// <summary>
  /// <c>MODULEENTRY32W</c>. The two fixed-length paths are why it is this large.
  /// </summary>
  /// <remarks>
  /// The name buffers are <c>fixed char</c> rather than <c>[MarshalAs(ByValTStr)]</c> strings,
  /// because the P/Invoke source generator only marshals blittable structs and a string field is not
  /// one. Reading them by hand is the price of not falling back to the old marshaller, which would
  /// have cost a copy of both 500-odd character buffers on every module of every call.
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  public unsafe struct ModuleEntry32 {
    public uint Size;
    public uint ModuleId;
    public uint ProcessId;
    public uint GlblcntUsage;
    public uint ProccntUsage;
    public nint ModuleBaseAddress;
    public uint ModuleBaseSize;
    public nint ModuleHandle;
    public fixed char Module[256];
    public fixed char ExePath[260];

    /// <summary>The module's own file name, up to its NUL.</summary>
    public string ReadModule() {
      fixed (char* pointer = this.Module)
        return ReadFixed(pointer, 256);
    }

    /// <summary>The full path the module was loaded from, up to its NUL.</summary>
    public string ReadExePath() {
      fixed (char* pointer = this.ExePath)
        return ReadFixed(pointer, 260);
    }

    private static string ReadFixed(char* pointer, int capacity) {
      var length = 0;
      while (length < capacity && pointer[length] != '\0')
        ++length;

      return new(pointer, 0, length);
    }
  }

  public const int SystemProcessInformationClass = 5;
  public const int SystemProcessorPerformanceInformationClass = 8;

  public const uint STATUS_SUCCESS = 0x00000000;
  public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

}
