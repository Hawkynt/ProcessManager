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

  /// <summary>
  /// The processor topology: cores, packages, NUMA nodes and caches, in one variable-length buffer.
  /// </summary>
  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetLogicalProcessorInformationEx(int relationshipType, nint buffer, ref uint returnedLength);

  /// <summary>RelationAll — every record in one call rather than four calls for four kinds.</summary>
  public const int RelationAll = 0xFFFF;

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

  /// <summary>
  /// One <c>PROCESS_MITIGATION_*</c> policy, as the flags word its structure is a union over
  /// (PRD §21).
  /// </summary>
  /// <remarks>
  /// The buffer is caller-owned unmanaged memory rather than a typed <c>out</c>, because the six
  /// structures this is called with have six different lengths and every one of them is a union with
  /// a bitfield — which C# cannot express and does not need to, since the only thing read out of any
  /// of them is the word.
  /// </remarks>
  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetProcessMitigationPolicy(nint process, int policy, nint buffer, nuint length);

  /// <summary>
  /// The full path of the running image (PRD §14).
  /// </summary>
  /// <remarks>
  /// <c>SYSTEM_PROCESS_INFORMATION</c> carries only the file name, so the path has to be asked for
  /// separately — but only with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is the right the
  /// owner lookup already holds, and only once per process because a running program does not move.
  /// The alternative, <c>GetModuleFileNameEx</c>, needs to read the target's address space.
  /// </remarks>
  // The Utf16 marshalling is what lets the buffer be a `ref char` over a stack span rather than a
  // string the generator would allocate and copy — the same reason LookupAccountSidW is written this
  // way above.
  [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
    StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, ref char name, ref uint size);

  /// <summary>
  /// <c>GetProcessInformation</c>, used here only for <c>ProcessProtectionLevelInfo</c>.
  /// </summary>
  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool GetProcessInformation(nint process, int informationClass, nint information, uint size);

  /// <summary>
  /// How many window-manager or graphics objects a process holds (PRD §20).
  /// </summary>
  /// <remarks>
  /// Returns nought both for a process with no such objects and for a call that failed, and the
  /// documentation says so in as many words — which is why the caller clears the last error first
  /// and asks afterwards. A console service really does hold no USER objects, and that is a
  /// measurement rather than a refusal (PRD §72.3).
  /// </remarks>
  [LibraryImport("user32.dll", SetLastError = true)]
  internal static partial uint GetGuiResources(nint process, uint flags);

  /// <summary>
  /// Which instruction set a process is being translated from, and which the machine itself runs.
  /// </summary>
  /// <remarks>
  /// Windows 10 1709 and newer. On anything older the export is simply not there, so the first call
  /// throws and the caller remembers that rather than asking again for every process on the machine.
  /// </remarks>
  [LibraryImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  internal static partial bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);

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
  /// TOKEN_IS_APP_CONTAINER: one DWORD, non-zero when the token belongs to an AppContainer.
  /// </summary>
  /// <remarks>
  /// Derived from its position in <c>TOKEN_INFORMATION_CLASS</c>, because Microsoft's reference page
  /// for that enumeration prints exactly one number — <c>TokenUser = 1</c> — and the rest by order.
  /// The two neighbours this file already uses, <c>TokenElevation</c> at 20 and
  /// <c>TokenIntegrityLevel</c> at 25, are derived the same way and have been right in practice
  /// since this probe was written, which is the only corroboration available from here.
  /// </remarks>
  public const int TokenIsAppContainer = 29;

  /// <summary>
  /// <c>ProcessProtectionLevelInfo</c>, the eighth member of <c>PROCESS_INFORMATION_CLASS</c>.
  /// </summary>
  public const int ProcessProtectionLevelInfo = 7;

  /// <summary>
  /// <c>PROTECTION_LEVEL_NONE</c>, which is <c>0xFFFFFFFE</c> and emphatically not <c>-1</c>.
  /// </summary>
  /// <remarks>
  /// <c>PROTECTION_LEVEL_SAME</c> is the <c>0xFFFFFFFF</c> one would otherwise reach for, and nought
  /// is <c>PROTECTION_LEVEL_WINTCB_LIGHT</c> — a real and high level. Getting either of those wrong
  /// reports the whole machine as protected or none of it.
  /// </remarks>
  public const uint PROTECTION_LEVEL_NONE = 0xFFFF_FFFE;

  /// <summary>
  /// The six <c>PROCESS_MITIGATION_POLICY</c> members this program reads, by their position in the
  /// enumeration (PRD §21).
  /// </summary>
  /// <remarks>
  /// Microsoft's reference page for the enumeration prints no numbers at all, so these are derived
  /// from the order of the members on that page: DEP, ASLR, dynamic code, strict handle check,
  /// system call disable, options mask, extension point disable, control flow guard, signature,
  /// font disable, image load, system call filter, payload restriction, child process, side channel
  /// isolation, user shadow stack. Note that the <c>GetProcessMitigationPolicy</c> page lists the
  /// same members in a <em>different</em> order and omits several, so it must not be used to derive
  /// them — which is the sort of thing that is only obvious once somebody has looked at both.
  /// </remarks>
  public const int ProcessDEPPolicy = 0;
  public const int ProcessASLRPolicy = 1;
  public const int ProcessDynamicCodePolicy = 2;
  public const int ProcessControlFlowGuardPolicy = 7;
  public const int ProcessSignaturePolicy = 8;
  public const int ProcessUserShadowStackPolicy = 15;

  /// <summary>The two <c>GetGuiResources</c> flags, which the documentation does give numbers for.</summary>
  public const uint GR_GDIOBJECTS = 0;
  public const uint GR_USEROBJECTS = 1;

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
