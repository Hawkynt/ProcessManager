using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// The native entry points the Windows probe uses.
/// </summary>
/// <remarks>
/// <c>[LibraryImport]</c> only, so the marshalling is generated at compile time and the NativeAOT
/// publish stays warning-free (PRD §2).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class Native {

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtQuerySystemInformation(
    int systemInformationClass,
    [Out] byte[] systemInformation,
    int systemInformationLength,
    out int returnLength
  );

  [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetPerformanceInfo(ref NtStructures.PerformanceInformation info, uint size);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  internal static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool CloseHandle(nint handle);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool TerminateProcess(nint handle, uint exitCode);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool SetPriorityClass(nint handle, uint priorityClass);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);

  [LibraryImport("kernel32.dll", EntryPoint = "SetProcessAffinityMask", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool SetProcessAffinityMask(nint handle, nuint mask);

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtSuspendProcess(nint handle);

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtResumeProcess(nint handle);

  [LibraryImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

  [LibraryImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetTokenInformation(nint token, int informationClass, nint information, uint length, out uint returnLength);

  [LibraryImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool ConvertSidToStringSidW(nint sid, out nint stringSid);

  [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool ConvertStringSidToSidW(string stringSid, out nint sid);

  // Spans rather than StringBuilder: the P/Invoke source generator does not marshal StringBuilder at
  // all, which is the point of using it — the old marshaller hid a copy in each direction here.
  [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool LookupAccountSidW(
    string? systemName,
    nint sid,
    ref char name,
    ref uint nameLength,
    ref char domain,
    ref uint domainLength,
    out int use
  );

  [LibraryImport("kernel32.dll", SetLastError = true)]
  internal static partial nint LocalFree(nint memory);

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtQueryInformationProcess(
    nint process,
    int informationClass,
    nint information,
    uint length,
    out uint returnLength
  );

  public const uint TOKEN_QUERY = 0x0008;
  public const int TokenUser = 1;

  /// <summary>
  /// <c>ProcessCommandLineInformation</c>. Available since Windows 8.1 and the only way to read a
  /// command line without reading the target's PEB through its address space.
  /// </summary>
  public const int ProcessCommandLineInformation = 60;

  public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

  public const uint PROCESS_TERMINATE = 0x0001;
  public const uint PROCESS_SET_INFORMATION = 0x0200;
  public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
  public const uint PROCESS_SUSPEND_RESUME = 0x0800;

  public const int ERROR_ACCESS_DENIED = 5;
  public const int ERROR_INVALID_PARAMETER = 87;

  public const uint IDLE_PRIORITY_CLASS = 0x00000040;
  public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
  public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
  public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
  public const uint HIGH_PRIORITY_CLASS = 0x00000080;
  public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

}
