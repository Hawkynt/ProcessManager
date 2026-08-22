using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Every service the machine knows about, from the service control manager (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// The Windows half of §41, which was empty: <c>GetServices</c> answered with nothing, so the
/// services view existed and had no rows on this platform. The list itself needs no privilege beyond
/// what any account has — <c>SC_MANAGER_ENUMERATE_SERVICE</c> is granted to Authenticated Users —
/// and neither does reading one service's configuration.
/// </para>
/// <para>
/// <b>Read-only, and it opens nothing it does not read.</b> The handles here are asked for
/// <c>SERVICE_QUERY_CONFIG</c> and <c>SERVICE_QUERY_STATUS</c> and nothing else, so this cannot start
/// or stop anything even by accident. Commanding a service is <see cref="Abstractions.IServiceControl"/>'s
/// job and asks for its own rights when it is used.
/// </para>
/// <para>
/// Two calls per service — the configuration and the description — after one call for the list. That
/// is why this is not on the sampling tick: it is the same reason the systemd side is read on demand,
/// and enumerating several hundred services once a second would make the monitor the thing worth
/// monitoring (§5.4).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsServiceReader {

  private const uint _ScManagerEnumerateService = 0x0004;
  private const uint _ScManagerConnect = 0x0001;
  private const uint _ServiceQueryConfig = 0x0001;
  private const uint _ServiceQueryStatus = 0x0004;

  private const uint _ServiceWin32 = 0x00000030;
  private const uint _ServiceDriver = 0x0000000B;
  private const uint _ServiceStateAll = 0x00000003;

  private const uint _ScStatusProcessInfo = 0;
  private const uint _ServiceConfigDescription = 1;

  private const int _MoreDataNeeded = 234;

  /// <summary>Start types, as <c>QUERY_SERVICE_CONFIG.dwStartType</c> reports them.</summary>
  private const uint _StartBoot = 0;
  private const uint _StartSystem = 1;
  private const uint _StartAuto = 2;
  private const uint _StartDemand = 3;
  private const uint _StartDisabled = 4;

  /// <summary>Current states, as <c>SERVICE_STATUS_PROCESS.dwCurrentState</c> reports them.</summary>
  private const uint _StateStopped = 1;
  private const uint _StateStartPending = 2;
  private const uint _StateStopPending = 3;
  private const uint _StateRunning = 4;

  [StructLayout(LayoutKind.Sequential)]
  private struct ServiceStatusProcess {
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
    public uint ProcessId;
    public uint ServiceFlags;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct EnumServiceStatusProcess {
    public nint ServiceName;
    public nint DisplayName;
    public ServiceStatusProcess Status;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct QueryServiceConfig {
    public uint ServiceType;
    public uint StartType;
    public uint ErrorControl;
    public nint BinaryPathName;
    public nint LoadOrderGroup;
    public uint TagId;
    public nint Dependencies;
    public nint ServiceStartName;
    public nint DisplayName;
  }

  [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
  private static partial nint OpenScManager(string? machine, string? database, uint access);

  [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
  private static partial nint OpenService(nint manager, string name, uint access);

  [LibraryImport("advapi32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool CloseServiceHandle(nint handle);

  [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool EnumServicesStatusEx(
    nint manager,
    uint infoLevel,
    uint serviceType,
    uint serviceState,
    nint services,
    uint bufferSize,
    out uint needed,
    out uint returned,
    ref uint resume,
    string? group
  );

  [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool QueryServiceConfigW(nint service, nint config, uint bufferSize, out uint needed);

  [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool QueryServiceConfig2W(nint service, uint level, nint buffer, uint bufferSize, out uint needed);

  /// <summary>
  /// Every service and driver on the machine.
  /// </summary>
  /// <remarks>
  /// Drivers are in the list on purpose. §41 asks for a driver-service indicator, which is a column
  /// that can only exist if drivers are rows — and a services view that silently omitted half the
  /// service control manager would be answering a narrower question than the one it appears to.
  /// </remarks>
  internal static IReadOnlyList<ServiceRecord> Read() {
    var manager = OpenScManager(null, null, _ScManagerConnect | _ScManagerEnumerateService);
    if (manager == 0)
      return [];

    try {
      return Enumerate(manager);
    } finally {
      CloseServiceHandle(manager);
    }
  }

  private static IReadOnlyList<ServiceRecord> Enumerate(nint manager) {
    var resume = 0u;
    // Asked once for the size it wants and then once for the data. The loop is for the resume
    // handle rather than for the size: a machine with more services than one buffer holds continues
    // where it left off, and dropping the tail would be a list that is quietly short.
    var records = new List<ServiceRecord>();
    var size = 0u;

    do {
      EnumServicesStatusEx(
        manager, _ScStatusProcessInfo, _ServiceWin32 | _ServiceDriver, _ServiceStateAll,
        0, 0, out var needed, out _, ref resume, null
      );

      if (needed == 0)
        break;

      if (needed > size)
        size = needed;

      var buffer = Marshal.AllocHGlobal((int)size);
      try {
        if (!EnumServicesStatusEx(
              manager, _ScStatusProcessInfo, _ServiceWin32 | _ServiceDriver, _ServiceStateAll,
              buffer, size, out _, out var returned, ref resume, null
            )) {
          // ERROR_MORE_DATA with a resume handle is the ordinary continue; anything else ends it.
          if (Marshal.GetLastWin32Error() != _MoreDataNeeded)
            break;
        }

        var entrySize = Marshal.SizeOf<EnumServiceStatusProcess>();
        for (var i = 0; i < returned; ++i) {
          var entry = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (i * entrySize));
          if (Marshal.PtrToStringUni(entry.ServiceName) is not { Length: > 0 } name)
            continue;

          records.Add(Describe(manager, name, Marshal.PtrToStringUni(entry.DisplayName), entry.Status));
        }
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    } while (resume != 0);

    return records;
  }

  private static ServiceRecord Describe(nint manager, string name, string? displayName, in ServiceStatusProcess status) {
    var service = OpenService(manager, name, _ServiceQueryConfig | _ServiceQueryStatus);
    string? command = null;
    string? account = null;
    string? description = null;
    bool? enabled = null;
    var disabled = false;
    var type = TypeOf(status.ServiceType);

    if (service != 0)
      try {
        if (ReadConfig(service) is { } config) {
          command = config.Command;
          account = config.Account;
          // Manual and automatic are both "not disabled": the question this answers is whether the
          // machine will bring it up on its own, which is what the systemd side means by enabled.
          enabled = config.StartType is _StartAuto or _StartBoot or _StartSystem;
          disabled = config.StartType == _StartDisabled;
          type = config.Type;
        }

        description = ReadDescription(service);
      } finally {
        CloseServiceHandle(service);
      }

    return new(
      name,
      // The display name, which is what the systemd side puts here too — a unit's Description= is
      // its display name and not a paragraph, so the two columns mean the same thing.
      description is { Length: > 0 } ? description : displayName,
      StateOf(status.CurrentState),
      enabled,
      // "Masked" is systemd's word for a unit that cannot be started at all, and Disabled is the
      // nearest thing Windows has: a disabled service refuses to start until the start type changes,
      // where a manual one starts on request. Mapping it to Enabled=false alone would lose that.
      disabled,
      (int)status.ProcessId,
      command,
      // A Windows service has no unit file. Its configuration lives in the registry, and the key is
      // the nearest thing to a path there is — an empty string here would read as "we did not look".
      $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
      // The restart policy is in the failure actions, which is a third query and is not read yet.
      null
    ) {
      LoadState = ServiceLoadState.Loaded,
      SubState = SubStateOf(status.CurrentState),
      Type = type,
      Account = account,
      // The parser is in Core and carries no platform attribute, so the fixtures below replay on
      // every CI leg rather than only on Windows (PRD §9.2).
      Executable = Query.ServiceImagePath.ExecutableOf(command),
      Arguments = Query.ServiceImagePath.ArgumentsOf(command),
    };
  }

  private static (string? Command, string? Account, uint StartType, string Type)? ReadConfig(nint service) {
    QueryServiceConfigW(service, 0, 0, out var needed);
    if (needed == 0)
      return null;

    var buffer = Marshal.AllocHGlobal((int)needed);
    try {
      if (!QueryServiceConfigW(service, buffer, needed, out _))
        return null;

      var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
      return (
        Marshal.PtrToStringUni(config.BinaryPathName),
        Marshal.PtrToStringUni(config.ServiceStartName),
        config.StartType,
        TypeOf(config.ServiceType)
      );
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static string? ReadDescription(nint service) {
    QueryServiceConfig2W(service, _ServiceConfigDescription, 0, 0, out var needed);
    if (needed == 0)
      return null;

    var buffer = Marshal.AllocHGlobal((int)needed);
    try {
      return QueryServiceConfig2W(service, _ServiceConfigDescription, buffer, needed, out _)
        // SERVICE_DESCRIPTION is one pointer, so the string is behind the first machine word.
        ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer))
        : null;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// Which of §41's three states this is.
  /// </summary>
  /// <remarks>
  /// A pending state is reported as what it is becoming rather than as unknown: a service that is
  /// starting is on its way to running, and calling it "the manager did not say" would be wrong about
  /// something the manager said clearly.
  /// </remarks>
  internal static ServiceState StateOf(uint state) => state switch {
    _StateRunning or _StateStartPending => ServiceState.Running,
    _StateStopped or _StateStopPending => ServiceState.Inactive,
    // Paused and its pending forms. Windows has a state systemd does not, and folding it into either
    // of the two above would say something untrue about a service that is neither.
    _ => ServiceState.Unknown,
  };

  internal static ServiceSubState SubStateOf(uint state) => state switch {
    _StateRunning => ServiceSubState.Running,
    _StateStopped => ServiceSubState.Dead,
    _ => ServiceSubState.Unknown,
  };

  /// <summary>The kind of service, in the words Windows uses for it.</summary>
  /// <remarks>
  /// Windows' own vocabulary and not systemd's, for §5.3's reason: "own process" and "simple" are not
  /// the same idea, and translating one into the other would put a word on the screen that the
  /// platform's own documentation does not use.
  /// </remarks>
  internal static string TypeOf(uint serviceType) => (serviceType & 0xFF) switch {
    0x01 => "kernel driver",
    0x02 => "file system driver",
    0x10 => "own process",
    0x20 => "shared process",
    0x50 => "own process (user)",
    0x60 => "shared process (user)",
    _ => (serviceType & 0x10) != 0 ? "own process" : (serviceType & 0x20) != 0 ? "shared process" : "unknown",
  };

}
