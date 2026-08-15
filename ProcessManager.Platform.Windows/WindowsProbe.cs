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
  private int _bufferLength;

  public string Description => "windows:ntquerysysteminformation";

  public void Dispose() { }

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

      record.UserName = this._identities.Resolve(record.Pid, record.Key.StartTicks, out var userId);
      record.UserId = userId;

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
  public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key)
    => this._bufferLength == 0
      ? []
      : SystemProcessInformationReader.ReadThreads(this._buffer.AsSpan(0, this._bufferLength), key);

  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];

  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];

  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];

  public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];

}
