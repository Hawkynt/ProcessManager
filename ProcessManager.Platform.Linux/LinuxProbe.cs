using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Reads the machine through <c>/proc</c>.
/// </summary>
/// <remarks>
/// Everything is relative to <see cref="LinuxProbeOptions.ProcRoot"/>, never to a hard-coded
/// <c>/proc</c>, which is what lets the whole probe run against a recorded tree in the tests
/// (PRD §9.1). Nothing here computes a rate or a percentage: the probe reports counters, and
/// <see cref="Sampling.SnapshotDelta"/> does the arithmetic (PRD §2).
/// </remarks>
public sealed class LinuxProbe : ISystemProbe {

  private static ReadOnlySpan<byte> _cpuPrefix => "cpu"u8;
  private static ReadOnlySpan<byte> _btimePrefix => "btime "u8;
  private static ReadOnlySpan<byte> _ctxtPrefix => "ctxt "u8;
  private static ReadOnlySpan<byte> _intrPrefix => "intr "u8;
  private static ReadOnlySpan<byte> _procsPrefix => "processes "u8;
  private static ReadOnlySpan<byte> _procsRunningPrefix => "procs_running "u8;

  private readonly LinuxProbeOptions _options;
  private readonly ProcIo _io;
  private readonly ProcFileReader _reader;
  private readonly UserNameResolver _users;
  private readonly Dictionary<ProcessKey, ProcessCache> _cache = [];
  private readonly List<ProcessKey> _stale = [];
  private readonly List<int> _pids = [];
  // One buffer for every getdents64 call. 32 KiB holds about a thousand short names, so a process
  // with a few hundred descriptors is one syscall rather than one per entry.
  private readonly byte[] _directoryScratch = new byte[32 * 1024];
  // A second buffer, because GetHandleCount is called from the UI thread while a sample may be
  // running on the background one, and they must not share a scratch.
  private readonly byte[] _onDemandScratch = new byte[32 * 1024];
  private readonly double _nanosecondsPerTick;
  private readonly string _procRoot;
  private readonly byte[] _procRootUtf8;
  private readonly int _effectiveUserId;

  private long _bootTimeUtcTicks;
  private int _generation;

  public LinuxProbe() : this(new LinuxProbeOptions()) { }

  public LinuxProbe(LinuxProbeOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    this._options = options;
    this._io = options.UsePortableFileAccess ? new ManagedProcIo() : ProcIo.ForCurrentPlatform;
    this._reader = new(this._io);
    this._procRoot = options.ProcRoot.TrimEnd('/');
    this._procRootUtf8 = System.Text.Encoding.UTF8.GetBytes(this._procRoot);
    this._users = new(options.PasswdPath);
    this._nanosecondsPerTick = 1_000_000_000d / Math.Max(1, options.ClockTicksPerSecond);
    this._effectiveUserId = options.EffectiveUserId;
  }

  public string Description => $"linux:{this._procRoot}";

  public void Dispose() { }

  private HostInfo? _host;

  /// <summary>Read once; nothing in it changes while the program runs, except the live clock speed.</summary>
  public HostInfo DescribeHost()
    => this._host ??= LinuxHostReader.Read(
      this._options.ProcRoot,
      this._options.SysRoot,
      // CPUID answers about the processor running it and about no other, so it is only the truth
      // when the files beside it are this machine's as well.
      live: this._options.ProcRoot == "/proc" && this._options.SysRoot == "/sys"
    );

  public void Sample(SystemSnapshot snapshot) {
    ArgumentNullException.ThrowIfNull(snapshot);

    ++this._generation;
    this.ReadSystem(snapshot);
    this.ReadProcesses(snapshot);
    this.PruneCache();
  }

  #region system-wide

  private void ReadSystem(SystemSnapshot snapshot) {
    ref var system = ref snapshot.System;
    system.CoreCount = Environment.ProcessorCount;

    this.ReadStat(snapshot, ref system);
    this.ReadMemInfo(ref system);
    this.ReadLoadAverage(ref system);
    this.ReadUptime(ref system);
  }

