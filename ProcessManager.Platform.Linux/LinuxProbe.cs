using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

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
  // Kept on the probe rather than made per call, because its whole value is that the second process
  // somebody inspects does not re-read the ELF header of the libc both of them map.
  private readonly ModuleImageReader _images = new();
  private readonly Dictionary<ProcessKey, ProcessCache> _cache = [];
  private readonly List<ProcessKey> _stale = [];
  private readonly List<int> _pids = [];
  // One cgroup's throttling counter, for the length of one sample. Several hundred processes share
  // a few dozen groups, so this is the difference between a file per process and a file per group.
  private readonly Dictionary<string, Counter> _throttling = [];
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
    if (options.ReadGpuUsage)
      this._gpu = new(options, this._reader, this._io, this._procRootUtf8);
  }

  /// <summary>Per-process graphics accounting, or null when it was not asked for (PRD §5.4, §19).</summary>
  private readonly LinuxGpuAccounting? _gpu;

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
    // Once for the whole sample, before the loop: NVML answers about all of a card's clients in one
    // call, and asking it per process would put the same question six hundred times.
    this._gpu?.BeginSample();
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
    this.ReadPressure(ref system);
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

  /// <summary>
  /// Everything <c>/proc/meminfo</c> will say (PRD §47).
  /// </summary>
  /// <remarks>
  /// A line this kernel does not publish leaves its counter reading "this kernel has no such thing"
  /// rather than zero — <c>Zswap</c> on a machine built without it, <c>Percpu</c> before 4.14, the
  /// huge-page lines on a kernel without <c>CONFIG_HUGETLB_PAGE</c>. Every one of those is a real
  /// configuration, and a zero would describe each of them as a machine that has the feature and is
  /// not using it (PRD §5.3).
  /// </remarks>
  private void ReadMemInfo(ref SystemCounters system) {
    // Before the read rather than after it: a machine with no /proc/meminfo at all — a container
    // with a lockdown mount, a fixture recorded without it — must not report a machine with no
    // memory either.
    MarkMemoryUnsupported(ref system);

    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, "meminfo"u8), out var content, out _))
      return;

    // Dirty and Writeback are two stages of one thing — changed and not yet on disk, and changed
    // and on its way — and the composition bar cares about the sum. Kept as counters rather than as
    // numbers so that the sum of two figures, one of which was never reported, is not a figure.
    var dirty = Counter.NotSupported;
    var writeback = Counter.NotSupported;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      // Every value in meminfo is in kB, whatever the unit column says — except the four
      // HugePages_* lines, which are counts of pages and carry no unit at all.
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
        system.DirtyBytes = dirty = Counter.Of(value * 1024);
      else if (TryValue(line, "Writeback:"u8, out value))
        system.WritebackBytes = writeback = Counter.Of(value * 1024);
      else if (TryValue(line, "AnonPages:"u8, out value))
        system.AnonymousBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Mapped:"u8, out value))
        system.MappedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SwapCached:"u8, out value))
        system.SwapCachedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Zswap:"u8, out value))
        system.CompressedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Zswapped:"u8, out value))
        system.CompressedOriginalBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Slab:"u8, out value))
        system.SlabBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Unevictable:"u8, out value))
        system.UnevictableBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Mlocked:"u8, out value))
        system.LockedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "VmallocUsed:"u8, out value))
        system.VmallocUsedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Percpu:"u8, out value))
        system.PerCpuBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "HardwareCorrupted:"u8, out value))
        system.HardwareCorruptedBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Hugepagesize:"u8, out value))
        system.HugePageSizeBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "HugePages_Total:"u8, out value))
        system.HugePagesTotal = Counter.Of(value);
      else if (TryValue(line, "HugePages_Free:"u8, out value))
        system.HugePagesFree = Counter.Of(value);
      else if (TryValue(line, "HugePages_Rsvd:"u8, out value))
        system.HugePagesReserved = Counter.Of(value);
      else if (TryValue(line, "Hugetlb:"u8, out value))
        system.HugeTlbBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "AnonHugePages:"u8, out value))
        system.AnonymousHugePagesBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "ShmemHugePages:"u8, out value))
        system.SharedHugePagesBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "FileHugePages:"u8, out value))
        system.FileHugePagesBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Active(anon):"u8, out value))
        system.ActiveAnonymousBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Inactive(anon):"u8, out value))
        system.InactiveAnonymousBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Active(file):"u8, out value))
        system.ActiveFileBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "Inactive(file):"u8, out value))
        system.InactiveFileBytes = Counter.Of(value * 1024);
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

    system.ModifiedMemoryBytes = dirty.HasValue && writeback.HasValue
      ? Counter.Of(dirty.Value + writeback.Value)
      : Counter.NotSupported;
  }

  /// <summary>
  /// Every memory counter set to "this kernel does not publish it", before anything is parsed.
  /// </summary>
  /// <remarks>
  /// The snapshot arrives saying nobody has sampled yet, which is true of a machine before its first
  /// tick and untrue of a line that is simply not in this kernel's <c>meminfo</c>. Telling a reader
  /// to wait a second for a figure that will never arrive is the wrong sentence (PRD §45.6).
  /// </remarks>
  private static void MarkMemoryUnsupported(ref SystemCounters system) {
    system.TotalMemoryBytes = Counter.NotSupported;
    system.AvailableMemoryBytes = Counter.NotSupported;
    system.CachedMemoryBytes = Counter.NotSupported;
    system.FreeMemoryBytes = Counter.NotSupported;
    system.BufferMemoryBytes = Counter.NotSupported;
    system.ModifiedMemoryBytes = Counter.NotSupported;
    system.DirtyBytes = Counter.NotSupported;
    system.WritebackBytes = Counter.NotSupported;
    system.AnonymousBytes = Counter.NotSupported;
    system.MappedBytes = Counter.NotSupported;
    system.SwapCachedBytes = Counter.NotSupported;
    system.CompressedBytes = Counter.NotSupported;
    system.CompressedOriginalBytes = Counter.NotSupported;
    system.SlabBytes = Counter.NotSupported;
    system.UnevictableBytes = Counter.NotSupported;
    system.LockedBytes = Counter.NotSupported;
    system.VmallocUsedBytes = Counter.NotSupported;
    system.PerCpuBytes = Counter.NotSupported;
    system.HardwareCorruptedBytes = Counter.NotSupported;
    system.HugePageSizeBytes = Counter.NotSupported;
    system.HugePagesTotal = Counter.NotSupported;
    system.HugePagesFree = Counter.NotSupported;
    system.HugePagesReserved = Counter.NotSupported;
    system.HugeTlbBytes = Counter.NotSupported;
    system.AnonymousHugePagesBytes = Counter.NotSupported;
    system.SharedHugePagesBytes = Counter.NotSupported;
    system.FileHugePagesBytes = Counter.NotSupported;
    system.ActiveAnonymousBytes = Counter.NotSupported;
    system.InactiveAnonymousBytes = Counter.NotSupported;
    system.ActiveFileBytes = Counter.NotSupported;
    system.InactiveFileBytes = Counter.NotSupported;
    system.CommittedBytes = Counter.NotSupported;
    system.CommitLimitBytes = Counter.NotSupported;
    system.ReclaimableKernelBytes = Counter.NotSupported;
    system.UnreclaimableKernelBytes = Counter.NotSupported;
    system.PageTableBytes = Counter.NotSupported;
    system.KernelStackBytes = Counter.NotSupported;
    system.SharedMemoryBytes = Counter.NotSupported;
    system.TotalSwapBytes = Counter.NotSupported;
    system.UsedSwapBytes = Counter.NotSupported;
  }

  private static bool TryValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong value) {
    value = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    var scanner = new AsciiScanner(line[key.Length..]);
    value = scanner.NextUInt64();
    return true;
  }

  /// <summary>
  /// One of the five <c>Cap*</c> lines of <c>status</c>, as the mask it spells.
  /// </summary>
  /// <remarks>
  /// Bare hex with no <c>0x</c> prefix, and separated from its label by a TAB rather than a space —
  /// trimming only spaces left the tab in front of the digits and <see cref="AsciiScanner.ParseHex"/>
  /// stopped on the first non-hex byte, reporting every process on the machine as having no
  /// capabilities at all. A security field that was confidently, silently wrong, which is why the
  /// trim is here once rather than at five call sites.
  /// </remarks>
  private static bool TryMask(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong mask) {
    mask = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    mask = AsciiScanner.ParseHex(line[key.Length..].TrimStart((byte)' ').TrimStart((byte)'\t'));
    return true;
  }

  /// <summary>
  /// Pressure stall information (PRD §46).
  /// </summary>
  /// <remarks>
  /// Three small files for the whole machine rather than one per process, so this costs three reads
  /// a sample however many processes there are — which is why it can be sampled at all, unlike the
  /// per-process figures of §5.4.
  /// <para>
  /// A kernel built without <c>CONFIG_PSI</c>, or booted with <c>psi=0</c>, has no such files. That
  /// leaves the readings unknown rather than zero: a machine under no pressure and a machine that
  /// cannot say look identical otherwise, and one of them may be thrashing.
  /// </para>
  /// </remarks>
  private void ReadPressure(ref SystemCounters system) {
    system.CpuPressure = this.ReadPressureFile("pressure/cpu"u8);
    system.MemoryPressure = this.ReadPressureFile("pressure/memory"u8);
    system.IoPressure = this.ReadPressureFile("pressure/io"u8);
  }

  private PressureReading ReadPressureFile(ReadOnlySpan<byte> leaf) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, leaf), out var content, out _))
      return PressureReading.Unknown;

    // The file is a few dozen ASCII bytes; decoding it is the one allocation on this path and is
    // three per sample rather than three per process.
    Span<char> text = stackalloc char[256];
    var length = Math.Min(content.Length, text.Length);
    for (var i = 0; i < length; ++i)
      text[i] = (char)content[i];

    return PressureParser.Parse(text[..length]);
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
    // A cgroup's counters are read once per sample and shared by every process in the group; kept
    // no longer than that, because the point of the column is that the number moves.
    this._throttling.Clear();
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
    var kept = snapshot.PrepareProcesses(written);

    // §19: an unsupported driver stack renders capability state and never a zero. A machine whose
    // cards answer neither NVML nor the kernel's own client accounting has just had every process
    // recorded as using none of the GPU, which would be a confident nought about a card that may
    // well be at a hundred percent. It can only be known after the loop, because "no client
    // anywhere" and "nothing published anywhere" look identical until somebody has looked.
    if (this._gpu is { CanRead: false })
      for (var i = 0; i < kept.Length; ++i)
        LinuxGpuAccounting.NotCollected(ref kept[i], UnknownReason.NotImplementedHere);

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

  /// <summary>
  /// The limits the process runs under, read fresh: a container's quota can be changed while it runs.
  /// </summary>
  public CgroupInfo? DescribeCgroup(ProcessKey key) {
    Span<byte> pathBuffer = stackalloc byte[ProcPath.MaxLength];
    if (!this._reader.TryRead(ProcPath.Build(pathBuffer, this._procRootUtf8, key.Pid, "cgroup"u8), out var content, out _))
      return null;

    // The unified hierarchy's line is the one beginning "0::"; a v1 machine has several lines and
    // none of them begins that way, which is how a v1 layout reports itself as unreadable rather
    // than as half an answer.
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (!AsciiScanner.StartsWith(line, "0::"u8))
        continue;

      var path = System.Text.Encoding.UTF8.GetString(line[3..]);
      return CgroupReader.Read(this._options.CgroupRoot, path);
    }

    return null;
  }

  /// <summary>
  /// What the running program actually is (PRD §14).
  /// </summary>
  /// <remarks>
  /// The executable is read through <c>/proc/[pid]/exe</c> rather than through the path it reports.
  /// The two differ exactly when it matters: a program whose binary was replaced or deleted while it
  /// ran still has a readable <c>exe</c> link to the old inode, and the path on disk now names
  /// something else or nothing.
  /// </remarks>
  public ImageInfo? DescribeImage(ProcessKey key) {
    var directory = Path.Combine(this._options.ProcRoot, key.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (!Directory.Exists(directory))
      return null;

    var executable = Path.Combine(directory, "exe");
    var path = TryLink(executable);
    var header = ReadImageHeader(executable);
    var size = Counter.NotPermitted;
    DateTime? modified = null;

    try {
      // The resolved target, not the /proc link. Asking the link its length gives nought — it is a
      // symlink, and its own size is not the executable's — which is how this first reported every
      // program as being no bytes long.
      var info = path is { Length: > 0 } ? new FileInfo(path) : null;
      if (info is { Exists: true }) {
        size = Counter.Of((ulong)Math.Max(0, info.Length));
        modified = info.LastWriteTimeUtc;
      }
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }

    return new(
      path,
      header?.Architecture,
      header is not null,
      header?.Bits ?? 0,
      header?.IsPositionIndependent,
      header?.Interpreter,
      size,
      modified,
      TryLink(Path.Combine(directory, "cwd")),
      ReadNamespaces(Path.Combine(directory, "ns"))
    );
  }

  /// <summary>
  /// The first page of an executable, which is all the header and the program headers need.
  /// </summary>
  /// <remarks>
  /// Every linker in use puts the program headers immediately after the sixty-four byte header, so
  /// a page reaches them. A file that needs more is one this reports no interpreter for, which is
  /// the same answer a static binary gets — worth knowing, and better than reading a megabyte of
  /// somebody's executable to be sure.
  /// </remarks>
  private static Query.ElfHeader.Image? ReadImageHeader(string path) {
    try {
      using var file = File.OpenRead(path);
      Span<byte> bytes = stackalloc byte[4096];
      var read = file.Read(bytes);
      return read <= 0 ? null : Query.ElfHeader.Read(bytes[..read]);
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>
  /// The namespaces a process is in, by kind and inode.
  /// </summary>
  /// <remarks>
  /// The inode is the identity: two processes sharing one share that namespace, which is how a
  /// container's members are actually told apart — rather than by a cgroup path, which anybody can
  /// write anything into.
  /// </remarks>
  private static IReadOnlyList<KeyValuePair<string, string>> ReadNamespaces(string directory) {
    if (!Directory.Exists(directory))
      return [];

    var found = new List<KeyValuePair<string, string>>();
    try {
      foreach (var entry in Directory.EnumerateFiles(directory)) {
        if (TryLink(entry) is not { Length: > 0 } target)
          continue;

        // "mnt:[4026531832]" — the kind is already in the link, so the inode alone is what is worth
        // keeping beside a name we already have from the file.
        var open = target.IndexOf('[');
        var close = target.IndexOf(']');
        found.Add(new(
          Path.GetFileName(entry),
          open >= 0 && close > open ? target[(open + 1)..close] : target
        ));
      }
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }

    found.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
    return found;
  }

  private static string? TryLink(string path) {
    try {
      return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

  /// <summary>The desktop's windows, if this session has a desktop willing to say (PRD §39).</summary>
  public WindowList GetWindows() => X11Windows.Enumerate();

  /// <summary>The window under the pointer, for picking a process by pointing at it.</summary>
  public WindowRecord? WindowUnderPointer() => X11Windows.UnderPointer();

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
    // After the key exists, and keyed on the pid only within this one sample: the GPU figures were
    // gathered a moment ago from the same instant, and nothing is carried over between samples, so a
    // pid that has been reused cannot inherit the readings of the process that had it before.
    this._gpu?.Fill(record.Key, ref record);
    cache.EnsureStatics(this._reader, this._options, this._procRootUtf8, this._procRoot);

    record.CommandLine = cache.CommandLine;
    record.ImagePath = cache.ImagePath;
    record.ContainerPath = cache.ContainerPath;
    // After the cgroup path is known, because that is what says which group's counter to read.
    this.ReadCpuThrottling(ref record);
    record.UserName = this._users.Resolve(record.UserId);
    // Almost always the same string, and taking it from the same cache costs a dictionary lookup
    // rather than an allocation. The handful of processes where the two differ are exactly the ones
    // a security column exists for (PRD §21).
    record.EffectiveUserName = record.EffectiveUserId == record.UserId
      ? record.UserName
      : this._users.Resolve(record.EffectiveUserId);

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
    // Free: the field is already under the cursor, and it was being skipped over.
    record.TerminalDevice = scanner.NextInt32();           // 7 tty_nr
    scanner.Skip(2);                                       // 8 tpgid, 9 flags
    var minorFaults = scanner.NextUInt64();                // 10 minflt
    scanner.Skip(1);                                       // 11 cminflt
    var majorFaults = scanner.NextUInt64();                // 12 majflt
    scanner.Skip(1);                                       // 13 cmajflt
    var utime = scanner.NextUInt64();                      // 14
    var stime = scanner.NextUInt64();                      // 15
    scanner.Skip(2);                                       // 16 cutime, 17 cstime
    record.Priority = scanner.NextInt32();                 // 18 priority, the kernel's own number
    record.Nice = scanner.NextInt32();                     // 19
    record.ThreadCount = scanner.NextInt32();              // 20
    scanner.Skip(1);                                       // 21 itrealvalue
    var startTicks = scanner.NextUInt64();                 // 22
    var virtualBytes = scanner.NextUInt64();               // 23
    var rssPages = scanner.NextUInt64();                   // 24
    scanner.Skip(14);                                      // 25 rsslim .. 38 exit_signal
    record.LastCpu = scanner.NextInt32();                  // 39 processor
    scanner.Skip(1);                                       // 40 rt_priority
    // Read here rather than re-scanned where it is wanted: two fields past the processor is two more
    // chances to miscount a positional line whose next-door neighbours are all plausible small
    // integers, and one parser that counts once cannot disagree with itself.
    var policy = scanner.NextField();                      // 41 policy

    record.Key = new(0, startTicks);
    // A stat line that stops short — an old kernel, a truncated read, a fixture — leaves the class
    // unknown. Defaulting to the ordinary class would be a confident answer nobody gave us.
    record.SchedulingPolicy = policy.IsEmpty
      ? SchedulingPolicy.Unknown
      : MapSchedulingPolicy(AsciiScanner.ParseUInt64(policy));
    record.UserTimeNs = Counter.Of((ulong)(utime * nanosecondsPerTick));
    record.KernelTimeNs = Counter.Of((ulong)(stime * nanosecondsPerTick));
    record.CpuTimeNs = Counter.Of((ulong)((utime + stime) * nanosecondsPerTick));
    record.VirtualBytes = Counter.Of(virtualBytes);
    record.WorkingSetBytes = Counter.Of(rssPages * (ulong)pageSize);
    // Minor and major together: the column asks how much this process is faulting, not which kind.
    record.PageFaults = Counter.Of(minorFaults + majorFaults);
    record.IsSuspended = record.State == ProcessState.Stopped;
    // -1, not 0: zero is root, so a quartet nobody filled would claim every process on the machine
    // is running as the superuser. The same reasoning as default(Counter) being a confident zero,
    // and worse here because the wrong answer is the alarming one (PRD §5.3).
    record.UserId = -1;
    record.EffectiveUserId = -1;
    record.SavedUserId = -1;
    record.FilesystemUserId = -1;
    record.GroupId = -1;
    record.EffectiveGroupId = -1;
    record.SavedGroupId = -1;
    record.FilesystemGroupId = -1;
    record.PrivateBytes = Counter.NotSupported;
    record.PrivateWorkingSetBytes = Counter.NotSupported;
    // default(Counter) is a confident zero, so a field nobody fills claims the process has none of
    // whatever it counts. Every one of these has to be stated (PRD §5.3).
    record.FileBackedBytes = Counter.NotSupported;
    record.SharedResidentBytes = Counter.NotSupported;
    record.ProportionalBytes = Counter.NotSampledYet;
    record.ProportionalSwapBytes = Counter.NotSampledYet;
    record.UniqueBytes = Counter.NotSampledYet;
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
    // Nobody has looked in the cgroup yet, and a nought here would say the group has never been
    // held back — which is a claim, not an absence.
    record.ThrottledPeriods = Counter.NotSupported;
    // Graphics is off unless asked for, so the default is "nobody looked" rather than "none" — and
    // stated here, because a record that left them alone would claim every process uses no GPU.
    LinuxGpuAccounting.NotCollected(ref record, UnknownReason.NotSampledYet);
    record.Name = string.Empty;
    return true;
  }

  /// <summary>
  /// The number in field 41 of <c>stat</c>, as a scheduler class.
  /// </summary>
  /// <remarks>
  /// The values are the <c>SCHED_*</c> constants from <c>uapi/linux/sched.h</c> and have never been
  /// renumbered — 4 is the abandoned <c>SCHED_ISO</c>, which no released kernel implements, and 7 is
  /// <c>SCHED_EXT</c> from 6.12. A number this does not know is left unknown rather than folded into
  /// the ordinary class, because the next class Linux adds will not be an ordinary one either.
  /// </remarks>
  private static SchedulingPolicy MapSchedulingPolicy(ulong policy) => policy switch {
    0 => SchedulingPolicy.Other,
    1 => SchedulingPolicy.Fifo,
    2 => SchedulingPolicy.RoundRobin,
    3 => SchedulingPolicy.Batch,
    5 => SchedulingPolicy.Idle,
    6 => SchedulingPolicy.Deadline,
    7 => SchedulingPolicy.Extensible,
    _ => SchedulingPolicy.Unknown,
  };

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
    record.SeccompFilters = Counter.NotSupported;
    record.NoNewPrivileges = Counter.NotSupported;
    record.EffectiveCapabilities = Counter.NotSupported;
    record.PermittedCapabilities = Counter.NotSupported;
    record.InheritableCapabilities = Counter.NotSupported;
    record.BoundingCapabilities = Counter.NotSupported;
    record.AmbientCapabilities = Counter.NotSupported;
    record.EffectiveUserId = -1;
    record.SavedUserId = -1;
    record.FilesystemUserId = -1;
    record.GroupId = -1;
    record.EffectiveGroupId = -1;
    record.SavedGroupId = -1;
    record.FilesystemGroupId = -1;
    record.SupplementaryGroups = null;
    record.SupplementaryGroupsReason = this._options.ReadSupplementaryGroups
      ? UnknownReason.NotSupportedOnPlatform
      : UnknownReason.NotSampledYet;
    record.CpuAffinity = null;
    record.CpuAffinityReason = this._options.ReadCpuAffinity
      ? UnknownReason.NotSupportedOnPlatform
      : UnknownReason.NotSampledYet;
    // Linux confines processes with capabilities and LSMs, not with an integrity level.
    record.IntegrityLevel = Counter.NotSupported;

    if (!this._reader.TryRead(cache.StatusPath, out var content, out var errno)) {
      if (errno is Native.EACCES or Native.EPERM) {
        record.PrivateBytes = Counter.NotPermitted;
        record.ContextSwitches = Counter.NotPermitted;
        record.IsElevated = Counter.NotPermitted;
        record.SeccompMode = Counter.NotPermitted;
        record.SeccompFilters = Counter.NotPermitted;
        record.NoNewPrivileges = Counter.NotPermitted;
        record.EffectiveCapabilities = Counter.NotPermitted;
        record.PermittedCapabilities = Counter.NotPermitted;
        record.InheritableCapabilities = Counter.NotPermitted;
        record.BoundingCapabilities = Counter.NotPermitted;
        record.AmbientCapabilities = Counter.NotPermitted;
        record.SupplementaryGroupsReason = UnknownReason.NotPermitted;
        record.CpuAffinityReason = UnknownReason.NotPermitted;
      }

      return;
    }

    ulong rssAnon = 0, swap = 0, voluntary = 0, involuntary = 0, data = 0, peakVirtual = 0, peakRss = 0;
    var haveRssAnon = false;
    var rssFile = 0ul;
    var haveRssFile = false;
    var rssShmem = 0ul;
    var haveRssShmem = false;
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
        record.SavedUserId = uids.NextInt32();
        record.FilesystemUserId = uids.NextInt32();
        haveEffectiveUid = true;

        // Effective uid, not real: a setuid binary started by an ordinary user is running as root
        // now, which is the thing worth colouring a row for (PRD §23).
        record.IsElevated = Counter.Of(record.EffectiveUserId == 0 ? 1ul : 0ul);
      } else if (AsciiScanner.StartsWith(line, "Gid:"u8)) {
        var gids = new AsciiScanner(line["Gid:"u8.Length..]);
        record.GroupId = gids.NextInt32();                 // real, effective, saved, filesystem
        record.EffectiveGroupId = gids.NextInt32();
        record.SavedGroupId = gids.NextInt32();
        record.FilesystemGroupId = gids.NextInt32();
      } else if (this._options.ReadSupplementaryGroups && AsciiScanner.StartsWith(line, "Groups:"u8)) {
        // Only when asked for: this is the one line of status that has to become a string, and a
        // string per process per sample is the allocation budget of §4 spent on a column nobody
        // opened. The line is trailing-space-padded and is empty for a kernel thread — which is a
        // real answer, not a missing one, so the empty string is kept.
        var groups = line["Groups:"u8.Length..].Trim(" \t"u8);
        record.SupplementaryGroups = groups.IsEmpty
          ? string.Empty
          : System.Text.Encoding.ASCII.GetString(groups);
        record.SupplementaryGroupsReason = UnknownReason.None;
      } else if (this._options.ReadCpuAffinity && AsciiScanner.StartsWith(line, "Cpus_allowed_list:"u8)) {
        // The list, not the mask two lines above it: "0-15" is readable on a machine where
        // "ffff" is arithmetic and "00000000,00000000,...,ffffffff" is neither. Only when asked
        // for, because it is the second line of status that has to become a string (PRD §5.4).
        var allowed = line["Cpus_allowed_list:"u8.Length..].Trim(" \t"u8);
        if (!allowed.IsEmpty) {
          record.CpuAffinity = System.Text.Encoding.ASCII.GetString(allowed);
          record.CpuAffinityReason = UnknownReason.None;
        }
      } else if (TryValue(line, "Seccomp:"u8, out var flag))
        record.SeccompMode = Counter.Of(flag);
      else if (TryValue(line, "Seccomp_filters:"u8, out flag))
        // 5.9 and newer. An older kernel writes no such line and the field stays unknown, which is
        // not the same statement as "no filters are attached".
        record.SeccompFilters = Counter.Of(flag);
      else if (TryValue(line, "NoNewPrivs:"u8, out flag))
        record.NoNewPrivileges = Counter.Of(flag);
      else if (AsciiScanner.StartsWith(line, "Cap"u8)) {
        // The five labels differ only after their third character, so one test takes the whole group
        // out of the way of the fifty other lines in status rather than each of them paying for five.
        if (TryMask(line, "CapEff:"u8, out var mask))
          record.EffectiveCapabilities = Counter.Of(mask);
        else if (TryMask(line, "CapPrm:"u8, out mask))
          record.PermittedCapabilities = Counter.Of(mask);
        else if (TryMask(line, "CapInh:"u8, out mask))
          record.InheritableCapabilities = Counter.Of(mask);
        else if (TryMask(line, "CapBnd:"u8, out mask))
          record.BoundingCapabilities = Counter.Of(mask);
        else if (TryMask(line, "CapAmb:"u8, out mask))
          record.AmbientCapabilities = Counter.Of(mask);
      } else if (TryValue(line, "RssAnon:"u8, out var value)) {
        rssAnon = value * 1024;
        haveRssAnon = true;
      } else if (TryValue(line, "RssFile:"u8, out value)) {
        // Free: the line is already in front of us. Worth splitting out because file-backed and
        // anonymous pages behave completely differently under pressure (PRD §17).
        rssFile = value * 1024;
        haveRssFile = true;
      } else if (TryValue(line, "RssShmem:"u8, out value)) {
        rssShmem = value * 1024;
        haveRssShmem = true;
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

    if (haveRssFile)
      record.FileBackedBytes = Counter.Of(rssFile);

    if (haveRssShmem)
      record.SharedResidentBytes = Counter.Of(rssShmem);

    // A kernel too old to report the effective uid leaves this unknown rather than guessing that
    // the real uid is also the effective one, which is false for every setuid process there is.
    if (!haveEffectiveUid)
      record.EffectiveUserId = -1;

    if (this._options.ReadSecurityContext)
      this.ReadSecurityContext(cache, ref record);
    else
      record.SecurityContextReason = UnknownReason.NotSampledYet;

    if (!this._options.UseProportionalSetSize) {
      // Nobody asked for it, which is not the same as the machine having none.
      record.ProportionalBytes = Counter.NotSampledYet;
      record.ProportionalSwapBytes = Counter.NotSampledYet;
      record.UniqueBytes = Counter.NotSampledYet;
    } else if (this.MayRead(record))
      this.ReadProportionalSetSize(cache, ref record);
    else {
      // smaps_rollup is 0400 for anybody else's process, so another user's proportional set is not
      // a failure to report — it is the answer without the elevated helper (PRD §8.3).
      record.ProportionalBytes = Counter.NotPermitted;
      record.ProportionalSwapBytes = Counter.NotPermitted;
      record.UniqueBytes = Counter.NotPermitted;
    }
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

  /// <summary>
  /// Proportional set size, from <c>smaps_rollup</c>.
  /// </summary>
  /// <remarks>
  /// Its own field rather than an improvement to the working-set one. It used to overwrite
  /// <see cref="ProcessRecord.PrivateWorkingSetBytes"/>, which meant a column headed "Private WS"
  /// showed the anonymous resident set on one machine and a share of every mapping on another — two
  /// different questions under one label, and no way for a reader to tell which they were looking at
  /// (PRD §5.1).
  /// </remarks>
  private void ReadProportionalSetSize(ProcessCache cache, ref ProcessRecord record) {
    if (!this._reader.TryRead(cache.SmapsRollupPath, out var content, out var errno)) {
      if (errno is Native.EACCES or Native.EPERM) {
        record.ProportionalBytes = Counter.NotPermitted;
        record.ProportionalSwapBytes = Counter.NotPermitted;
        record.UniqueBytes = Counter.NotPermitted;
      }

      return;
    }

    var privateClean = 0ul;
    var privateDirty = 0ul;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      // Pss_Anon, Pss_File and Pss_Shmem all begin with "Pss" and would each match a prefix test on
      // three characters; the colon is what makes "Pss:" mean the total and only the total.
      if (TryValue(line, "Pss:"u8, out var value))
        record.ProportionalBytes = Counter.Of(value * 1024);
      else if (TryValue(line, "SwapPss:"u8, out value))
        record.ProportionalSwapBytes = Counter.Of(value * 1024);
      // Private clean plus private dirty is the unique set: the memory only this process maps, and
      // so the only memory that would come back if it exited. Free once the file is open.
      else if (TryValue(line, "Private_Clean:"u8, out value))
        privateClean = value * 1024;
      else if (TryValue(line, "Private_Dirty:"u8, out value))
        privateDirty = value * 1024;
    }

    record.UniqueBytes = Counter.Of(privateClean + privateDirty);
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

  /// <summary>
  /// How often the process's cgroup has been stopped for using up its CPU quota (PRD §15).
  /// </summary>
  /// <remarks>
  /// Cached per cgroup for the length of one sample, which is what makes the column affordable: a
  /// machine with six hundred processes has a few dozen groups, and the counter belongs to the
  /// group rather than to any one of the processes in it. Nobody asking is
  /// <see cref="UnknownReason.NotSampledYet"/> and never a nought — "we did not look" and "this has
  /// never been throttled" are the two answers this column exists to keep apart.
  /// </remarks>
  private void ReadCpuThrottling(ref ProcessRecord record) {
    if (!this._options.ReadCpuThrottling) {
      record.ThrottledPeriods = Counter.NotSampledYet;
      return;
    }

    if (record.ContainerPath is not { Length: > 0 } path) {
      // A process outside any group has no such counter; a run with the cgroup read switched off
      // has one nobody looked for. Different answers, and they are reported differently.
      record.ThrottledPeriods = this._options.ReadCgroups ? Counter.NotSupported : Counter.NotSampledYet;
      return;
    }

    if (this._throttling.TryGetValue(path, out var known)) {
      record.ThrottledPeriods = known;
      return;
    }

    var counter = this.ReadThrottleCounter(path);
    this._throttling[path] = counter;
    record.ThrottledPeriods = counter;
  }

  private Counter ReadThrottleCounter(string cgroupPath) {
    // The path from /proc/[pid]/cgroup begins with a slash and is relative to the mount point, so
    // it is trimmed rather than joined — joining it would read from the file-system root instead.
    var file = $"{this._options.CgroupRoot}/{cgroupPath.TrimStart('/')}/cpu.stat";
    if (!this._reader.TryRead(file, out var content, out var errno))
      return errno is Native.EACCES or Native.EPERM ? Counter.NotPermitted : Counter.NotSupported;

    // Widened rather than decoded: cpu.stat is ASCII by construction and a dozen short lines long,
    // and the parser it goes to lives in Core so that it is exercised on every leg (PRD §9.2).
    Span<char> text = content.Length <= 1024 ? stackalloc char[content.Length] : new char[content.Length];
    for (var i = 0; i < content.Length; ++i)
      text[i] = (char)content[i];

    return CgroupCpuStatParser.Throttled(text);
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

    foreach (var key in this._stale) {
      this._cache.Remove(key);
      this._gpu?.Forget(key);
    }
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

  /// <summary>
  /// The context-switch counts and the affinity of one thread, from its own <c>status</c>.
  /// </summary>
  /// <remarks>
  /// Which failure it was matters more than that there was one: a status we were refused is
  /// <see cref="UnknownReason.NotPermitted"/> and the elevated helper could answer it, while a
  /// thread that exited between the directory listing and this read is gone and always will be. The
  /// old reading called every failure a permission problem, which sent the reader looking for a
  /// privilege they already had (PRD §72.3).
  /// </remarks>
  private ThreadStatus ReadThreadStatus(string taskDirectory) {
    if (!this._reader.TryRead(taskDirectory + "/status", out var content, out var errno))
      return ThreadStatus.Unreadable(errno switch {
        Native.EACCES or Native.EPERM => UnknownReason.NotPermitted,
        Native.ENOENT or Native.ESRCH => UnknownReason.ProcessExited,
        _ => UnknownReason.NotSupportedOnPlatform,
      });

    // Widened rather than decoded: status is ASCII by construction — the only field that could carry
    // anything else is the name, which this parser does not read — and one array beats the string per
    // line a decode-then-split would make. The parser itself is in Core so it is tested everywhere.
    Span<char> text = content.Length <= 4096 ? stackalloc char[content.Length] : new char[content.Length];
    for (var i = 0; i < content.Length; ++i)
      text[i] = (char)content[i];

    return ThreadStatusParser.Parse(text);
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

      // Per-thread switch counts and affinity live in the thread's own status, which is one more
      // file each. Threads are enumerated for one process on demand, so it is affordable here in a
      // way it would not be in the sample loop (PRD §5.4).
      var status = this.ReadThreadStatus(directory);

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
        ContextSwitches: status.TotalContextSwitches,
        LastCpu: record.LastCpu,
        WaitReason: this.ReadWaitChannel(directory),
        VoluntaryContextSwitches: status.VoluntaryContextSwitches,
        InvoluntaryContextSwitches: status.InvoluntaryContextSwitches,
        // Nice, which is the priority the thread was *given*: a thread reniced to 19 still shows an
        // effective priority that moves with the load, and the two together are what say whether a
        // busy thread is being polite or was simply never asked to be.
        BasePriority: record.Nice,
        Policy: record.SchedulingPolicy,
        Affinity: status.Affinity
      ));
    }

    return result;
  }

  /// <summary>
  /// The images mapped into a process (PRD §31).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>smaps</c> first, <c>maps</c> second. They carry the same header lines, and <c>smaps</c> adds
  /// the per-mapping counter block that says how much of an image is actually resident — the one
  /// number that distinguishes a library the process is running from one it merely linked against.
  /// It costs a walk of the page table, which is why it is asked for here and not in the sample loop
  /// (PRD §5.4), and why the cheaper file is still tried when it is refused.
  /// </para>
  /// <para>
  /// The parse is in <see cref="MapsParser"/> so the Windows and macOS CI legs run it too (PRD §9.2);
  /// only the two reads and the on-disk enrichment are here.
  /// </para>
  /// </remarks>
  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) {
    List<ModuleRecord> modules;
    if (this._reader.TryReadWhole($"{this._procRoot}/{key.Pid}/smaps", out var content, out _))
      // A header line with no counter block behind it does not happen on a kernel that produced the
      // header at all, so this reason is a statement about a kernel we have never met rather than
      // about this one.
      modules = MapsParser.Collect(content, Counter.NotSupported);
    else if (this._reader.TryReadWhole($"{this._procRoot}/{key.Pid}/maps", out content, out var errno))
      modules = MapsParser.Collect(
        content,
        errno is Native.EACCES or Native.EPERM ? Counter.NotPermitted : Counter.NotSupported
      );
    else
      return [];

    // Whether the file behind each mapping can be read is a separate question from whether the
    // process could be: /proc said "libfoo.so", and the answer to "how big is it, and what does its
    // header say" is on the file system.
    for (var i = 0; i < modules.Count; ++i)
      modules[i] = this._images.Describe(modules[i]);

    return modules;
  }

  /// <summary>
  /// Every descriptor a process holds, with what <c>fdinfo</c> says about each (PRD §32).
  /// </summary>
  /// <remarks>
  /// Two reads per descriptor — the symlink for what it points at, <c>fdinfo</c> for the position,
  /// the flags and the inode. The inode is what makes the list useful beyond a name: it is the join
  /// key from a socket descriptor to a row of <c>/proc/net/tcp</c>, which is the difference between
  /// "holds a socket" and "holds this connection" (PRD §40).
  /// </remarks>
  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) {
    var result = new List<HandleRecord>();
    var fdRoot = $"{this._procRoot}/{key.Pid}/fd";
    string[] entries;
    try {
      // Every entry, not GetFiles: a descriptor on a directory is a symlink to a directory, and the
      // managed enumerator classifies those as directories. Listing only the files silently dropped
      // every open directory a process held, which is most of what a file manager or an indexer has.
      entries = Directory.GetFileSystemEntries(fdRoot);
    } catch (UnauthorizedAccessException) {
      return this.HandlesThroughHelper(key, result);
    } catch (IOException) {
      return result;
    }

    foreach (var entry in entries) {
      var name = Path.GetFileName(entry);
      if (!int.TryParse(name, out var fd))
        continue;

      var target = this._reader.TryReadLink(entry);
      var info = this._reader.TryRead($"{this._procRoot}/{key.Pid}/fdinfo/{name}", out var content, out var errno)
        ? DescriptorParser.ParseFdInfo(content)
        : errno is Native.EACCES or Native.EPERM
          ? DescriptorParser.Refused
          : DescriptorParser.Unread;

      result.Add(Build((ulong)fd, target, info));
    }

    return result;
  }

  /// <summary>
  /// One descriptor, from its target and its <c>fdinfo</c>.
  /// </summary>
  /// <remarks>
  /// The inode is taken from <c>fdinfo</c> when it is there and from the <c>socket:[n]</c> name when
  /// it is not: the <c>ino:</c> line is recent, the bracketed number has been in the link target for
  /// as long as <c>/proc</c> has had one, and the socket join must not stop working on an older
  /// kernel.
  /// </remarks>
  private static HandleRecord Build(ulong fd, string? target, DescriptorParser.DescriptorInfo info) {
    var inode = info.Inode;
    if (!inode.HasValue && DescriptorParser.TryParsePseudoInode(target, out var fromName))
      inode = Counter.Of(fromName);

    var kind = DescriptorParser.Classify(target, info.OpenFlags);
    // O_DIRECTORY is not compulsory — open(2) on a directory succeeds without it — so a descriptor
    // that the flags did not settle is asked of the file system, which is what the previous version
    // did for every descriptor.
    if (kind == HandleKind.File && target is not null && Directory.Exists(target))
      kind = HandleKind.Directory;

    return new(
      fd,
      kind,
      target,
      DescriptorParser.DescribeAccess(info.OpenFlags),
      info.Position,
      info.OpenFlags,
      inode,
      info.TargetPid
    );
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
      // The helper answers with the name and nothing else. Everything fdinfo would have added is
      // therefore missing because the protocol does not carry it yet — a fact about this program, not
      // about the kernel, and the two must not render the same (PRD §7).
      result.Add(Build((ulong)fd, target.Length == 0 ? null : target, DescriptorParser.NotRelayed));
    }

    return result;
  }

  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) {
    // The socket inodes this process holds, joined against the five network tables. The join runs
    // once per request rather than once per process, which is what keeps it off the sampling path
    // (PRD §5.1).
    var inodes = new HashSet<ulong>();
    // The descriptor's own inode, which fdinfo reports directly and the socket:[n] name still
    // carries on a kernel too old to write it (PRD §32).
    foreach (var handle in this.GetHandles(key)) {
      if (handle.Kind != HandleKind.Socket)
        continue;

      if (handle.Inode.TryGetValue(out var inode))
        inodes.Add(inode);
    }

    var result = new List<ConnectionRecord>();
    if (inodes.Count == 0)
      return result;

    this.CollectSockets(inodes, result);
    for (var i = 0; i < result.Count; ++i)
      // Every row came back because this process holds a descriptor on it, so the owner is known
      // without the machine-wide scan the unfiltered listing has to do.
      result[i] = result[i] with {
        Pid = key.Pid,
        UserName = this._users.Resolve(result[i].UserId),
      };

    return result;
  }

  /// <summary>
  /// Every socket on the machine, each attributed to a process where a descriptor names it.
  /// </summary>
  /// <remarks>
  /// The attribution is the expensive half: the tables are five files, and finding out who holds
  /// each socket means reading every process's descriptors, which is the 85 µs-per-process read
  /// that is kept off the sampling path (PRD §5.4). Sockets belonging to another user's processes
  /// stay unattributed rather than being left out, because "port 22 is listening and I may not see
  /// whose it is" is a different statement from "nothing is listening on port 22".
  /// </remarks>
  public IReadOnlyList<ConnectionRecord> GetConnections() {
    var owners = this.SocketOwners();
    var result = new List<ConnectionRecord>();
    this.CollectSockets(null, result);
    for (var i = 0; i < result.Count; ++i)
      result[i] = result[i] with {
        Pid = owners.TryGetValue(result[i].Inode, out var pid) ? pid : 0,
        UserName = this._users.Resolve(result[i].UserId),
      };

    return result;
  }

  /// <summary>
  /// Which process holds each socket, by walking every readable <c>/proc/[pid]/fd</c>.
  /// </summary>
  /// <remarks>
  /// One entry per socket rather than a list: a socket inherited across a <c>fork</c> is held by
  /// several processes at once and this reports the first one found, which is the same compromise
  /// <c>ss -p</c> makes when a listener has been passed to a pool of workers.
  /// <para>
  /// Deliberately not routed through <see cref="GetHandles"/>: that falls back to the privileged
  /// helper for every process it may not read, which on a shared machine is several hundred
  /// round-trips to answer a question the caller asked once.
  /// </para>
  /// </remarks>
  private Dictionary<ulong, int> SocketOwners() {
    var owners = new Dictionary<ulong, int>();
    string[] processes;
    try {
      processes = Directory.GetDirectories(this._procRoot);
    } catch (IOException) {
      return owners;
    } catch (UnauthorizedAccessException) {
      return owners;
    }

    foreach (var process in processes) {
      if (!int.TryParse(Path.GetFileName(process), out var pid))
        continue;

      string[] descriptors;
      try {
        descriptors = Directory.GetFiles(process + "/fd");
      } catch (IOException) {
        continue;                                          // it exited, or it is not a process at all
      } catch (UnauthorizedAccessException) {
        continue;                                          // somebody else's, and no helper for this
      }

      foreach (var descriptor in descriptors)
        if (this._reader.TryReadLink(descriptor) is { } target
          && ProcNetParser.TryParseSocketInode(target, out var inode))
          owners.TryAdd(inode, pid);
    }

    return owners;
  }

  /// <summary>
  /// Reads the five tables into <paramref name="result"/>, keeping only <paramref name="inodes"/>
  /// when one is given.
  /// </summary>
  private void CollectSockets(HashSet<ulong>? inodes, List<ConnectionRecord> result) {
    var interfaces = this.ReadInterfaces();
    this.CollectInet("/net/tcp", ConnectionProtocol.Tcp, interfaces, inodes, result);
    this.CollectInet("/net/tcp6", ConnectionProtocol.Tcp6, interfaces, inodes, result);
    this.CollectInet("/net/udp", ConnectionProtocol.Udp, interfaces, inodes, result);
    this.CollectInet("/net/udp6", ConnectionProtocol.Udp6, interfaces, inodes, result);
    if (this.ReadText("/net/unix") is { } unix)
      ProcNetParser.ParseUnix(unix, inodes, result);
  }

  private void CollectInet(
    string relativePath,
    ConnectionProtocol protocol,
    NetworkInterfaceMap interfaces,
    HashSet<ulong>? inodes,
    List<ConnectionRecord> result
  ) {
    if (this.ReadText(relativePath) is { } content)
      ProcNetParser.ParseInet(content, protocol, interfaces, inodes, result);
  }

  /// <summary>
  /// The address-to-interface map, read fresh for each request.
  /// </summary>
  /// <remarks>
  /// Not cached: an interface comes up, a lease is renewed, a VPN connects, and a cached map would
  /// then name the wrong card for the rest of the program's life. Two small files against a request
  /// somebody made by opening a tab is a trade worth making.
  /// </remarks>
  private NetworkInterfaceMap ReadInterfaces() {
    // Both files are turned into strings before either is parsed: the reader hands back a view of
    // one shared buffer, so reading the second invalidates the first.
    var routes = this.ReadText("/net/route");
    var addresses = this.ReadText("/net/if_inet6");
    return routes is null && addresses is null
      ? NetworkInterfaceMap.Empty
      : NetworkInterfaceMap.Parse(routes ?? string.Empty, addresses ?? string.Empty);
  }

  /// <summary>
  /// One of the machine-wide <c>/proc/net</c> files as text, or null when it is not there.
  /// </summary>
  /// <remarks>
  /// UTF-8 rather than ASCII, because a Unix socket's path is a filename and a filename may be
  /// anything the filesystem allows.
  /// <para>
  /// Read whole rather than to the first short read: these are the <c>/proc</c> files that run to
  /// hundreds of kilobytes, and the usual reader would stop at the first page and report a machine
  /// with fifty Unix sockets on it rather than seventeen hundred.
  /// </para>
  /// </remarks>
  private string? ReadText(string relativePath)
    => this._reader.TryReadWhole(this._procRoot + relativePath, out var content, out _)
      ? Encoding.UTF8.GetString(content)
      : null;

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
