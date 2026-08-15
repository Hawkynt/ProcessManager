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
/// The parsing half is deliberately separable from the calling half, so that a captured buffer can be
/// replayed through it on the Linux CI leg (PRD §9.4).
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
  private readonly List<int> _seen = [];

  public string Description => "windows:ntquerysysteminformation";

  public void Dispose() { }

  public void Sample(SystemSnapshot snapshot) {
    ArgumentNullException.ThrowIfNull(snapshot);

    ReadProcessorTimes(snapshot);
    if (!this.QueryProcesses(out var length))
      return;

    ParseProcesses(this._buffer.AsSpan(0, length), this.BufferAddress, snapshot);
    ReadMemory(ref snapshot.System);
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

  /// <summary>
  /// Walks the process chain into <paramref name="snapshot"/>.
  /// </summary>
  /// <param name="buffer">The bytes the query produced.</param>
  /// <param name="bufferBaseAddress">
  /// The address <paramref name="buffer"/> lived at when the kernel filled it. Every image name is a
  /// <c>UNICODE_STRING</c> whose <c>Buffer</c> is an <em>absolute</em> pointer back into this same
  /// allocation, so reading one means subtracting this base to get an offset. Passing the address in
  /// rather than dereferencing the pointer is what makes a captured buffer replayable on a machine
  /// that was not the one it came from — and it bounds-checks the read, which dereferencing did not
  /// (PRD §9.4).
  /// </param>
  /// <param name="snapshot">Filled with what was read.</param>
  internal static void ParseProcesses(ReadOnlySpan<byte> buffer, nint bufferBaseAddress, SystemSnapshot snapshot) {
    var count = CountProcesses(buffer);
    var records = snapshot.PrepareProcesses(count);
    var written = 0;
    var offset = 0;
    var totalThreads = 0;

    while (offset >= 0 && offset < buffer.Length && written < records.Length) {
      ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemProcessInformation>(buffer[offset..]);
      ref var record = ref records[written++];
      record = default;

      var pid = (int)entry.UniqueProcessId;
      // CreateTime is a FILETIME and is unique per process at 100 ns resolution, which is exactly
      // what the identity pair needs (PRD §3.2).
      record.Key = new(pid, (ulong)entry.CreateTime);
      record.ParentPid = (int)entry.InheritedFromUniqueProcessId;
      record.Name = ReadImageName(buffer, bufferBaseAddress, entry.ImageName, pid);
      record.SessionId = (int)entry.SessionId;
      record.ThreadCount = (int)entry.NumberOfThreads;
      record.Priority = entry.BasePriority;
      record.Nice = 0;
      record.UserId = -1;
      record.StartTimeUtcTicks = entry.CreateTime > 0 ? DateTime.FromFileTimeUtc(entry.CreateTime).Ticks : 0;

      // FILETIME units are 100 ns; the model is nanoseconds everywhere above the probe.
      record.UserTimeNs = Counter.Of((ulong)Math.Max(0, entry.UserTime) * 100);
      record.KernelTimeNs = Counter.Of((ulong)Math.Max(0, entry.KernelTime) * 100);
      record.CpuTimeNs = Counter.Of((ulong)Math.Max(0, entry.UserTime + entry.KernelTime) * 100);

      // PrivatePageCount is what Task Manager calls "commit"; WorkingSetPrivateSize is the resident
      // part of it. The private column is the commit charge, because that is what the process would
      // give back, which is the question the column exists to answer (PRD §6.1).
      record.PrivateBytes = Counter.Of((ulong)entry.PrivatePageCount);
      record.WorkingSetBytes = Counter.Of((ulong)entry.WorkingSetSize);
      record.VirtualBytes = Counter.Of((ulong)entry.VirtualSize);
      record.SwapBytes = Counter.Of((ulong)entry.PagefileUsage);
      record.ReadBytes = Counter.Of((ulong)Math.Max(0, entry.ReadTransferCount));
      record.WriteBytes = Counter.Of((ulong)Math.Max(0, entry.WriteTransferCount));
      record.HandleCount = Counter.Of(entry.HandleCount);
      record.ContextSwitches = Counter.NotSupported;
      record.MemoryLimitBytes = Counter.NotSupported;
      record.State = entry.NumberOfThreads == 0 ? ProcessState.Dead : ProcessState.Running;
      record.IsSuspended = false;

      totalThreads += (int)entry.NumberOfThreads;
      if (entry.NextEntryOffset == 0)
        break;

      offset += (int)entry.NextEntryOffset;
    }

    snapshot.PrepareProcesses(written);
    snapshot.System.TotalThreads = totalThreads;
  }

  private static int CountProcesses(ReadOnlySpan<byte> buffer) {
    var count = 0;
    var offset = 0;
    while (offset >= 0 && offset < buffer.Length) {
      ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemProcessInformation>(buffer[offset..]);
      ++count;
      if (entry.NextEntryOffset == 0)
        break;

      offset += (int)entry.NextEntryOffset;
    }

    return count;
  }

  private static string ReadImageName(
    ReadOnlySpan<byte> buffer,
    nint bufferBaseAddress,
    NtStructures.UnicodeString name,
    int pid
  ) {
    // Pid 0 has no image name at all; pid 4 is the kernel. Both are real rows and both would
    // otherwise be blank.
    if (name.Buffer == 0 || name.Length == 0)
      return pid switch { 0 => "Idle", 4 => "System", _ => $"({pid})" };

    // Length is in bytes, not characters — the single most common way to read a UNICODE_STRING
    // wrongly, and it reads double the name plus whatever follows it when you get it backwards.
    var offset = (long)name.Buffer - bufferBaseAddress;
    if (offset < 0 || offset + name.Length > buffer.Length)
      return $"({pid})";

    var characters = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(
      buffer.Slice((int)offset, name.Length)
    );

    return new string(characters);
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

  public Counter GetHandleCount(ProcessKey key) {
    // Already in the bulk query on this platform, so there is nothing to do on demand. Returning
    // NotSampledYet rather than a second query keeps the one source of truth.
    return Counter.NotSampledYet;
  }

  public IReadOnlyList<ThreadRecord> GetThreads(ProcessKey key) => [];

  public IReadOnlyList<ModuleRecord> GetModules(ProcessKey key) => [];

  public IReadOnlyList<HandleRecord> GetHandles(ProcessKey key) => [];

  public IReadOnlyList<ConnectionRecord> GetConnections(ProcessKey key) => [];

  public IReadOnlyList<KeyValuePair<string, string>> GetEnvironment(ProcessKey key) => [];

}
