using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Reads the machine through the native API.
/// </summary>
/// <remarks>
/// <para>
/// One <c>NtQuerySystemInformation(SystemProcessInformation)</c> call carries every process
/// <em>and</em> every thread, with CPU, memory, I/O, handle count and session already in it. No
/// <c>OpenProcess</c> in the sampling loop, and no WMI anywhere: WMI is orders of magnitude slower
/// and depends on a service that is not always healthy on the machines where a process manager gets
/// opened (PRD §5.2).
/// </para>
/// <para>
/// The parsing half lives in <see cref="SystemProcessInformationReader"/> and is <em>not</em> gated
/// on Windows, because it touches no Windows API — it walks bytes. That is what lets a captured
/// buffer be replayed through it on the Linux and macOS CI legs (PRD §9.4), and the analyzer said so
/// before the split did.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsProbe : ISystemProbe {

  // Pinned, and that is load-bearing twice over. The kernel writes absolute pointers into this
  // buffer (every UNICODE_STRING's Buffer field points back into it), so an array the GC is free to
  // move would leave those pointers dangling between the call and the parse. And a stable base
  // address is what lets the parse express them as offsets, which is what makes a captured buffer
  // replayable at all (PRD §9.4).
  private byte[] _buffer = GC.AllocateUninitializedArray<byte>(512 * 1024, pinned: true);
  private readonly Dictionary<ProcessKey, string?> _commandLines = [];
  private readonly WindowsIdentityResolver _identities = new();
  private readonly HashSet<int> _livePids = [];
  private readonly HandleNameResolver _handleNames = new();
  private int _bufferLength;

  public string Description => "windows:ntquerysysteminformation";

  private HostInfo? _host;

  /// <summary>Read once; nothing in it changes while the program runs.</summary>
  public HostInfo DescribeHost() => this._host ??= WindowsHostReader.Read();

  public void Dispose() => this._handleNames.Dispose();

  public void Sample(SystemSnapshot snapshot) {
    ArgumentNullException.ThrowIfNull(snapshot);

    ReadProcessorTimes(snapshot);
    if (!this.QueryProcesses(out var length))
      return;

    this._bufferLength = length;
    SystemProcessInformationReader.Parse(this._buffer.AsSpan(0, length), this.BufferAddress, snapshot);
    ReadMemory(ref snapshot.System);
    this.ResolveOwnersAndCommandLines(snapshot);
  }

  /// <summary>
  /// Calls the query, growing the buffer until it fits.
  /// </summary>
  /// <remarks>
  /// The length the call reports on <c>STATUS_INFO_LENGTH_MISMATCH</c> is the size needed <em>at that
  /// moment</em>; processes start while the retry is in flight, so it is taken as a floor with room
  /// added, and the loop is bounded rather than trusting to converge.
  /// </remarks>
  private bool QueryProcesses(out int length) {
    for (var attempt = 0; attempt < 8; ++attempt) {
      var status = Native.NtQuerySystemInformation(
        NtStructures.SystemProcessInformationClass,
        this._buffer,
        this._buffer.Length,
        out var needed
      );

      if (status == NtStructures.STATUS_SUCCESS) {
        length = needed > 0 ? Math.Min(needed, this._buffer.Length) : this._buffer.Length;
        return true;
      }

      if (status != NtStructures.STATUS_INFO_LENGTH_MISMATCH) {
        length = 0;
        return false;
      }

      this._buffer = GC.AllocateUninitializedArray<byte>(Math.Max(needed + 64 * 1024, this._buffer.Length * 2), pinned: true);
    }

    length = 0;
    return false;
  }

  private nint BufferAddress {
    get {
      unsafe {
        fixed (byte* pointer = this._buffer)
          return (nint)pointer;
      }
    }
  }

  private static void ReadProcessorTimes(SystemSnapshot snapshot) {
    var cores = Environment.ProcessorCount;
    var size = Marshal.SizeOf<NtStructures.SystemProcessorPerformanceInformation>();
    var rented = ArrayPool<byte>.Shared.Rent(size * cores);
    try {
      var status = Native.NtQuerySystemInformation(
        NtStructures.SystemProcessorPerformanceInformationClass,
        rented,
        size * cores,
        out _
      );

      if (status != NtStructures.STATUS_SUCCESS)
        return;

      var perCore = snapshot.PrepareCores(cores);
      var aggregate = default(CpuTimes);
      for (var i = 0; i < cores; ++i) {
        ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemProcessorPerformanceInformation>(
          rented.AsSpan(i * size)
        );

        // KernelTime as reported *includes* idle. Subtracting it is what makes the busy percentage
        // agree with Task Manager instead of reading 100% on an idle machine.
        var idle = (ulong)Math.Max(0, entry.IdleTime) * 100;
        var kernel = (ulong)Math.Max(0, entry.KernelTime) * 100;
        var times = new CpuTimes {
          IdleNs = idle,
          KernelNs = kernel > idle ? kernel - idle : 0,
          UserNs = (ulong)Math.Max(0, entry.UserTime) * 100,
          IrqNs = (ulong)Math.Max(0, entry.InterruptTime) * 100,
        };

        perCore[i] = times;
        aggregate.IdleNs += times.IdleNs;
        aggregate.KernelNs += times.KernelNs;
        aggregate.UserNs += times.UserNs;
        aggregate.IrqNs += times.IrqNs;
      }

      snapshot.System.Cpu = aggregate;
      snapshot.System.CoreCount = cores;
    } finally {
      ArrayPool<byte>.Shared.Return(rented);
    }
  }

  private static void ReadMemory(ref SystemCounters system) {
    var info = new NtStructures.PerformanceInformation { Size = (uint)Marshal.SizeOf<NtStructures.PerformanceInformation>() };
    if (!Native.GetPerformanceInfo(ref info, info.Size))
      return;

    var pageSize = (ulong)info.PageSize;
    system.TotalMemoryBytes = Counter.Of((ulong)info.PhysicalTotal * pageSize);
    system.AvailableMemoryBytes = Counter.Of((ulong)info.PhysicalAvailable * pageSize);
    system.CachedMemoryBytes = Counter.Of((ulong)info.SystemCache * pageSize);
    system.RunningProcesses = (int)info.ProcessCount;

    // Windows has a commit charge rather than a swap file with a used/total pair, so the swap meters
    // show commit — which is the number that actually predicts a machine falling over.
    system.TotalSwapBytes = Counter.Of((ulong)info.CommitLimit * pageSize);
    system.UsedSwapBytes = Counter.Of((ulong)info.CommitTotal * pageSize);
    system.UptimeSeconds = Environment.TickCount64 / 1000d;
  }

  /// <summary>
  /// Fills in the two things the bulk query does not carry: who owns each process, and what it was
  /// started with. Both are constant for a process's lifetime, so both are cached and only new
  /// processes cost anything (PRD §5.2).
  /// </summary>
  private void ResolveOwnersAndCommandLines(SystemSnapshot snapshot) {
    this._livePids.Clear();
    var processes = snapshot.ProcessBuffer;
    for (var i = 0; i < processes.Length; ++i) {
      ref var record = ref processes[i];
      this._livePids.Add(record.Pid);

      // One token read answers all three, and the answer is cached for the life of the process:
      // none of the owner, the elevation or the integrity level changes while it runs.
      record.UserName = this._identities.Resolve(
        record.Pid,
        record.Key.StartTicks,
        out var userId,
        out var elevated,
        out var integrity
      );

      record.UserId = userId;
      record.IsElevated = elevated;
      record.IntegrityLevel = integrity;
      // Windows has no notion of a real-versus-effective uid; the token is the whole answer.
      record.EffectiveUserId = userId;

      if (this._commandLines.TryGetValue(record.Key, out var commandLine)) {
        record.CommandLine = commandLine;
        continue;
      }

      commandLine = ReadCommandLine(record.Pid);
      this._commandLines[record.Key] = commandLine;
      record.CommandLine = commandLine;
    }

    this._identities.Prune(this._livePids);
    if (this._commandLines.Count > 4096)
      foreach (var key in this._commandLines.Keys.Where(key => !this._livePids.Contains(key.Pid)).ToList())
        this._commandLines.Remove(key);
  }

  /// <summary>
  /// The command line, through <c>ProcessCommandLineInformation</c>.
  /// </summary>
  /// <remarks>
  /// The alternative is reading the target's PEB across its address space, which needs far more
  /// access and breaks on a cross-bitness target. This needs only
  /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and has existed since Windows 8.1. A process that
  /// refuses is reported as having no command line, which is the truth as far as this user can see
  /// it (PRD §3.4).
  /// </remarks>
  private static string? ReadCommandLine(int pid) {
    var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (process == 0)
      return null;

    try {
      Native.NtQueryInformationProcess(process, Native.ProcessCommandLineInformation, 0, 0, out var needed);
      if (needed is 0 or > 64 * 1024)
        return null;

      var buffer = Marshal.AllocHGlobal((int)needed);
      try {
        var status = Native.NtQueryInformationProcess(process, Native.ProcessCommandLineInformation, buffer, needed, out _);
        if (status != NtStructures.STATUS_SUCCESS)
          return null;

        // The result is a UNICODE_STRING whose Buffer points just past itself, inside this same
        // allocation — so the string is read from the buffer we own rather than from the target.
        var length = (ushort)Marshal.ReadInt16(buffer);
        var pointer = Marshal.ReadIntPtr(buffer, nint.Size);
        return length == 0 || pointer == 0 ? null : Marshal.PtrToStringUni(pointer, length / sizeof(char));
      } finally {
        Marshal.FreeHGlobal(buffer);
      }
    } finally {
      Native.CloseHandle(process);
    }
  }

  public Counter GetHandleCount(ProcessKey key) {
    // Already in the bulk query on this platform, so there is nothing to do on demand. Returning
    // NotSampledYet rather than a second query keeps the one source of truth.
    return Counter.NotSampledYet;
  }

  /// <summary>
  /// Threads, read back out of the buffer the last sample already produced.
  /// </summary>
  /// <remarks>
  /// Costs nothing: <c>SYSTEM_PROCESS_INFORMATION</c> is followed by one
  /// <c>SYSTEM_THREAD_INFORMATION</c> per thread, so the whole machine's threads arrived with the
  /// process list. Linux has to open a directory per process for the same answer (PRD §5.1).
  /// </remarks>
  /// <summary>
  /// Not read yet. Windows keeps startup entries in the registry's Run keys, the Startup folders and
  /// the task scheduler, and none of the three is implemented (PRD §42).
  /// </summary>
  public IReadOnlyList<StartupEntry> GetStartupEntries() => [];

  /// <summary>
  /// Not read yet: Windows sessions come from the terminal-services API (WTSEnumerateSessions),
  /// which is not written (PRD §43).
  /// </summary>
  public IReadOnlyList<SessionRecord> GetSessions() => [];

  /// <summary>
  /// Not read yet: the per-device counters come from the performance-counter API or from
  /// IOCTL_STORAGE_QUERY_PROPERTY, and neither is written (PRD §48, §49). The snapshot carries no
  /// devices on Windows, so nothing calls these.
  /// </summary>
  public DiskInfo DescribeDisk(string name)
    => new(name, null, null, Counter.Unknown(UnknownReason.NotImplementedHere));

  public NetworkInterfaceInfo DescribeInterface(string name) => new(
    name,
    null,
    Counter.Unknown(UnknownReason.NotImplementedHere),
    null,
    Counter.Unknown(UnknownReason.NotImplementedHere),
    IsLoopback: false
  );

  public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key)
    => this._bufferLength == 0
      ? []
      : SystemProcessInformationReader.ReadThreads(this._buffer.AsSpan(0, this._bufferLength), key);

  /// <summary>Loaded modules, through a Toolhelp snapshot.</summary>
  /// <remarks>
  /// Toolhelp rather than a PEB walk: it needs no read access to the target's address space, works
  /// for a 32-bit process seen from a 64-bit one when both snapshot flags are passed, and is a
  /// documented API rather than a structure that moves between Windows releases.
  /// </remarks>
  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) {
    var snapshot = Native.CreateToolhelp32Snapshot(
      Native.TH32CS_SNAPMODULE | Native.TH32CS_SNAPMODULE32,
      key.Pid
    );

    if (snapshot == Native.INVALID_HANDLE_VALUE)
      return [];

    try {
      var result = new List<ModuleRecord>();
      var entry = new NtStructures.ModuleEntry32 {
        Size = (uint)Marshal.SizeOf<NtStructures.ModuleEntry32>(),
      };

      if (!Native.Module32FirstW(snapshot, ref entry))
        return result;

      do {
        var path = entry.ReadExePath();
        result.Add(new(
          path.Length > 0 ? path : entry.ReadModule(),
          (ulong)entry.ModuleBaseAddress,
          entry.ModuleBaseSize,
          // Windows does not report per-module page protection here; the mapping's own protection is
          // per-region rather than per-module, so claiming one would be inventing it.
          string.Empty
        ));

        entry.Size = (uint)Marshal.SizeOf<NtStructures.ModuleEntry32>();
      } while (Native.Module32NextW(snapshot, ref entry));

      return result;
    } finally {
      Native.CloseHandle(snapshot);
    }
  }

  /// <summary>
  /// Every handle the process holds, named where the kernel will name it.
  /// </summary>
  /// <remarks>
  /// The machine's whole handle table arrives in one call and is filtered by owner — there is no
  /// per-process handle query. Each handle is then duplicated into this process to be asked about,
  /// because a handle value is only meaningful in the process that owns it. Naming goes through
  /// <see cref="HandleNameResolver"/>, which is where the hang described in PRD §5.2 is handled.
  /// </remarks>
  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
    var target = Native.OpenProcess(Native.PROCESS_DUP_HANDLE, false, key.Pid);
    if (target == 0)
      return [];

    var buffer = 256 * 1024;
    try {
      for (var attempt = 0; attempt < 8; ++attempt) {
        var memory = Marshal.AllocHGlobal(buffer);
        try {
          var status = Native.NtQuerySystemInformationRaw(
            Native.SystemExtendedHandleInformationClass,
            memory,
            (uint)buffer,
            out var needed
          );

          if (status == NtStructures.STATUS_INFO_LENGTH_MISMATCH) {
            buffer = (int)Math.Max(needed + 64 * 1024, (uint)buffer * 2);
            continue;
          }

          return status != NtStructures.STATUS_SUCCESS
            ? []
            : this.ReadHandles(memory, target, key.Pid);
        } finally {
          Marshal.FreeHGlobal(memory);
        }
      }

      return [];
    } finally {
      Native.CloseHandle(target);
    }
  }

  private List<HandleRecord> ReadHandles(nint memory, nint target, int pid) {
    var result = new List<HandleRecord>();
    var count = (long)(nuint)Marshal.ReadIntPtr(memory);
    var entrySize = Marshal.SizeOf<NtStructures.SystemHandleTableEntryInfoEx>();
    var first = memory + Marshal.SizeOf<NtStructures.SystemHandleInformationEx>();
    var self = Native.GetCurrentProcess();

    for (long i = 0; i < count; ++i) {
      var entry = Marshal.PtrToStructure<NtStructures.SystemHandleTableEntryInfoEx>(first + (nint)(i * entrySize));
      if ((int)entry.UniqueProcessId != pid)
        continue;

      if (!Native.DuplicateHandle(target, entry.HandleValue, self, out var copy, 0, false, Native.DUPLICATE_SAME_ACCESS))
        continue;

      try {
        var type = HandleNameResolver.QueryType(copy);
        var name = this._handleNames.TryGetName(copy);
        result.Add(new((ulong)entry.HandleValue, ClassifyType(type), name, null));
      } finally {
        Native.CloseHandle(copy);
      }
    }

    return result;
  }

  private static HandleKind ClassifyType(string? type) => type switch {
    "File" => HandleKind.File,
    "Directory" => HandleKind.Directory,
    "Key" => HandleKind.Key,
    "Event" => HandleKind.Event,
    "Mutant" => HandleKind.Mutex,
    "Section" => HandleKind.Section,
    "Thread" => HandleKind.Thread,
    "Process" => HandleKind.Process,
    "Device" => HandleKind.Device,
    _ => HandleKind.Unknown,
  };

  /// <summary>
  /// The sockets this process owns, from the TCP and UDP tables.
  /// </summary>
  /// <remarks>
  /// Windows reports the owning pid in the table itself, so unlike Linux there is no inode to join
  /// against — the whole machine's table comes back and is filtered. Both address families are asked
  /// for separately, because the call takes one at a time.
  /// </remarks>
  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) {
    var result = new List<ConnectionRecord>();
    ReadTcp(key.Pid, Native.AF_INET, ConnectionProtocol.Tcp, result);
    ReadTcp(key.Pid, Native.AF_INET6, ConnectionProtocol.Tcp6, result);
    ReadUdp(key.Pid, Native.AF_INET, ConnectionProtocol.Udp, result);
    ReadUdp(key.Pid, Native.AF_INET6, ConnectionProtocol.Udp6, result);
    return result;
  }

  private static void ReadTcp(int pid, uint family, ConnectionProtocol protocol, List<ConnectionRecord> result) {
    // MIB_TCPROW_OWNER_PID for IPv4 is state, local addr, local port, remote addr, remote port,
    // owning pid — six 32-bit fields. The IPv6 row carries 16-byte addresses and scope ids instead.
    var rowSize = family == Native.AF_INET ? 24 : 56;
    Walk(
      (nint table, ref uint size) => Native.GetExtendedTcpTable(table, ref size, false, family, Native.TCP_TABLE_OWNER_PID_ALL, 0),
      rowSize,
      (row, entry) => {
        if (family == Native.AF_INET) {
          var owner = Marshal.ReadInt32(entry, 20);
          if (owner != pid)
            return;

          result.Add(new(
            protocol,
            FormatIPv4((uint)Marshal.ReadInt32(entry, 4)),
            NetworkPort(Marshal.ReadInt32(entry, 8)),
            FormatIPv4((uint)Marshal.ReadInt32(entry, 12)),
            NetworkPort(Marshal.ReadInt32(entry, 16)),
            TcpStateName(Marshal.ReadInt32(entry, 0)),
            0
          ));
        } else {
          var owner = Marshal.ReadInt32(entry, 52);
          if (owner != pid)
            return;

          result.Add(new(
            protocol,
            FormatIPv6(entry, 0),
            NetworkPort(Marshal.ReadInt32(entry, 20)),
            FormatIPv6(entry, 24),
            NetworkPort(Marshal.ReadInt32(entry, 44)),
            TcpStateName(Marshal.ReadInt32(entry, 48)),
            0
          ));
        }
      }
    );
  }

  private static void ReadUdp(int pid, uint family, ConnectionProtocol protocol, List<ConnectionRecord> result) {
    var rowSize = family == Native.AF_INET ? 12 : 28;
    Walk(
      (nint table, ref uint size) => Native.GetExtendedUdpTable(table, ref size, false, family, Native.UDP_TABLE_OWNER_PID, 0),
      rowSize,
      (row, entry) => {
        if (family == Native.AF_INET) {
          if (Marshal.ReadInt32(entry, 8) != pid)
            return;

          result.Add(new(protocol, FormatIPv4((uint)Marshal.ReadInt32(entry, 0)), NetworkPort(Marshal.ReadInt32(entry, 4)), "*", 0, "LISTEN", 0));
        } else {
          if (Marshal.ReadInt32(entry, 24) != pid)
            return;

          result.Add(new(protocol, FormatIPv6(entry, 0), NetworkPort(Marshal.ReadInt32(entry, 20)), "*", 0, "LISTEN", 0));
        }
      }
    );
  }

  private delegate uint TableQuery(nint table, ref uint size);

  private static void Walk(TableQuery query, int rowSize, Action<int, nint> row) {
    uint size = 0;
    if (query(0, ref size) != Native.ERROR_INSUFFICIENT_BUFFER || size == 0)
      return;

    var buffer = Marshal.AllocHGlobal((int)size);
    try {
      if (query(buffer, ref size) != 0)
        return;

      var count = Marshal.ReadInt32(buffer);
      // The row array begins after the DWORD count, and the table is capped at what the buffer can
      // actually hold — a count larger than that is a corrupt table, not a reason to walk off it.
      var maximum = ((int)size - 4) / rowSize;
      for (var i = 0; i < Math.Min(count, maximum); ++i)
        row(i, buffer + 4 + i * rowSize);
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>Ports come back in network byte order in the low two bytes.</summary>
  private static int NetworkPort(int value) => ((value & 0xFF) << 8) | ((value >> 8) & 0xFF);

  private static string FormatIPv4(uint address)
    => $"{address & 0xFF}.{(address >> 8) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 24) & 0xFF}";

  private static string FormatIPv6(nint entry, int offset) {
    Span<byte> bytes = stackalloc byte[16];
    for (var i = 0; i < 16; ++i)
      bytes[i] = Marshal.ReadByte(entry, offset + i);

    return new System.Net.IPAddress(bytes).ToString();
  }

  private static string TcpStateName(int state) => state switch {
    1 => "CLOSED",
    2 => "LISTEN",
    3 => "SYN_SENT",
    4 => "SYN_RCVD",
    5 => "ESTABLISHED",
    6 => "FIN_WAIT1",
    7 => "FIN_WAIT2",
    8 => "CLOSE_WAIT",
    9 => "CLOSING",
    10 => "LAST_ACK",
    11 => "TIME_WAIT",
    12 => "DELETE_TCB",
    _ => "UNKNOWN",
  };

  /// <summary>
  /// The environment block, read out of the target's own address space.
  /// </summary>
  /// <remarks>
  /// <para>
  /// There is no query for this: the block lives in the process's memory, reachable only by walking
  /// its PEB. <c>NtQueryInformationProcess(ProcessBasicInformation)</c> gives the PEB's address, the
  /// PEB holds a pointer to its <c>RTL_USER_PROCESS_PARAMETERS</c>, and those hold the block and its
  /// length. Three <c>ReadProcessMemory</c> calls, and it needs <c>PROCESS_VM_READ</c> — which is why
  /// the command line does <em>not</em> come this way (PRD §5.2).
  /// </para>
  /// <para>
  /// The offsets are for 64-bit Windows and are the one genuinely fragile thing in this probe: they
  /// are structure layout, not API. They have been stable across every 64-bit Windows release, and a
  /// bad read is bounds-checked into an empty list rather than a crash — but if this ever returns
  /// nonsense on a new release, this is the paragraph to come back to.
  /// </para>
  /// </remarks>
  public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) {
    const int PebProcessParametersOffset = 0x20;
    const int ParametersEnvironmentOffset = 0x80;
    const int ParametersEnvironmentSizeOffset = 0x3F0;

    var process = Native.OpenProcess(
      Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_VM_READ,
      false,
      key.Pid
    );

    if (process == 0)
      return [];

    try {
      var basicInformation = Marshal.AllocHGlobal(48);
      try {
        if (Native.NtQueryInformationProcess(process, Native.ProcessBasicInformation, basicInformation, 48, out _)
            != NtStructures.STATUS_SUCCESS)
          return [];

        // PROCESS_BASIC_INFORMATION: ExitStatus (pointer-sized), then PebBaseAddress.
        var peb = Marshal.ReadIntPtr(basicInformation, nint.Size);
        if (peb == 0)
          return [];

        if (!TryReadPointer(process, peb + PebProcessParametersOffset, out var parameters) || parameters == 0)
          return [];
        if (!TryReadPointer(process, parameters + ParametersEnvironmentOffset, out var environment) || environment == 0)
          return [];
        if (!TryReadUInt32(process, parameters + ParametersEnvironmentSizeOffset, out var size))
          return [];

        // A length the target controls, so it is bounded before anything is allocated.
        if (size is 0 or > 1024 * 1024)
          return [];

        var buffer = Marshal.AllocHGlobal((int)size);
        try {
          if (!Native.ReadProcessMemory(process, environment, buffer, size, out var read) || read == 0)
            return [];

          return ParseEnvironmentBlock(buffer, (int)Math.Min(read, size));
        } finally {
          Marshal.FreeHGlobal(buffer);
        }
      } finally {
        Marshal.FreeHGlobal(basicInformation);
      }
    } finally {
      Native.CloseHandle(process);
    }
  }

  private static bool TryReadPointer(nint process, nint address, out nint value) {
    var buffer = Marshal.AllocHGlobal(nint.Size);
    try {
      if (!Native.ReadProcessMemory(process, address, buffer, (nuint)nint.Size, out _)) {
        value = 0;
        return false;
      }

      value = Marshal.ReadIntPtr(buffer);
      return true;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static bool TryReadUInt32(nint process, nint address, out uint value) {
    var buffer = Marshal.AllocHGlobal(sizeof(uint));
    try {
      if (!Native.ReadProcessMemory(process, address, buffer, sizeof(uint), out _)) {
        value = 0;
        return false;
      }

      value = (uint)Marshal.ReadInt32(buffer);
      return true;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// The block is UTF-16 <c>NAME=VALUE</c> strings, each NUL-terminated, the run ending with an empty
  /// one. A name beginning with <c>=</c> is a per-drive working directory that Windows keeps in here;
  /// it is skipped, because it is not an environment variable anybody set.
  /// </summary>
  private static List<KeyValuePair<string, string>> ParseEnvironmentBlock(nint buffer, int bytes) {
    var result = new List<KeyValuePair<string, string>>();
    var characters = bytes / sizeof(char);
    var start = 0;
    for (var i = 0; i < characters; ++i) {
      if (Marshal.ReadInt16(buffer, i * sizeof(char)) != 0)
        continue;

      if (i == start)
        break;

      var entry = Marshal.PtrToStringUni(buffer + start * sizeof(char), i - start);
      start = i + 1;
      if (string.IsNullOrEmpty(entry) || entry[0] == '=')
        continue;

      var equals = entry.IndexOf('=', StringComparison.Ordinal);
      if (equals > 0)
        result.Add(new(entry[..equals], entry[(equals + 1)..]));
    }

    return result;
  }

}
