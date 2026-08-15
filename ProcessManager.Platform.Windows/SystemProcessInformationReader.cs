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

}
