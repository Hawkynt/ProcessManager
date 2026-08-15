using System.Runtime.InteropServices;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Walks a <c>SYSTEM_PROCESS_INFORMATION</c> chain into a snapshot.
/// </summary>
/// <remarks>
/// Separate from <see cref="WindowsProbe"/> and deliberately <em>not</em> marked
/// <c>[SupportedOSPlatform("windows")]</c>: nothing in here calls a Windows API. It reads bytes and
/// writes records, which is exactly why a buffer captured on Windows can be replayed through it on
/// the Linux and macOS CI legs (PRD §9.4). The analyzer found this before the design did — CA1416
/// fired on a test that is reachable on every platform calling into a type that claimed to be
/// Windows-only.
/// </remarks>
internal static class SystemProcessInformationReader {

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
  public static void Parse(ReadOnlySpan<byte> buffer, nint bufferBaseAddress, SystemSnapshot snapshot) {
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


  /// <summary>
  /// Walks the chain again for one process and yields its threads.
  /// </summary>
  /// <remarks>
  /// Not part of <see cref="Parse"/>: the process list is refreshed every second and the thread list
  /// is looked at when somebody opens a detail view, so materialising every thread of every process
  /// on every sample would be work nobody asked for (PRD §3.5). The bytes are already here, which is
  /// why it costs nothing to ask later.
  /// </remarks>
  public static IReadOnlyList<ThreadRecord> ReadThreads(ReadOnlySpan<byte> buffer, ProcessKey key) {
    var entrySize = System.Runtime.CompilerServices.Unsafe.SizeOf<NtStructures.SystemProcessInformation>();
    var threadSize = System.Runtime.CompilerServices.Unsafe.SizeOf<NtStructures.SystemThreadInformation>();

    var offset = 0;
    while (offset >= 0 && offset + entrySize <= buffer.Length) {
      ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemProcessInformation>(buffer[offset..]);
      if ((int)entry.UniqueProcessId == key.Pid && (ulong)entry.CreateTime == key.StartTicks) {
        var count = (int)entry.NumberOfThreads;
        var threads = new List<ThreadRecord>(count);
        var threadOffset = offset + entrySize;
        for (var i = 0; i < count && threadOffset + threadSize <= buffer.Length; ++i, threadOffset += threadSize) {
          ref readonly var thread = ref MemoryMarshal.AsRef<NtStructures.SystemThreadInformation>(buffer[threadOffset..]);
          threads.Add(new(
            (int)thread.ClientId.UniqueThread,
            MapThreadState(thread.ThreadState),
            Counter.Of((ulong)Math.Max(0, thread.KernelTime + thread.UserTime) * 100),
            thread.CreateTime > 0 ? DateTime.FromFileTimeUtc(thread.CreateTime).Ticks : 0,
            (ulong)thread.StartAddress,
            null,
            thread.Priority
          ));
        }

        return threads;
      }

      if (entry.NextEntryOffset == 0)
        break;

      offset += (int)entry.NextEntryOffset;
    }

    return [];
  }

  /// <summary>
  /// <c>KTHREAD_STATE</c> mapped onto the model's states. Windows distinguishes rather more of them
  /// than a process list can usefully show, so several collapse onto one.
  /// </summary>
  private static ProcessState MapThreadState(uint state) => state switch {
    0 => ProcessState.Idle,          // Initialized
    1 => ProcessState.Sleeping,      // Ready
    2 => ProcessState.Running,       // Running
    3 => ProcessState.Sleeping,      // Standby
    4 => ProcessState.Dead,          // Terminated
    5 => ProcessState.Sleeping,      // Waiting
    _ => ProcessState.Unknown,
  };

}
