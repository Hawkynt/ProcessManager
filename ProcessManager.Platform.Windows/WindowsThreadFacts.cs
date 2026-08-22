using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The three thread readings the bulk query does not carry (PRD §29).
/// </summary>
/// <remarks>
/// <para>
/// Everything else on a thread row comes out of one <c>NtQuerySystemInformation</c> call that
/// describes every thread on the machine at once. These three do not: each wants a handle on the
/// thread itself, which is one <c>OpenThread</c> and one query per thread.
/// </para>
/// <para>
/// That cost is why this runs only for the threads of the one process somebody is looking at, and
/// never over the table. A machine with four hundred processes has several thousand threads, and
/// three syscalls each for a page nobody has opened is exactly the reading §5.4 says to charge for
/// rather than take.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsThreadFacts {

  /// <summary>What a thread's handle was opened for.</summary>
  /// <remarks>
  /// <c>THREAD_QUERY_LIMITED_INFORMATION</c> is the one a non-elevated process gets for another
  /// user's thread and is enough for the cycle count and the ideal processor.
  /// <c>THREAD_QUERY_INFORMATION</c> is the wider right the TEB pointer needs, so the open is tried
  /// twice: a refusal of the second is a smaller answer rather than no answer, which is the
  /// difference between a page with two of three columns filled and a page with none.
  /// </remarks>
  private const uint _QueryLimited = 0x0800;

  private const uint _Query = 0x0040;

  private const int _AccessDenied = 5;
  private const int _InvalidParameter = 87;

  [StructLayout(LayoutKind.Sequential)]
  private struct ProcessorNumber {
    public ushort Group;
    public byte Number;
    public byte Reserved;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct ThreadBasicInformation {
    public int ExitStatus;
    public nint TebBaseAddress;
    public nint UniqueProcess;
    public nint UniqueThread;
    public nuint AffinityMask;
    public int Priority;
    public int BasePriority;
  }

  [LibraryImport("kernel32.dll", SetLastError = true)]
  private static partial nint OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool CloseHandle(nint handle);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool GetThreadIdealProcessorEx(nint thread, out ProcessorNumber processor);

  [LibraryImport("ntdll.dll")]
  private static partial uint NtQueryInformationThread(
    nint thread,
    int informationClass,
    ref ThreadBasicInformation information,
    uint length,
    out uint returned
  );

  /// <summary>What one thread's handle would say, or why it would not.</summary>
  internal readonly record struct Facts(Counter Cycles, Counter IdealProcessor, Counter TebBase) {

    /// <summary>The same reason for all three, for the cases where the handle itself is the problem.</summary>
    internal static Facts AllUnknown(UnknownReason reason) {
      var counter = Counter.Unknown(reason);
      return new(counter, counter, counter);
    }

  }

  /// <summary>
  /// Reads the three for one thread.
  /// </summary>
  /// <remarks>
  /// Each is reported on its own: a machine can hand over a cycle count and refuse a TEB pointer, and
  /// folding that into one answer would either claim a reading nobody took or hide two that were
  /// (PRD §72.3).
  /// </remarks>
  internal static Facts Read(int tid) {
    if (tid <= 0)
      return Facts.AllUnknown(UnknownReason.CounterInvalid);

    // The wider right first, because it is a superset: an open that gets it needs no second call, and
    // one that does not still gets the other two.
    var thread = OpenThread(_Query | _QueryLimited, false, (uint)tid);
    var wide = thread != 0;
    if (!wide)
      thread = OpenThread(_QueryLimited, false, (uint)tid);

    if (thread == 0)
      return Facts.AllUnknown(Marshal.GetLastWin32Error() switch {
        _AccessDenied => UnknownReason.NotPermitted,
        // A tid the kernel does not know is a thread that has ended between the bulk query and this
        // call, which is ordinary rather than an error (PRD §73).
        _InvalidParameter => UnknownReason.ProcessExited,
        _ => UnknownReason.CounterInvalid,
      });

    try {
      return new(
        QueryThreadCycleTime(thread, out var cycles) ? Counter.Of(cycles) : Failed(),
        GetThreadIdealProcessorEx(thread, out var processor)
          // The group is part of the answer on a machine with more than sixty-four processors, where
          // processor 3 of group 1 and processor 3 of group 0 are different processors. Flattened the
          // way every other processor number in this program is, so the column can be compared with
          // the last-CPU column beside it.
          ? Counter.Of((ulong)(processor.Group * 64 + processor.Number))
          : Failed(),
        ReadTeb(thread, wide)
      );
    } finally {
      CloseHandle(thread);
    }
  }

  private static Counter Failed() => Marshal.GetLastWin32Error() switch {
    _AccessDenied => Counter.NotPermitted,
    _InvalidParameter => Counter.Unknown(UnknownReason.ProcessExited),
    _ => Counter.Unknown(UnknownReason.CounterInvalid),
  };

  private static Counter ReadTeb(nint thread, bool wide) {
    if (!wide)
      return Counter.NotPermitted;

    var information = default(ThreadBasicInformation);
    var status = NtQueryInformationThread(
      thread,
      0,
      ref information,
      (uint)Marshal.SizeOf<ThreadBasicInformation>(),
      out _
    );

    if (status != 0)
      return Counter.Unknown(UnknownReason.CounterInvalid);

    // A TEB pointer of zero is a thread that has no environment block rather than one at address
    // zero — a thread being torn down loses it before it loses its handle. Nought here would render
    // as 0x0 and read as an address (PRD §72.3).
    return information.TebBaseAddress == 0
      ? Counter.Unknown(UnknownReason.SourceGone)
      : Counter.Of((ulong)information.TebBaseAddress);
  }

}
