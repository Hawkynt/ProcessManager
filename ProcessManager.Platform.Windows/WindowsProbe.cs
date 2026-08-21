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
  private readonly WindowsImageReader _images = new();
  private readonly WindowsProbeOptions _options;
  private readonly Dictionary<int, ObjectTally> _objects = [];
  private readonly HashSet<string> _liveImages = new(StringComparer.OrdinalIgnoreCase);
  private ObjectTypeIndices _objectTypes = ObjectTypeIndices.None;
  private readonly HashSet<ushort> _askedTypes = [];
  private int _bufferLength;

  public WindowsProbe() : this(new()) { }

  public WindowsProbe(WindowsProbeOptions options)
    => this._options = options ?? throw new ArgumentNullException(nameof(options));

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

    // The commit charge again, under the name the rest of the program uses for it.
    system.CommittedBytes = Counter.Of((ulong)info.CommitTotal * pageSize);
    system.CommitLimitBytes = Counter.Of((ulong)info.CommitLimit * pageSize);

    // The two pools, which is the one place Windows has the finer figure and Linux has the
    // approximation rather than the other way round.
    system.ReclaimableKernelBytes = Counter.Of((ulong)info.KernelPaged * pageSize);
    system.UnreclaimableKernelBytes = Counter.Of((ulong)info.KernelNonpaged * pageSize);

    // Everything else on the memory page is reachable on Windows — the modified and standby lists
    // through SYSTEM_MEMORY_LIST_INFORMATION, the rest through the performance counters — and is
    // not read yet. Said in as many words rather than left to the snapshot's "nobody has sampled
    // this second", which would send a reader off to wait for a figure that is never coming
    // (PRD §7, §45.6).
    var missing = Counter.Unknown(UnknownReason.NotImplementedHere);
    system.FreeMemoryBytes = missing;
    system.BufferMemoryBytes = missing;
    system.ModifiedMemoryBytes = missing;
    system.DirtyBytes = missing;
    system.WritebackBytes = missing;
    system.AnonymousBytes = missing;
    system.MappedBytes = missing;
    system.SwapCachedBytes = missing;
    system.CompressedBytes = missing;
    system.CompressedOriginalBytes = missing;
    system.SlabBytes = missing;
    system.UnevictableBytes = missing;
    system.LockedBytes = missing;
    system.VmallocUsedBytes = missing;
    system.PerCpuBytes = missing;
    system.HardwareCorruptedBytes = missing;
    system.HugePageSizeBytes = missing;
    system.HugePagesTotal = missing;
    system.HugePagesFree = missing;
    system.HugePagesReserved = missing;
    system.HugeTlbBytes = missing;
    system.AnonymousHugePagesBytes = missing;
    system.SharedHugePagesBytes = missing;
    system.FileHugePagesBytes = missing;
    system.ActiveAnonymousBytes = missing;
    system.InactiveAnonymousBytes = missing;
    system.ActiveFileBytes = missing;
    system.InactiveFileBytes = missing;
    system.PageTableBytes = missing;
    system.KernelStackBytes = missing;
    system.SharedMemoryBytes = missing;
    // The three §46 rate counters and the machine's handle total. Every one of them is in
    // SYSTEM_PERFORMANCE_INFORMATION, which this probe does not query yet — so they say that rather
    // than sending a reader off to wait for the next sample (PRD §45.6).
    system.SoftInterrupts = missing;
    system.OpenDescriptors = missing;
    system.DescriptorLimit = missing;
  }

  /// <summary>
  /// Fills in the two things the bulk query does not carry: who owns each process, and what it was
  /// started with. Both are constant for a process's lifetime, so both are cached and only new
  /// processes cost anything (PRD §5.2).
  /// </summary>
  private void ResolveOwnersAndCommandLines(SystemSnapshot snapshot) {
    this._livePids.Clear();
    this._liveImages.Clear();
    this.CollectObjectCounts();

    var processes = snapshot.ProcessBuffer;
    for (var i = 0; i < processes.Length; ++i) {
      ref var record = ref processes[i];
      this._livePids.Add(record.Pid);

      // One open answers seven questions, and the answers are cached for the life of the process:
      // none of the owner, the elevation, the integrity level, the protection level, the sandbox
      // flag, the translated machine or the image path changes while it runs.
      var identity = this._identities.Resolve(
        record.Pid,
        record.Key.StartTicks,
        this._options.ReadMitigations,
        out var mitigations
      );

      record.UserName = identity.UserName;
      record.UserId = identity.UserId;
      record.IsElevated = identity.Elevated;
      record.IntegrityLevel = identity.Integrity;
      record.ProtectionLevel = identity.ProtectionLevel;
      record.IsAppContainer = identity.IsAppContainer;
      record.Emulation = identity.Emulation;
      record.ImagePath = identity.ImagePath;
      // Windows has no notion of a real-versus-effective uid; the token is the whole answer.
      record.EffectiveUserId = identity.UserId;

      record.DepPolicy = mitigations.Dep;
      record.AslrPolicy = mitigations.Aslr;
      record.ControlFlowGuardPolicy = mitigations.ControlFlowGuard;
      record.ShadowStackPolicy = mitigations.ShadowStack;
      record.DynamicCodePolicy = mitigations.DynamicCode;
      record.BinarySignaturePolicy = mitigations.BinarySignature;

      this.ApplyObjectCounts(ref record);
      this.ApplyImageFacts(ref record);

      if (this._commandLines.TryGetValue(record.Key, out var commandLine)) {
        record.CommandLine = commandLine;
        continue;
      }

      commandLine = ReadCommandLine(record.Pid);
      this._commandLines[record.Key] = commandLine;
      record.CommandLine = commandLine;
    }

    this._identities.Prune(this._livePids);
    this._images.Prune(this._liveImages);
    if (this._commandLines.Count > 4096)
      foreach (var key in this._commandLines.Keys.Where(key => !this._livePids.Contains(key.Pid)).ToList())
        this._commandLines.Remove(key);
  }

  /// <summary>
  /// What the process's own image says about itself, and its subsystem (PRD §14).
  /// </summary>
  /// <remarks>
  /// Only when asked for. A run that has not asked leaves both readings at "nobody has looked"
  /// rather than at a blank, so that a reader is not sent off waiting for a version that is never
  /// coming (PRD §45.6, §72.3).
  /// </remarks>
  private void ApplyImageFacts(ref ProcessRecord record) {
    if (record.ImagePath is { Length: > 0 } path)
      this._liveImages.Add(path);

    if (!this._options.ReadImageVersions) {
      record.ImageVersionReason = UnknownReason.NotSampledYet;
      record.Subsystem = Counter.NotSampledYet;
      return;
    }

    var facts = this._images.Read(record.ImagePath, out var reason);
    WindowsImageReader.Apply(ref record, facts, reason);
  }

  #region object counts (PRD §20)

  /// <summary>
  /// Tallies the machine's whole handle table once, for every process at once.
  /// </summary>
  /// <remarks>
  /// Once per sample and not once per process: there is no per-process handle query on Windows, so a
  /// per-process implementation would read the same machine-wide table for every row. The type
  /// indices are discovered on the first pass and kept — they are fixed for as long as the kernel is
  /// running, being the order in which its object types were created at boot, and they are not
  /// constants anybody may hard-code (PRD §5.3).
  /// </remarks>
  private void CollectObjectCounts() {
    this._objects.Clear();
    if (!this._options.ReadObjectCounts)
      return;

    var size = 256 * 1024;
    for (var attempt = 0; attempt < 8; ++attempt) {
      var memory = Marshal.AllocHGlobal(size);
      try {
        var status = Native.NtQuerySystemInformationRaw(
          Native.SystemExtendedHandleInformationClass,
          memory,
          (uint)size,
          out var needed
        );

        if (status == NtStructures.STATUS_INFO_LENGTH_MISMATCH) {
          size = (int)Math.Max(needed + (64 * 1024), (uint)size * 2);
          continue;
        }

        if (status != NtStructures.STATUS_SUCCESS)
          return;

        unsafe {
          var table = new ReadOnlySpan<byte>((void*)memory, size);
          this.NameObjectTypes(table);
          WindowsObjectTally.Tally(table, in this._objectTypes, this._objects);
        }

        return;
      } finally {
        Marshal.FreeHGlobal(memory);
      }
    }
  }

  /// <summary>
  /// Works out which type index means which object type, by asking one handle of each.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A few dozen duplications rather than one per handle: the table has a million rows and a few
  /// dozen distinct type indices in it, so one sample of each index is the whole of what is needed.
  /// A handle whose owner will not open for duplication simply leaves that index unnamed, which
  /// means the count for that type is missing rather than wrong.
  /// </para>
  /// <para>
  /// Repeated across samples until all five are known, and this is not belt and braces. A type is
  /// only discoverable while some process on the machine holds a handle of it, so a sample taken
  /// when nothing happens to hold a semaphore teaches nothing about semaphores — and doing the pass
  /// once would leave that column permanently unknown for the life of the program. Which is exactly
  /// what happened when this was written: two runs a minute apart, one of which learnt the semaphore
  /// index and one of which did not.
  /// </para>
  /// <para>
  /// It converges rather than repeating work: an index is asked about once ever, whether or not it
  /// turned out to be one of the five, and the pass stops entirely once all five are known.
  /// </para>
  /// </remarks>
  private void NameObjectTypes(ReadOnlySpan<byte> table) {
    if (this._objectTypes.IsComplete)
      return;

    var self = Native.GetCurrentProcess();
    var pid = Environment.ProcessId;
    foreach (var (index, owningPid, handle) in WindowsObjectTally.SampleTypes(table, pid)) {
      // Asked about once ever, whether or not it turned out to be one of the five. An index is only
      // recorded as asked when something actually answered, so a type whose every candidate refused
      // is tried again from a later sample rather than written off.
      if (this._askedTypes.Contains(index))
        continue;

      var owner = owningPid == pid
        ? self
        : Native.OpenProcess(Native.PROCESS_DUP_HANDLE, false, owningPid);

      if (owner == 0)
        continue;

      try {
        if (!Native.DuplicateHandle(owner, handle, self, out var copy, 0, false, Native.DUPLICATE_SAME_ACCESS))
          continue;

        try {
          var name = HandleNameResolver.QueryType(copy);
          if (name is null)
            continue;

          this._askedTypes.Add(index);
          this._objectTypes = this._objectTypes.With(name, index);
        } finally {
          Native.CloseHandle(copy);
        }
      } finally {
        // Never the pseudo-handle for this process, which is not a real handle and must not be closed.
        if (owner != self)
          Native.CloseHandle(owner);
      }
    }
  }

  /// <summary>
  /// The five tallies and the two desktop quotas, or why there are none.
  /// </summary>
  /// <remarks>
  /// A process the table mentioned nowhere holds none of these objects, which is a measurement and
  /// reads as nought. A run that did not ask has not measured anything, which is a different cell
  /// (PRD §72.3).
  /// </remarks>
  private void ApplyObjectCounts(ref ProcessRecord record) {
    if (this._options.ReadObjectCounts) {
      this._objects.TryGetValue(record.Pid, out var tally);
      // An index that was never named cannot be counted, and reporting nought for it would say the
      // process holds no events when nobody ever knew which handles were events.
      record.EventObjectCount = Tallied(this._objectTypes.Event, tally.Events);
      record.SemaphoreObjectCount = Tallied(this._objectTypes.Semaphore, tally.Semaphores);
      record.MutexObjectCount = Tallied(this._objectTypes.Mutant, tally.Mutexes);
      record.SectionObjectCount = Tallied(this._objectTypes.Section, tally.Sections);
      record.RegistryKeyCount = Tallied(this._objectTypes.Key, tally.Keys);
    } else {
      record.EventObjectCount = Counter.NotSampledYet;
      record.SemaphoreObjectCount = Counter.NotSampledYet;
      record.MutexObjectCount = Counter.NotSampledYet;
      record.SectionObjectCount = Counter.NotSampledYet;
      record.RegistryKeyCount = Counter.NotSampledYet;
    }

    if (!this._options.ReadGuiObjectCounts) {
      record.UserObjectCount = Counter.NotSampledYet;
      record.GdiObjectCount = Counter.NotSampledYet;
      return;
    }

    var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, record.Pid);
    if (process == 0) {
      record.UserObjectCount = Counter.NotPermitted;
      record.GdiObjectCount = Counter.NotPermitted;
      return;
    }

    try {
      record.UserObjectCount = GuiResources(process, Native.GR_USEROBJECTS);
      record.GdiObjectCount = GuiResources(process, Native.GR_GDIOBJECTS);
    } finally {
      Native.CloseHandle(process);
    }
  }

  private static Counter Tallied(ushort index, uint count)
    => index == ObjectTypeIndices.Unknown ? Counter.Unknown(UnknownReason.NotPermitted) : Counter.Of(count);

  /// <summary>
  /// One of the two desktop quotas.
  /// </summary>
  /// <remarks>
  /// <c>GetGuiResources</c> returns nought both for a process holding no such objects and for a call
  /// that failed, and says so in its own documentation — so the last error is cleared first and
  /// asked afterwards. A console service really does hold no USER objects, and that is a
  /// measurement; a call that was refused is not, and the two must not be the same cell
  /// (PRD §72.3).
  /// </remarks>
  private static Counter GuiResources(nint process, uint flag) {
    Marshal.SetLastSystemError(0);
    var count = Native.GetGuiResources(process, flag);
    if (count != 0)
      return Counter.Of(count);

    // Nought and no error is a process holding no such objects, which is every console program on
    // the machine and is a measurement. Nought with an error is not an answer at all, and which
    // error it is decides whether a reader should try again with more privilege or stop asking.
    return Marshal.GetLastWin32Error() == 0 ? Counter.Of(0ul) : Native.WhyItFailed();
  }

  #endregion

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
  /// Not read yet: Windows services come from the service control manager
  /// (EnumServicesStatusEx), which is not written (PRD §41).
  /// </summary>
  public IReadOnlyList<ServiceRecord> GetServices() => [];

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
          Path: path.Length > 0 ? path : entry.ReadModule(),
          BaseAddress: (ulong)entry.ModuleBaseAddress,
          Size: entry.ModuleBaseSize,
          // Windows does not report per-module page protection here; the mapping's own protection is
          // per-region rather than per-module, so claiming one would be inventing it.
          Permissions: string.Empty,
          EndAddress: (ulong)entry.ModuleBaseAddress + entry.ModuleBaseSize,
          // Everything below is readable on Windows and is not read yet — the version resource, the
          // signature, the section-by-section working set. "Windows cannot do this" and "we have not
          // written it" are different sentences and must not render as the same cell (PRD §7).
          ResidentBytes: Counter.Unknown(UnknownReason.NotImplementedHere),
          // A Toolhelp entry is one image at one base, not a fold of several mappings, so there is no
          // per-mapping file offset to report and no count of mappings to fold.
          FileOffset: Counter.NotSupported,
          Inode: Counter.NotSupported,
          Device: null,
          IsDeleted: false,
          MappingCount: 1,
          FileSizeBytes: Counter.Unknown(UnknownReason.NotImplementedHere),
          FileModifiedUtcTicks: 0,
          Type: ModuleType.Unknown,
          Architecture: null,
          EntryPoint: Counter.Unknown(UnknownReason.NotImplementedHere),
          // A PE image has no SONAME and asks for no interpreter: the import table names what it
          // needs, and the loader is the kernel's.
          Soname: null,
          Interpreter: null,
          // A PE image declares its own hardening — ASLR, DEP, CFG and the rest live in the optional
          // header's DllCharacteristics — and none of it is read yet. None here therefore means "not
          // read", which is exactly what the flag word means everywhere else (PRD §7, §72.3).
          Mitigations: ImageMitigations.None,
          // Windows has an equivalent identity in the debug directory's PDB signature, which is also
          // not read yet.
          BuildId: null,
          // Toolhelp does not say why a module is loaded; the loader's own table does, and reading
          // it is a separate piece of work from listing the modules.
          LoadReason: ModuleLoadReason.Unknown,
          // Toolhelp does hand out a load count — two of them, the global and the per-process — in
          // the entry this row was built from. Reading them is the Windows half of §31 and is not
          // written yet, so this is nought: the pass that fills it in has not run (PRD §7, §72.3).
          LoadCount: 0,
          // And the runtime a module belongs to is in the same header the mitigations are in, which
          // is also not read yet.
          Runtime: ModuleRuntime.Unknown
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
        result.Add(new(
          Handle: (ulong)entry.HandleValue,
          Kind: ClassifyType(type),
          Name: name,
          // The granted access mask is in the table entry we are already looking at; decoding it into
          // the per-object-type rights of §32 is not written yet, and an empty cell would say Windows
          // does not have them (PRD §7).
          Access: null,
          // A Windows handle has no file position and no open flags: the position belongs to the file
          // object, and the equivalent of the flags is the access mask above.
          Position: Counter.NotSupported,
          OpenFlags: Counter.NotSupported,
          Inode: Counter.NotSupported,
          TargetPid: Counter.Unknown(UnknownReason.NotImplementedHere),
          // The Unix mount identity has no Windows counterpart: a handle's volume is in the object
          // name this already resolves, and there is no per-descriptor mount to join against.
          MountId: Counter.NotSupported,
          Device: null,
          FileSystem: null,
          // Windows keeps its per-type detail in NtQueryObject's information classes rather than in a
          // text file, and none of it is read yet.
          Detail: null,
          // GetFileType answers §32's file type for a Windows handle — disk, character, pipe — and
          // is not called yet. Unknown is therefore "we have not asked", which is what it means
          // everywhere else, and not a claim that the handle has no type (PRD §7, §72.3).
          NodeType: FileNodeType.Unknown,
          NodeDevice: null
        ));
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
  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => Connections(key.Pid);

  /// <summary>
  /// Every socket on the machine (PRD §40).
  /// </summary>
  /// <remarks>
  /// The same four tables with the owner filter taken off, because they always did arrive whole —
  /// the per-process call reads the machine's table and throws most of it away.
  /// </remarks>
  public IReadOnlyList<ConnectionRecord> GetConnections() => Connections(null);

  private static List<ConnectionRecord> Connections(int? pid) {
    var result = new List<ConnectionRecord>();
    ReadTcp(pid, Native.AF_INET, ConnectionProtocol.Tcp, result);
    ReadTcp(pid, Native.AF_INET6, ConnectionProtocol.Tcp6, result);
    ReadUdp(pid, Native.AF_INET, ConnectionProtocol.Udp, result);
    ReadUdp(pid, Native.AF_INET6, ConnectionProtocol.Udp6, result);
    return result;
  }

  /// <summary>
  /// What the owner table does not carry.
  /// </summary>
  /// <remarks>
  /// Not <see cref="Counter.NotSupported"/>: Windows can answer all three through
  /// <c>GetPerTcpConnectionEStats</c>, and saying "this platform has no such counter" would tell the
  /// reader the machine cannot do something it can. This is a gap in this program (PRD §7).
  /// </remarks>
  private static readonly Counter _NotYetOnWindows = Counter.Unknown(UnknownReason.NotImplementedHere);

  /// <summary>
  /// The same gap, for the per-socket counters Linux reads through the socket diagnostics.
  /// </summary>
  /// <remarks>
  /// <c>GetPerTcpConnectionEStats</c> is the Windows equivalent and reaches all of them — bytes,
  /// segments, round-trip time, retransmissions — once it is enabled per connection. Until it is
  /// called, these say so rather than claiming Windows has nothing to offer (PRD §7).
  /// </remarks>
  private static readonly SocketStatistics _StatisticsNotYetOnWindows
    = SocketStatistics.Unknown(UnknownReason.NotImplementedHere);

  private static readonly Rate _RateNotYetOnWindows = Rate.Unknown(UnknownReason.NotImplementedHere);

  private static void ReadTcp(int? pid, uint family, ConnectionProtocol protocol, List<ConnectionRecord> result) {
    // MIB_TCPROW_OWNER_PID for IPv4 is state, local addr, local port, remote addr, remote port,
    // owning pid — six 32-bit fields. The IPv6 row carries 16-byte addresses and scope ids instead.
    var rowSize = family == Native.AF_INET ? 24 : 56;
    Walk(
      (nint table, ref uint size) => Native.GetExtendedTcpTable(table, ref size, false, family, Native.TCP_TABLE_OWNER_PID_ALL, 0),
      rowSize,
      (row, entry) => {
        if (family == Native.AF_INET) {
          var owner = Marshal.ReadInt32(entry, 20);
          if (pid is { } wanted && owner != wanted)
            return;

          result.Add(new(
            protocol,
            SocketKind.Stream,
            FormatIPv4((uint)Marshal.ReadInt32(entry, 4)),
            NetworkPort(Marshal.ReadInt32(entry, 8)),
            FormatIPv4((uint)Marshal.ReadInt32(entry, 12)),
            NetworkPort(Marshal.ReadInt32(entry, 16)),
            TcpStateName(Marshal.ReadInt32(entry, 0)),
            0,
            owner,
            -1,
            null,
            null,
            _NotYetOnWindows,
            _NotYetOnWindows,
            _NotYetOnWindows,
            _StatisticsNotYetOnWindows,
            _RateNotYetOnWindows,
            _RateNotYetOnWindows,
            // The owner table names a process and stops there. Which service that process belongs
            // to is a second question, and the answer is not read yet (PRD §7).
            null,
            null,
            // A Windows socket has a kernel reference count and no published table prints it, which
            // is a different statement from "not read yet" — but the tables above are read through
            // an API that never offered it, so it is ours to add and not Windows' to refuse.
            _NotYetOnWindows
          ));
        } else {
          var owner = Marshal.ReadInt32(entry, 52);
          if (pid is { } wanted && owner != wanted)
            return;

          result.Add(new(
            protocol,
            SocketKind.Stream,
            FormatIPv6(entry, 0),
            NetworkPort(Marshal.ReadInt32(entry, 20)),
            FormatIPv6(entry, 24),
            NetworkPort(Marshal.ReadInt32(entry, 44)),
            TcpStateName(Marshal.ReadInt32(entry, 48)),
            0,
            owner,
            -1,
            null,
            null,
            _NotYetOnWindows,
            _NotYetOnWindows,
            _NotYetOnWindows,
            _StatisticsNotYetOnWindows,
            _RateNotYetOnWindows,
            _RateNotYetOnWindows,
            // The owner table names a process and stops there. Which service that process belongs
            // to is a second question, and the answer is not read yet (PRD §7).
            null,
            null,
            // A Windows socket has a kernel reference count and no published table prints it, which
            // is a different statement from "not read yet" — but the tables above are read through
            // an API that never offered it, so it is ours to add and not Windows' to refuse.
            _NotYetOnWindows
          ));
        }
      }
    );
  }

  private static void ReadUdp(int? pid, uint family, ConnectionProtocol protocol, List<ConnectionRecord> result) {
    var rowSize = family == Native.AF_INET ? 12 : 28;
    Walk(
      (nint table, ref uint size) => Native.GetExtendedUdpTable(table, ref size, false, family, Native.UDP_TABLE_OWNER_PID, 0),
      rowSize,
      (row, entry) => {
        var ownerOffset = family == Native.AF_INET ? 8 : 24;
        var owner = Marshal.ReadInt32(entry, ownerOffset);
        if (pid is { } wanted && owner != wanted)
          return;

        result.Add(new(
          protocol,
          SocketKind.Datagram,
          family == Native.AF_INET ? FormatIPv4((uint)Marshal.ReadInt32(entry, 0)) : FormatIPv6(entry, 0),
          NetworkPort(Marshal.ReadInt32(entry, family == Native.AF_INET ? 4 : 20)),
          "*",
          0,
          "LISTEN",
          0,
          owner,
          -1,
          null,
          null,
          _NotYetOnWindows,
          _NotYetOnWindows,
          _NotYetOnWindows,
          _StatisticsNotYetOnWindows,
          _RateNotYetOnWindows,
          _RateNotYetOnWindows,
          null,
          null,
          _NotYetOnWindows
        ));
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