  private void ReadStat(SystemSnapshot snapshot, ref SystemCounters system) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, "stat"u8), out var content, out _))
      return;

    // Two passes would mean reading the file twice; instead the per-core lines are counted on the
    // fly and the buffer is grown once, because /proc/stat lists cpu0..cpuN before anything else.
    var cores = 0;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (!AsciiScanner.StartsWith(line, _cpuPrefix))
        break;
      if (line.Length > 3 && line[3] != (byte)' ')
        ++cores;
    }

    var perCore = snapshot.PrepareCores(cores);
    var index = 0;
    scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      if (AsciiScanner.StartsWith(line, _cpuPrefix)) {
        var isAggregate = line.Length > 3 && line[3] == (byte)' ';
        var times = ParseCpuLine(line, this._nanosecondsPerTick);
        if (isAggregate)
          system.Cpu = times;
        else if (index < perCore.Length)
          perCore[index++] = times;

        continue;
      }

      if (AsciiScanner.StartsWith(line, _btimePrefix)) {
        var seconds = (long)AsciiScanner.ParseUInt64(line[_btimePrefix.Length..]);
        this._bootTimeUtcTicks = DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond;
      } else if (AsciiScanner.StartsWith(line, _ctxtPrefix))
        system.ContextSwitches = Counter.Of(AsciiScanner.ParseUInt64(line[_ctxtPrefix.Length..]));
      else if (AsciiScanner.StartsWith(line, _intrPrefix))
        system.Interrupts = Counter.Of(AsciiScanner.ParseUInt64(line[_intrPrefix.Length..]));
      else if (AsciiScanner.StartsWith(line, _procsPrefix))
        system.ProcessesCreated = Counter.Of(AsciiScanner.ParseUInt64(line[_procsPrefix.Length..]));
      else if (AsciiScanner.StartsWith(line, _procsRunningPrefix))
        system.RunningProcesses = (int)AsciiScanner.ParseUInt64(line[_procsRunningPrefix.Length..]);
    }

    if (cores > 0)
      system.CoreCount = cores;
  }

  private static CpuTimes ParseCpuLine(ReadOnlySpan<byte> line, double nanosecondsPerTick) {
    var scanner = new AsciiScanner(line);
    scanner.NextField();                                   // "cpu" / "cpuN"
    return new() {
      UserNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      NiceNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      KernelNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      IdleNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      IoWaitNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      IrqNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      SoftIrqNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
      StealNs = Scale(scanner.NextUInt64(), nanosecondsPerTick),
    };
  }

  private static ulong Scale(ulong ticks, double nanosecondsPerTick) => (ulong)(ticks * nanosecondsPerTick);

  private void ReadMemInfo(ref SystemCounters system) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, "meminfo"u8), out var content, out _))
      return;

    // Dirty and Writeback are two stages of one thing — changed and not yet on disk, and changed
    // and on its way — and a reader cares about the sum. Accumulated in a local rather than into the
    // counter, so this stays right whether or not the caller zeroed the snapshot first.
    var dirty = 0ul;
    var writeback = 0ul;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      // Every value in meminfo is in kB, whatever the unit column says.
      if (TryValue(line, "MemTotal:"u8, out var value))
        system.TotalMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "MemAvailable:"u8, out value))
        system.AvailableMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Cached:"u8, out value))
        system.CachedMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "MemFree:"u8, out value))
        system.FreeMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Buffers:"u8, out value))
        system.BufferMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Dirty:"u8, out value))
        dirty = value * 1024;
      else if (TryValue(line, "Writeback:"u8, out value))
        writeback = value * 1024;
      else if (TryValue(line, "Committed_AS:"u8, out value))
        system.CommittedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "CommitLimit:"u8, out value))
        system.CommitLimitBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SReclaimable:"u8, out value))
        system.ReclaimableKernelBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SUnreclaim:"u8, out value))
        system.UnreclaimableKernelBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "PageTables:"u8, out value))
        system.PageTableBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "KernelStack:"u8, out value))
        system.KernelStackBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Shmem:"u8, out value))
        system.SharedMemoryBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SwapTotal:"u8, out value))
        system.TotalSwapBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SwapFree:"u8, out value)) {
        var total = system.TotalSwapBytes.GetValueOrDefault();
        var free = value * 1024;
        system.UsedSwapBytes = Counter.Of(total >= free ? total - free : 0);
      }
    }

    system.ModifiedMemoryBytes = Counter.Of(dirty + writeback);
  }

  private static bool TryValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong value) {
    value = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    var scanner = new AsciiScanner(line[key.Length..]);
    value = scanner.NextUInt64();
    return true;
  }

  private void ReadLoadAverage(ref SystemCounters system) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, "loadavg"u8), out var content, out _))
      return;

    var scanner = new AsciiScanner(content);
    system.LoadAverage1 = ParseDouble(scanner.NextField());
    system.LoadAverage5 = ParseDouble(scanner.NextField());
    system.LoadAverage15 = ParseDouble(scanner.NextField());
  }

  private void ReadUptime(ref SystemCounters system) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, "uptime"u8), out var content, out _))
      return;

    var scanner = new AsciiScanner(content);
    system.UptimeSeconds = ParseDouble(scanner.NextField());
  }

  /// <summary>A fixed-point decimal with no exponent, which is all /proc ever writes.</summary>
  private static double ParseDouble(ReadOnlySpan<byte> field) {
    var dot = field.IndexOf((byte)'.');
    if (dot < 0)
      return AsciiScanner.ParseUInt64(field);

    var whole = AsciiScanner.ParseUInt64(field[..dot]);
    var fractionSpan = field[(dot + 1)..];
    var fraction = AsciiScanner.ParseUInt64(fractionSpan);
    var scale = Math.Pow(10, fractionSpan.Length);
    return whole + fraction / scale;
  }

  #endregion

  #region processes

  private void ReadProcesses(SystemSnapshot snapshot) {
    this._pids.Clear();
    Span<byte> rootPath = stackalloc byte[ProcPath.MaxLength];
    this._procRootUtf8.CopyTo(rootPath);
    rootPath[this._procRootUtf8.Length] = 0;
    this._io.ListNumericEntries(rootPath[..(this._procRootUtf8.Length + 1)], this._directoryScratch, this._pids);

    var buffer = snapshot.PrepareProcesses(this._pids.Count);
    var written = 0;
    var totalThreads = 0;
    foreach (var pid in this._pids)
      if (this.ReadProcess(pid, ref buffer[written])) {
        totalThreads += buffer[written].ThreadCount;
        ++written;
      }

    // Processes that exited between the listing and their own stat file leave a hole; the snapshot
    // is shortened rather than carrying a half-filled record.
    snapshot.PrepareProcesses(written);

    this.ReadDevices(snapshot);

    // Summed here rather than left at zero. Windows gets this free from its bulk query and Linux
    // never set it, so the machine-wide thread count read as a confident zero — which is the one
    // thing this program is not allowed to do (PRD §72.3).
    snapshot.System.TotalThreads = totalThreads;
  }

  private readonly Dictionary<string, DiskInfo> _diskInfo = [];
  private readonly Dictionary<string, NetworkInterfaceInfo> _interfaceInfo = [];
  private readonly Dictionary<string, bool> _wholeDevice = [];

  /// <summary>
  /// Per-device disk and network counters — one file each for the whole machine (PRD §48, §49).
  /// </summary>
  // Reused between samples rather than stack-allocated: both carry a name, and a span of a managed
  // type cannot live on the stack. Sized once at 64, which is more disks and interfaces than any
  // machine this will meet — and the parsers stop at the span's length rather than overrunning it.
  private readonly DiskCounters[] _diskScratch = new DiskCounters[64];
  private readonly NetworkCounters[] _networkScratch = new NetworkCounters[64];
  private readonly DeviceNameCache _deviceNames = new();

  private void ReadDevices(SystemSnapshot snapshot) {
    var diskCount = 0;
    if (this._reader.TryRead($"{this._procRoot}/diskstats", out var diskstats, out _))
      diskCount = DeviceStatParser.ParseDiskStats(diskstats, this.IsWholeDevice, this._diskScratch, this._deviceNames);

    this._diskScratch.AsSpan(0, diskCount).CopyTo(snapshot.PrepareDisks(diskCount));

    var networkCount = 0;
    if (this._reader.TryRead($"{this._procRoot}/net/dev", out var netdev, out _))
      networkCount = DeviceStatParser.ParseNetDev(netdev, this._networkScratch, this._deviceNames);

    this._networkScratch.AsSpan(0, networkCount).CopyTo(snapshot.PrepareNetworks(networkCount));
  }

  /// <summary>Cached: /sys/block is walked once per device name, not once per sample.</summary>
  private bool IsWholeDevice(string name) {
    if (this._wholeDevice.TryGetValue(name, out var known))
      return known;

    var whole = LinuxDeviceReader.IsWholeDevice(this._options.SysRoot, name);
    this._wholeDevice[name] = whole;
    return whole;
  }

  /// <summary>What each disk is, read once (PRD §48).</summary>
  public DiskInfo DescribeDisk(string name) {
    if (this._diskInfo.TryGetValue(name, out var known))
      return known;

    var info = LinuxDeviceReader.Describe(this._options.SysRoot, name);
    this._diskInfo[name] = info;
    return info;
  }

  /// <summary>How the cores are arranged, read once — the machine will not rearrange them.</summary>
  public CpuTopology DescribeTopology()
    => this._topology ??= LinuxHostReader.ReadTopology(this._options.SysRoot);

  private CpuTopology? _topology;

  /// <summary>
  /// Every graphics adapter, read fresh: unlike a disk's model, a GPU's utilisation is the point.
  /// </summary>
  public IReadOnlyList<GpuInfo> DescribeGpus() => LinuxDeviceReader.DescribeGpus(this._options.SysRoot);

  /// <summary>What each interface is, read once (PRD §49).</summary>
  public NetworkInterfaceInfo DescribeInterface(string name) {
    if (this._interfaceInfo.TryGetValue(name, out var known))
      return known;

    var info = LinuxDeviceReader.DescribeInterface(this._options.SysRoot, name);
    this._interfaceInfo[name] = info;
    return info;
  }

  private bool ReadProcess(int pid, ref ProcessRecord record) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    var statPath = ProcPath.Build(pathBuffer, this._procRootUtf8, pid, "stat"u8);
    if (!this._reader.TryRead(statPath, out var content, out _))
      return false;

    record = default;
    if (!ParseStat(content, this._nanosecondsPerTick, this._options.PageSize, ref record))
      return false;

    record.Key = new(pid, record.Key.StartTicks);
    record.StartTimeUtcTicks = this._bootTimeUtcTicks
      + (long)(record.Key.StartTicks * this._nanosecondsPerTick / 100);          // ns → 100 ns ticks

    var cache = this.GetCache(record.Key, pid);
    cache.Generation = this._generation;
    record.Name = cache.UpdateName(content);

    this.ReadStatus(cache, ref record);
    this.ReadIo(cache, ref record);
    this.ReadFileDescriptorCount(cache, ref record);
    cache.EnsureStatics(this._reader, this._options, this._procRootUtf8, this._procRoot);

    record.CommandLine = cache.CommandLine;
    record.ImagePath = cache.ImagePath;
    record.ContainerPath = cache.ContainerPath;
    record.UserName = this._users.Resolve(record.UserId);
    return true;
  }

  /// <summary>
  /// Parses <c>/proc/[pid]/stat</c>. Everything after the command name is positional, and the
  /// command name itself is the reason this cannot be a plain split: it is wrapped in parentheses,
  /// it may contain spaces, and it may contain <c>)</c> — a process named <c>foo) 0 (bar</c> is
  /// legal and has been used to confuse exactly this parser. Scanning back from the *last* <c>)</c>
  /// is the only correct reading (PRD §5.1, §9.3).
  /// </summary>
  internal static bool ParseStat(
    ReadOnlySpan<byte> content,
    double nanosecondsPerTick,
    long pageSize,
    ref ProcessRecord record
  ) {
    var open = content.IndexOf((byte)'(');
    var close = content.LastIndexOf((byte)')');
    if (open < 0 || close < open)
      return false;

    var scanner = new AsciiScanner(content[(close + 1)..]);
    var state = scanner.NextField();
    record.State = state.IsEmpty ? ProcessState.Unknown : MapState(state[0]);
    record.ParentPid = scanner.NextInt32();                // 4 ppid
    scanner.Skip(1);                                       // 5 pgrp
    record.SessionId = scanner.NextInt32();                // 6 session
    scanner.Skip(3);                                       // 7 tty_nr, 8 tpgid, 9 flags
    var minorFaults = scanner.NextUInt64();                // 10 minflt
    scanner.Skip(1);                                       // 11 cminflt
    var majorFaults = scanner.NextUInt64();                // 12 majflt
    scanner.Skip(1);                                       // 13 cmajflt
    var utime = scanner.NextUInt64();                      // 14
    var stime = scanner.NextUInt64();                      // 15
    scanner.Skip(2);                                       // 16 cutime, 17 cstime
    record.Priority = scanner.NextInt32();                 // 18
    record.Nice = scanner.NextInt32();                     // 19
    record.ThreadCount = scanner.NextInt32();              // 20
    scanner.Skip(1);                                       // 21 itrealvalue
    var startTicks = scanner.NextUInt64();                 // 22
    var virtualBytes = scanner.NextUInt64();               // 23
    var rssPages = scanner.NextUInt64();                   // 24
    scanner.Skip(14);                                      // 25 rsslim .. 38 delayacct_blkio_ticks
    record.LastCpu = scanner.NextInt32();                  // 39 processor

    record.Key = new(0, startTicks);
    record.UserTimeNs = Counter.Of((ulong)(utime * nanosecondsPerTick));
    record.KernelTimeNs = Counter.Of((ulong)(stime * nanosecondsPerTick));
    record.CpuTimeNs = Counter.Of((ulong)((utime + stime) * nanosecondsPerTick));
    record.VirtualBytes = Counter.Of(virtualBytes);
    record.WorkingSetBytes = Counter.Of(rssPages * (ulong)pageSize);
    // Minor and major together: the column asks how much this process is faulting, not which kind.
    record.PageFaults = Counter.Of(minorFaults + majorFaults);
    record.IsSuspended = record.State == ProcessState.Stopped;
    record.UserId = -1;
    record.PrivateBytes = Counter.NotSupported;
    record.PrivateWorkingSetBytes = Counter.NotSupported;
    record.PeakWorkingSetBytes = Counter.NotSupported;
    record.PeakVirtualBytes = Counter.NotSupported;
    record.PagedPoolBytes = Counter.NotSupported;
    record.PeakPagedPoolBytes = Counter.NotSupported;
    record.NonPagedPoolBytes = Counter.NotSupported;
    record.PeakNonPagedPoolBytes = Counter.NotSupported;
    // Linux does not count cycles per process. Saying so beats a zero (PRD §3.4).
    record.Cycles = Counter.NotSupported;
    record.OtherBytes = Counter.NotSupported;
    record.SwapBytes = Counter.NotSupported;
    record.ReadBytes = Counter.NotSupported;
    record.WriteBytes = Counter.NotSupported;
    record.HandleCount = Counter.NotSupported;
    record.ContextSwitches = Counter.NotSupported;
    record.MemoryLimitBytes = Counter.NotSupported;
    record.Name = string.Empty;
    return true;
  }

  private static ProcessState MapState(byte c) => c switch {
    (byte)'R' => ProcessState.Running,
    (byte)'S' => ProcessState.Sleeping,
    (byte)'D' => ProcessState.DiskSleep,
    (byte)'T' => ProcessState.Stopped,
    (byte)'t' => ProcessState.Traced,
    (byte)'Z' => ProcessState.Zombie,
    (byte)'I' => ProcessState.Idle,
    (byte)'X' or (byte)'x' => ProcessState.Dead,
    _ => ProcessState.Unknown,
  };

  private void ReadStatus(ProcessCache cache, ref ProcessRecord record) {
    // Set before anything is parsed, because default(Counter) is a confident zero: a kernel that
    // does not write one of these lines, or a status we could not open at all, must leave the field
    // unknown rather than claiming the process is unprivileged and unconfined (PRD §72.3).
    record.IsElevated = Counter.NotSupported;
    record.SeccompMode = Counter.NotSupported;
    record.NoNewPrivileges = Counter.NotSupported;
    record.EffectiveCapabilities = Counter.NotSupported;
    record.EffectiveUserId = -1;
    // Linux confines processes with capabilities and LSMs, not with an integrity level.
    record.IntegrityLevel = Counter.NotSupported;

    if (!this._reader.TryRead(cache.StatusPath, out var content, out var errno)) {
      if (errno is Native.EACCES or Native.EPERM) {
        record.PrivateBytes = Counter.NotPermitted;
        record.ContextSwitches = Counter.NotPermitted;
        record.IsElevated = Counter.NotPermitted;
        record.SeccompMode = Counter.NotPermitted;
        record.NoNewPrivileges = Counter.NotPermitted;
        record.EffectiveCapabilities = Counter.NotPermitted;
      }

      return;
    }

    ulong rssAnon = 0, swap = 0, voluntary = 0, involuntary = 0, data = 0, peakVirtual = 0, peakRss = 0;
    var haveRssAnon = false;
    var haveData = false;
    var haveEffectiveUid = false;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      if (AsciiScanner.StartsWith(line, "Uid:"u8)) {
        var uids = new AsciiScanner(line["Uid:"u8.Length..]);
        record.UserId = uids.NextInt32();                  // real, effective, saved, filesystem
        record.EffectiveUserId = uids.NextInt32();
        haveEffectiveUid = true;

        // Effective uid, not real: a setuid binary started by an ordinary user is running as root
        // now, which is the thing worth colouring a row for (PRD §23).
        record.IsElevated = Counter.Of(record.EffectiveUserId == 0 ? 1ul : 0ul);
      } else if (TryValue(line, "Seccomp:"u8, out var flag))
        record.SeccompMode = Counter.Of(flag);
      else if (TryValue(line, "NoNewPrivs:"u8, out flag))
        record.NoNewPrivileges = Counter.Of(flag);
      else if (AsciiScanner.StartsWith(line, "CapEff:"u8)) {
        // Bare hex with no 0x prefix, and separated from the label by a TAB rather than a space —
        // trimming only spaces left the tab in front of it and ParseHex stopped on the first
        // non-hex byte, reporting every process as having no capabilities at all.
        var mask = line["CapEff:"u8.Length..].TrimStart((byte)' ').TrimStart((byte)'\t');
        record.EffectiveCapabilities = Counter.Of(ParseHex(mask));
      }
      else if (TryValue(line, "RssAnon:"u8, out var value)) {
        rssAnon = value * 1024;
        haveRssAnon = true;
      } else if (TryValue(line, "VmData:"u8, out value)) {
        // The closest thing Linux has to Windows' commit charge: private, writable, and counted
        // whether or not it is resident. .NET reports the same figure for PrivateMemorySize64.
        data = value * 1024;
        haveData = true;
      } else if (TryValue(line, "VmPeak:"u8, out value))
        peakVirtual = value * 1024;
      else if (TryValue(line, "VmHWM:"u8, out value))
        peakRss = value * 1024;
      else if (TryValue(line, "VmSwap:"u8, out value))
        swap = value * 1024;
      else if (TryValue(line, "voluntary_ctxt_switches:"u8, out value))
        voluntary = value;
      else if (TryValue(line, "nonvoluntary_ctxt_switches:"u8, out value))
        involuntary = value;
    }

    record.SwapBytes = Counter.Of(swap);
    record.ContextSwitches = Counter.Of(voluntary + involuntary);
    if (peakVirtual > 0)
      record.PeakVirtualBytes = Counter.Of(peakVirtual);

    if (peakRss > 0)
      record.PeakWorkingSetBytes = Counter.Of(peakRss);

    if (haveData)
      record.PrivateBytes = Counter.Of(data);

    if (haveRssAnon)
      record.PrivateWorkingSetBytes = Counter.Of(rssAnon);

    // A kernel too old to report the effective uid leaves this unknown rather than guessing that
    // the real uid is also the effective one, which is false for every setuid process there is.
    if (!haveEffectiveUid)
      record.EffectiveUserId = -1;

    if (this._options.ReadSecurityContext)
      this.ReadSecurityContext(cache, ref record);
    else
      record.SecurityContextReason = UnknownReason.NotSampledYet;

    if (this._options.UseProportionalSetSize && this.MayRead(record))
      this.ReadProportionalSetSize(cache, ref record);
  }

  /// <summary>
  /// Reads the LSM label from <c>/proc/[pid]/attr/current</c> — an SELinux context on one machine,
  /// an AppArmor profile on another, and nothing at all on a kernel with neither.
  /// </summary>
  /// <remarks>
  /// One more open and read per process, which is why it is opt-in: at six hundred processes it is
  /// the same order of cost as the file-descriptor scan that had to leave the sample loop (PRD §4).
  /// </remarks>
  private void ReadSecurityContext(ProcessCache cache, ref ProcessRecord record) {
    if (!this._reader.TryRead(cache.SecurityContextPath, out var content, out var errno)) {
      // A kernel with no LSM loaded fails this read with EINVAL rather than leaving the file empty,
      // so "no security module here" and "not allowed to look" are different answers and are
      // reported as different answers.
      record.SecurityContextReason = errno is Native.EACCES or Native.EPERM
        ? UnknownReason.NotPermitted
        : UnknownReason.NotSupportedOnPlatform;

      return;
    }

    // The file is NUL-terminated and often has a trailing newline; both would end up in the column.
    var end = content.IndexOf((byte)0);
    if (end >= 0)
      content = content[..end];

    content = content.TrimEnd((byte)'\n');
    if (content.IsEmpty) {
      record.SecurityContextReason = UnknownReason.NotSupportedOnPlatform;
      return;
    }

    var text = System.Text.Encoding.UTF8.GetString(content);
    // "unconfined" is what AppArmor says when there is no profile. It is a real answer, not a
    // missing one, so it is kept rather than blanked.
    record.SecurityContext = text;
  }

  private void ReadProportionalSetSize(ProcessCache cache, ref ProcessRecord record) {
    if (!this._reader.TryRead(cache.SmapsRollupPath, out var content, out var errno)) {
      if (errno is Native.EACCES or Native.EPERM)
        record.PrivateWorkingSetBytes = Counter.NotPermitted;

      return;
    }

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (!TryValue(line, "Pss:"u8, out var value))
        continue;

      // PSS is resident by definition, so it refines the working-set figure rather than the commit
      // one — a process's share of what it maps, which is the honest "would I get this back".
      record.PrivateWorkingSetBytes = Counter.Of(value * 1024);
      return;
    }
  }

  /// <summary>
  /// Whether this process is worth opening privileged files of.
  /// </summary>
  /// <remarks>
  /// Not a permission model — the kernel is still the authority, and a denied read is still handled.
  /// This is an optimisation with a measured reason: opening another user's <c>io</c> or <c>fd</c>
  /// raises a managed exception, and on a machine where half the process table belongs to somebody
  /// else that is several hundred exceptions per sample. Asking first is free (PRD §4).
  /// </remarks>
  private bool MayRead(in ProcessRecord record)
    => this._effectiveUserId == 0 || record.UserId < 0 || record.UserId == this._effectiveUserId;

  private void ReadIo(ProcessCache cache, ref ProcessRecord record) {
    if (!this.MayRead(record)) {
      record.ReadBytes = Counter.NotPermitted;
      record.WriteBytes = Counter.NotPermitted;
      return;
    }

    if (!this._reader.TryRead(cache.IoPath, out var content, out var errno)) {
      // Since kernel 5.12 this file is 0400, so another user's I/O is not a failure to report — it
      // is the normal answer without the elevated helper (PRD §5.1, §8.3).
      record.ReadBytes = errno is Native.EACCES or Native.EPERM
        ? Counter.NotPermitted
        : Counter.Unknown(UnknownReason.ProcessExited);
      record.WriteBytes = record.ReadBytes;
      return;
    }

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (TryValue(line, "read_bytes:"u8, out var value))
        record.ReadBytes = Counter.Of(value);
      else if (TryValue(line, "write_bytes:"u8, out value))
        record.WriteBytes = Counter.Of(value);
    }
  }

  private void ReadFileDescriptorCount(ProcessCache cache, ref ProcessRecord record) {
    if (!this._options.CountFileDescriptors) {
      record.HandleCount = Counter.NotSampledYet;
      return;
    }

    if (!this.MayRead(record)) {
      record.HandleCount = Counter.NotPermitted;
      return;
    }

    record.HandleCount = this.CountDescriptors(cache.FdPath, this._directoryScratch);
  }

  private Counter CountDescriptors(scoped ReadOnlySpan<byte> fdPath, Span<byte> scratch) {
    var count = this._io.CountDirectoryEntries(fdPath, scratch, out var errno);
    return count >= 0
      ? Counter.Of((ulong)count)
      : errno switch {
        Native.EACCES or Native.EPERM => Counter.NotPermitted,
        _ => Counter.Unknown(UnknownReason.ProcessExited),
      };
  }

  /// <inheritdoc />
  public Counter GetHandleCount(ProcessKey key) {
    Span<byte> path = stackalloc byte[ProcPath.MaxLength];
    return this.CountDescriptors(
      ProcPath.Build(path, this._procRootUtf8, key.Pid, "fd"u8),
      this._onDemandScratch
    );
  }

  private ProcessCache GetCache(ProcessKey key, int pid) {
    if (this._cache.TryGetValue(key, out var cache))
      return cache;

    cache = new(this._procRootUtf8, pid);
    this._cache[key] = cache;
    return cache;
  }

  private void PruneCache() {
    this._stale.Clear();
    foreach (var (key, cache) in this._cache)
      if (cache.Generation != this._generation)
        this._stale.Add(key);

    foreach (var key in this._stale)
      this._cache.Remove(key);
  }

  #endregion

  #region details

  /// <summary>The command name from a stat line: everything between the first ( and the last ).</summary>
  private static string? ReadCommand(ReadOnlySpan<byte> content) {
    var open = content.IndexOf((byte)'(');
    var close = content.LastIndexOf((byte)')');
    return open < 0 || close <= open
      ? null
      : System.Text.Encoding.UTF8.GetString(content[(open + 1)..close]);
  }

  /// <summary>
  /// What the thread is blocked in, as a kernel symbol name.
  /// </summary>
  /// <remarks>
  /// "poll_schedule_timeout" or "futex_wait" answers "why is this hanging" without a stack walk,
  /// which is the question §2 puts first. The file reads as "0" for a running thread and is
  /// unreadable without permission, both of which mean there is nothing to show.
  /// </remarks>
  private string? ReadWaitChannel(string taskDirectory) {
    if (!this._reader.TryRead(taskDirectory + "/wchan", out var content, out _))
      return null;

    content = content.TrimEnd((byte)'\n');
    if (content.IsEmpty || content.SequenceEqual("0"u8))
      return null;

    return System.Text.Encoding.UTF8.GetString(content);
  }

  private Counter ReadThreadContextSwitches(string taskDirectory) {
    if (!this._reader.TryRead(taskDirectory + "/status", out var content, out _))
      return Counter.NotPermitted;

    ulong voluntary = 0, involuntary = 0;
    var found = false;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (TryValue(line, "voluntary_ctxt_switches:"u8, out var value)) {
        voluntary = value;
        found = true;
      } else if (TryValue(line, "nonvoluntary_ctxt_switches:"u8, out value)) {
        involuntary = value;
        found = true;
      }
    }

    return found ? Counter.Of(voluntary + involuntary) : Counter.NotSupported;
  }

  /// <summary>
  /// systemd services, from the unit files and the cgroups (PRD §41).
  /// </summary>
  /// <remarks>
  /// On request only: it walks a few hundred unit files, which is nothing once and far too much
  /// every second.
  /// </remarks>
  public IReadOnlyList<ServiceRecord> GetServices() => SystemdServiceReader.Read(
    this._options.UnitDirectories ?? ["/usr/lib/systemd/system", "/etc/systemd/system"],
    this._options.WantsDirectories ?? [
      "/etc/systemd/system/multi-user.target.wants",
      "/etc/systemd/system/graphical.target.wants",
      "/etc/systemd/system/sysinit.target.wants",
      "/etc/systemd/system/default.target.wants",
    ],
    // The whole tree, not just system.slice: a user's manager runs as user@1000.service under
    // user.slice, and starting at the system slice reports it as stopped.
    this._options.ServiceCgroupRoot ?? "/sys/fs/cgroup"
  );

  /// <summary>
  /// Who is logged in, from utmp (PRD §43).
  /// </summary>
  /// <remarks>
  /// Read on request rather than sampled: logins are rare, and the file is a fixed-size array whose
  /// length is the number of slots rather than the number of people.
  /// </remarks>
  public IReadOnlyList<SessionRecord> GetSessions() {
    if (!this._reader.TryRead(this._options.UtmpPath, out var content, out _))
      return [];

    var buffer = new SessionRecord[Math.Max(1, content.Length / UtmpParser.RecordSize)];
    var count = UtmpParser.Parse(content, buffer);
    return buffer[..count];
  }

  /// <summary>
  /// XDG autostart entries, user files overriding system ones of the same name (PRD §42).
  /// </summary>
  public IReadOnlyList<StartupEntry> GetStartupEntries() => XdgAutostartReader.Read(
    this._options.AutostartUserDirectory ?? DefaultUserAutostart(),
    this._options.AutostartSystemDirectories ?? ["/etc/xdg/autostart"],
    this._options.CurrentDesktop ?? Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
  );

  private static string? DefaultUserAutostart() {
    var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    if (!string.IsNullOrEmpty(config))
      return Path.Combine(config, "autostart");

    var home = Environment.GetEnvironmentVariable("HOME");
    return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".config", "autostart");
  }

  public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) {
    var result = new List<ThreadRecord>();
    var taskRoot = $"{this._procRoot}/{key.Pid}/task";
    if (!Directory.Exists(taskRoot))
      return result;

    foreach (var directory in Directory.EnumerateDirectories(taskRoot)) {
      var name = Path.GetFileName(directory);
      if (!int.TryParse(name, out var tid))
        continue;

      if (!this._reader.TryRead(directory + "/stat", out var content, out _))
        continue;

      // The thread's name is between the parentheses of the very line already being read, so it is
      // free: Linux gives every thread its own comm, and it is usually the most useful column here.
      var threadName = ReadCommand(content);

      var record = new ProcessRecord();
      if (!ParseStat(content, this._nanosecondsPerTick, this._options.PageSize, ref record))
        continue;

      result.Add(new(
        tid,
        record.State,
        record.CpuTimeNs,
        this._bootTimeUtcTicks + (long)(record.Key.StartTicks * this._nanosecondsPerTick / 100),
        0,
        null,
        record.Priority,
        Name: threadName,
        UserTimeNs: record.UserTimeNs,
        KernelTimeNs: record.KernelTimeNs,
        // Per-thread switch counts live in the thread's own status, which is one more file each.
        // Threads are enumerated for one process on demand, so it is affordable here in a way it
        // would not be in the sample loop (PRD §5.4).
        ContextSwitches: this.ReadThreadContextSwitches(directory),
        LastCpu: record.LastCpu,
        WaitReason: this.ReadWaitChannel(directory)
      ));
    }

    return result;
  }

  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) {
    var result = new List<ModuleRecord>();
    if (!this._reader.TryRead($"{this._procRoot}/{key.Pid}/maps", out var content, out _))
      return result;

    // One entry per distinct backing file rather than per mapping: a shared library shows up as four
    // consecutive lines (text, rodata, data, bss) and listing it four times helps nobody.
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var lineScanner = new AsciiScanner(line);
      var range = lineScanner.NextField();
      var permissions = lineScanner.NextField();
      lineScanner.Skip(3);                                 // offset, dev, inode
      var pathBytes = lineScanner.NextField();
      if (pathBytes.IsEmpty || pathBytes[0] != (byte)'/')
        continue;

      var path = Encoding.UTF8.GetString(pathBytes);
      if (!seen.Add(path))
        continue;

      var dash = range.IndexOf((byte)'-');
      var start = dash > 0 ? ParseHex(range[..dash]) : 0;
      var end = dash > 0 ? ParseHex(range[(dash + 1)..]) : 0;
      result.Add(new(path, start, end > start ? end - start : 0, Encoding.ASCII.GetString(permissions)));
    }

    return result;
  }

  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
    var result = new List<HandleRecord>();
    var fdRoot = $"{this._procRoot}/{key.Pid}/fd";
    string[] entries;
    try {
      entries = Directory.GetFiles(fdRoot);
    } catch (UnauthorizedAccessException) {
      return this.HandlesThroughHelper(key, result);
    } catch (IOException) {
      return result;
    }

    foreach (var entry in entries) {
      if (!int.TryParse(Path.GetFileName(entry), out var fd))
        continue;

      var target = this._reader.TryReadLink(entry);
      result.Add(new((ulong)fd, ClassifyFd(target), target, null));
    }

    return result;
  }

  /// <summary>
  /// The same list, asked of the helper. It answers with one <c>fd\ttarget</c> line per descriptor —
  /// the helper formats it rather than shipping a directory handle back, because a file descriptor
  /// is only meaningful in the process that holds it.
  /// </summary>
  private List<HandleRecord> HandlesThroughHelper(ProcessKey key, List<HandleRecord> result) {
    if (this._options.Elevated is not { } channel)
      return result;

    var (status, payload) = channel.Send(Abstractions.ElevatedOpcode.ListFds, key);
    if (status != Abstractions.ElevatedStatus.Ok)
      return result;

    foreach (var line in Abstractions.ElevatedProtocol.DecodePayload(payload).Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var tab = line.IndexOf('\t', StringComparison.Ordinal);
      if (tab < 0 || !int.TryParse(line.AsSpan(0, tab), out var fd))
        continue;

      var target = line[(tab + 1)..];
      result.Add(new((ulong)fd, ClassifyFd(target.Length == 0 ? null : target), target.Length == 0 ? null : target, null));
    }

    return result;
  }

  private static HandleKind ClassifyFd(string? target) => target switch {
    null => HandleKind.Unknown,
    _ when target.StartsWith("socket:", StringComparison.Ordinal) => HandleKind.Socket,
    _ when target.StartsWith("pipe:", StringComparison.Ordinal) => HandleKind.Pipe,
    _ when target.StartsWith("anon_inode:", StringComparison.Ordinal) => HandleKind.AnonInode,
    _ when Directory.Exists(target) => HandleKind.Directory,
    _ when target.StartsWith("/dev/", StringComparison.Ordinal) => HandleKind.Device,
    _ => HandleKind.File,
  };

  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) {
    // The socket inodes this process holds, joined against the four network tables. The join runs
    // once per request rather than once per process, which is what keeps it off the sampling path
    // (PRD §5.1).
    var inodes = new HashSet<ulong>();
    foreach (var handle in this.GetHandles(key)) {
      if (handle.Kind != HandleKind.Socket || handle.Name is null)
        continue;

      var open = handle.Name.IndexOf('[', StringComparison.Ordinal);
      var close = handle.Name.IndexOf(']', StringComparison.Ordinal);
      if (open >= 0 && close > open && ulong.TryParse(handle.Name.AsSpan(open + 1, close - open - 1), out var inode))
        inodes.Add(inode);
    }

    var result = new List<ConnectionRecord>();
    if (inodes.Count == 0)
      return result;

    this.CollectSockets("/net/tcp", ConnectionProtocol.Tcp, inodes, result);
    this.CollectSockets("/net/tcp6", ConnectionProtocol.Tcp6, inodes, result);
    this.CollectSockets("/net/udp", ConnectionProtocol.Udp, inodes, result);
    this.CollectSockets("/net/udp6", ConnectionProtocol.Udp6, inodes, result);
    return result;
  }

  private void CollectSockets(
    string relativePath,
    ConnectionProtocol protocol,
    HashSet<ulong> inodes,
    List<ConnectionRecord> result
  ) {
    if (!this._reader.TryRead(this._procRoot + relativePath, out var content, out _))
      return;

    var scanner = new AsciiScanner(content);
    scanner.NextLine();                                    // header
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      var lineScanner = new AsciiScanner(line);
      lineScanner.NextField();                             // slot
      var local = lineScanner.NextField();
      var remote = lineScanner.NextField();
      var state = lineScanner.NextField();
      lineScanner.Skip(4);                                 // tx/rx queue, tr/when, retransmits, uid
      lineScanner.Skip(1);                                 // timeout
      var inode = lineScanner.NextUInt64();
      if (!inodes.Contains(inode))
        continue;

      var (localAddress, localPort) = SplitEndpoint(local);
      var (remoteAddress, remotePort) = SplitEndpoint(remote);
      result.Add(new(
        protocol,
        localAddress,
        localPort,
        remoteAddress,
        remotePort,
        TcpStateName((int)ParseHex(state)),
        inode
      ));
    }
  }

  private static (string Address, int Port) SplitEndpoint(ReadOnlySpan<byte> field) {
    var colon = field.LastIndexOf((byte)':');
    if (colon < 0)
      return (string.Empty, 0);

    var address = field[..colon];
    var port = (int)ParseHex(field[(colon + 1)..]);
    return (FormatHexAddress(address), port);
  }

  private static string FormatHexAddress(ReadOnlySpan<byte> hex) {
    // IPv4 arrives as eight hex digits in host byte order, IPv6 as thirty-two in four host-order
    // words. Anything else is not ours to guess at, so it is handed back as written.
    if (hex.Length == 8) {
      var value = ParseHex(hex);
      return $"{value & 0xFF}.{(value >> 8) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 24) & 0xFF}";
    }

    return Encoding.ASCII.GetString(hex);
  }

  private static string TcpStateName(int state) => state switch {
    1 => "ESTABLISHED",
    2 => "SYN_SENT",
    3 => "SYN_RECV",
    4 => "FIN_WAIT1",
    5 => "FIN_WAIT2",
    6 => "TIME_WAIT",
    7 => "CLOSE",
    8 => "CLOSE_WAIT",
    9 => "LAST_ACK",
    10 => "LISTEN",
    11 => "CLOSING",
    _ => "UNKNOWN",
  };

  private static ulong ParseHex(ReadOnlySpan<byte> field) {
    ulong value = 0;
    for (var i = 0; i < field.Length; ++i) {
      var c = field[i];
      var digit = c switch {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => -1,
      };

      if (digit < 0)
        break;

      value = value * 16 + (ulong)digit;
    }

    return value;
  }

  public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) {
    var result = new List<KeyValuePair<string, string>>();
    if (!this._reader.TryRead($"{this._procRoot}/{key.Pid}/environ", out var content, out var errno)) {
      // Another user's environment block is not ours to read. If a helper is running, it is — and
      // this is exactly the kind of one-shot question worth a round trip (PRD §8).
      if (errno is not (Native.EACCES or Native.EPERM) || this._options.Elevated is not { } channel)
        return result;

      var (status, payload) = channel.Send(Abstractions.ElevatedOpcode.ReadEnviron, key);
      if (status != Abstractions.ElevatedStatus.Ok)
        return result;

      ParseEnvironment(payload, result);
      return result;
    }

    ParseEnvironment(content, result);
    return result;
  }

  private static void ParseEnvironment(ReadOnlySpan<byte> content, List<KeyValuePair<string, string>> result) {
    foreach (var range in content.Split((byte)0)) {
      var entry = content[range];
      if (entry.IsEmpty)
        continue;

      var equals = entry.IndexOf((byte)'=');
      if (equals < 0)
        continue;

      result.Add(new(Encoding.UTF8.GetString(entry[..equals]), Encoding.UTF8.GetString(entry[(equals + 1)..])));
    }
  }

  #endregion

}
