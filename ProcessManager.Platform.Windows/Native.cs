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

  /// <summary>
  /// The same call as above against caller-owned unmanaged memory, for the queries whose result is a
  /// chain of pointers into itself and therefore must not be copied into a managed array.
  /// </summary>
  [LibraryImport("ntdll.dll", EntryPoint = "NtQuerySystemInformation")]
  internal static partial uint NtQuerySystemInformationRaw(
    int systemInformationClass,
    nint systemInformation,
    uint systemInformationLength,
    out uint returnLength
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

  [LibraryImport("ntdll.dll")]
  internal static partial uint NtQueryObject(nint handle, int informationClass, nint information, uint length, out uint returnLength);

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool DuplicateHandle(
    nint sourceProcess,
    nint sourceHandle,
    nint targetProcess,
    out nint targetHandle,
    uint desiredAccess,
    [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
    uint options
  );

  [LibraryImport("kernel32.dll")]
  internal static partial nint GetCurrentProcess();

  [LibraryImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true)]
  internal static partial nint CreateToolhelp32Snapshot(uint flags, int processId);

  [LibraryImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool Module32FirstW(nint snapshot, ref NtStructures.ModuleEntry32 entry);

  [LibraryImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool Module32NextW(nint snapshot, ref NtStructures.ModuleEntry32 entry);

  [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedTcpTable", SetLastError = true)]
  internal static partial uint GetExtendedTcpTable(
    nint table,
    ref uint size,
    [MarshalAs(UnmanagedType.Bool)] bool order,
    uint addressFamily,
    int tableClass,
    uint reserved
  );

  [LibraryImport("iphlpapi.dll", EntryPoint = "GetExtendedUdpTable", SetLastError = true)]
  internal static partial uint GetExtendedUdpTable(
    nint table,
    ref uint size,
    [MarshalAs(UnmanagedType.Bool)] bool order,
    uint addressFamily,
    int tableClass,
    uint reserved
  );

  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool ReadProcessMemory(nint process, nint address, nint buffer, nuint size, out nuint read);

  public const uint AF_INET = 2;
  public const uint AF_INET6 = 23;

  /// <summary><c>TCP_TABLE_OWNER_PID_ALL</c> / <c>UDP_TABLE_OWNER_PID</c>.</summary>
  public const int TCP_TABLE_OWNER_PID_ALL = 5;
  public const int UDP_TABLE_OWNER_PID = 1;

  public const uint ERROR_INSUFFICIENT_BUFFER = 122;
  public const uint PROCESS_VM_READ = 0x0010;
  public const uint PROCESS_QUERY_INFORMATION = 0x0400;
  public const int ProcessBasicInformation = 0;

  public const int ObjectNameInformation = 1;
  public const int ObjectTypeInformation = 2;

  /// <summary><c>SystemExtendedHandleInformation</c> — every handle on the machine, with its owner.</summary>
  public const int SystemExtendedHandleInformationClass = 64;

  public const uint PROCESS_DUP_HANDLE = 0x0040;
  public const uint DUPLICATE_SAME_ACCESS = 0x0002;

  public const uint TH32CS_SNAPMODULE = 0x00000008;
  public const uint TH32CS_SNAPMODULE32 = 0x00000010;
  public static readonly nint INVALID_HANDLE_VALUE = -1;

  public const uint TOKEN_QUERY = 0x0008;
  public const int TokenUser = 1;

  /// <summary>TOKEN_ELEVATION: one DWORD, non-zero when the token is elevated.</summary>
  public const int TokenElevation = 20;

  /// <summary>
  /// TOKEN_MANDATORY_LABEL: a SID whose last sub-authority is the integrity level.
  /// </summary>
  public const int TokenIntegrityLevel = 25;

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
