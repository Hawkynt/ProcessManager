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
    // A counter that reads zero for every process on the machine is not a measurement, it is an
    // unimplemented stub. Wine returns zero for these three from SystemProcessInformation, and
    // reporting "0 B private bytes" for every process would be a confident lie where "not reported
    // here" is the truth (PRD §72.3). Real Windows sets each of these on the first process with any
    // memory at all, so the fix-up pass below never runs there.
    var anyPrivateBytes = false;
    var anyPrivateWorkingSet = false;
    var anyPageFaults = false;
    var anyCycles = false;

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
      record.PrivateWorkingSetBytes = Counter.Of((ulong)Math.Max(0, entry.WorkingSetPrivateSize));
      record.WorkingSetBytes = Counter.Of((ulong)entry.WorkingSetSize);
      record.PeakWorkingSetBytes = Counter.Of((ulong)entry.PeakWorkingSetSize);
      record.VirtualBytes = Counter.Of((ulong)entry.VirtualSize);
      record.PeakVirtualBytes = Counter.Of((ulong)entry.PeakVirtualSize);
      record.SwapBytes = Counter.Of((ulong)entry.PagefileUsage);
      record.PagedPoolBytes = Counter.Of((ulong)entry.QuotaPagedPoolUsage);
      record.PeakPagedPoolBytes = Counter.Of((ulong)entry.QuotaPeakPagedPoolUsage);
      record.NonPagedPoolBytes = Counter.Of((ulong)entry.QuotaNonPagedPoolUsage);
      record.PeakNonPagedPoolBytes = Counter.Of((ulong)entry.QuotaPeakNonPagedPoolUsage);
      record.PageFaults = Counter.Of(entry.PageFaultCount);
      record.Cycles = Counter.Of(entry.CycleTime);

      anyPrivateBytes |= entry.PrivatePageCount != 0;
      anyPrivateWorkingSet |= entry.WorkingSetPrivateSize > 0;
      anyPageFaults |= entry.PageFaultCount != 0;
      anyCycles |= entry.CycleTime != 0;
      record.ReadBytes = Counter.Of((ulong)Math.Max(0, entry.ReadTransferCount));
      record.WriteBytes = Counter.Of((ulong)Math.Max(0, entry.WriteTransferCount));
      record.OtherBytes = Counter.Of((ulong)Math.Max(0, entry.OtherTransferCount));
      record.HandleCount = Counter.Of(entry.HandleCount);
      // Windows has all three object types and a handle table to count them in; walking it is not
      // written, so this is a fact about us rather than about the machine (PRD §7, §20).
      record.SocketCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      record.FileCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      record.PipeCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      // Per-process context switches are per *thread* in this structure; summing every thread of
      // every process on every sample is not worth a column nobody sorts by. The threads carry it.
      record.ContextSwitches = Counter.NotSupported;
      record.MemoryLimitBytes = Counter.NotSupported;
      // Windows has no cgroups; a job object can cap CPU, but it counts nothing that corresponds to
      // a throttled period, so there is no figure here rather than a nought (PRD §5.3).
      record.ThrottledPeriods = Counter.NotSupported;
      // Affinity is the other case: GetProcessAffinityMask answers this perfectly well and we have
      // not written it, which is a fact about us rather than about the machine (PRD §7).
      record.CpuAffinity = null;
      record.CpuAffinityReason = UnknownReason.NotImplementedHere;

      // Windows names the owning process in the connection table itself, so the socket counts are
      // reachable here without the descriptor scan Linux needs — and are not read yet. Saying so
      // beats a nought, which would report every service on the machine as holding no sockets
      // (PRD §7, §18).
      record.TcpSocketCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      record.UdpSocketCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      record.ListeningSocketCount = Counter.Unknown(UnknownReason.NotImplementedHere);
      record.RemoteEndpointCount = Counter.Unknown(UnknownReason.NotImplementedHere);

      // Windows has no seccomp, no no_new_privs and no capability mask; it has integrity levels and
      // privileges instead, which are different things and get their own fields when they are built.
      record.SeccompMode = Counter.NotSupported;
      record.SeccompFilters = Counter.NotSupported;
      record.NoNewPrivileges = Counter.NotSupported;
      record.EffectiveCapabilities = Counter.NotSupported;
      record.PermittedCapabilities = Counter.NotSupported;
      record.InheritableCapabilities = Counter.NotSupported;
      record.BoundingCapabilities = Counter.NotSupported;
      record.AmbientCapabilities = Counter.NotSupported;
      record.EffectiveUserId = -1;
      record.SecurityContextReason = UnknownReason.NotSupportedOnPlatform;
      record.ConfinementMode = Counter.NotSupported;
      // Windows reports its mitigations as a policy per process rather than as a state per thread,
      // and those are §21's own fields — dep, aslr, cfg, cet — which are not built. The Linux
      // readings have no counterpart to be filled in from here, so they say so rather than
      // reporting a machine with every mitigation off (PRD §5.3, §72.3).
      record.SpeculationStoreBypass = Counter.NotSupported;
      record.SpeculationIndirectBranch = Counter.NotSupported;
      record.ThreadFeatures = Counter.NotSupported;
      // A process on Windows has no file-creation mask: the equivalent is the inherited ACL of the
      // directory a file is made in, which belongs to the file rather than to the process.
      record.Umask = Counter.NotSupported;
      // Whether a debugger is attached is answerable here through CheckRemoteDebuggerPresent, and
      // that names no debugger — so this is unbuilt rather than unanswerable (PRD §7).
      record.TracerPid = Counter.Unknown(UnknownReason.NotImplementedHere);
      // There is no descriptor table to size. The handle table has a quota, which is a different
      // number and belongs to whichever field ends up reporting it.
      record.DescriptorTableSize = Counter.NotSupported;
      // Hashing an image is the same operation on any platform and nothing here asks for it yet,
      // which is a fact about us rather than about Windows (PRD §7, §21).
      record.ImageSha256 = null;
      record.ImageSha1 = null;
      record.ImageHashReason = UnknownReason.NotImplementedHere;

      // -1 rather than the zero a fresh struct carries, because zero is a real account on the
      // platform these fields come from: a record nobody filled would otherwise report every
      // process as running with the superuser's identity (PRD §5.3). Windows has a token with
      // groups and privileges in it, which is a different shape and gets its own fields (§36).
      record.SavedUserId = -1;
      record.FilesystemUserId = -1;
      record.GroupId = -1;
      record.EffectiveGroupId = -1;
      record.SavedGroupId = -1;
      record.FilesystemGroupId = -1;
      record.SupplementaryGroups = null;
      record.SupplementaryGroupsReason = UnknownReason.NotSupportedOnPlatform;

      // The bulk query carries this per thread, not per process; a process does not have one.
      record.LastCpu = -1;

      // Elevation is a different case: Windows reports it perfectly well through the process token,
      // and we have not written that yet. Saying "not supported here" would tell the reader the
      // machine cannot answer a question it can (PRD §7).
      // Filled from the token a moment later, in the pass that resolves the owner.
      record.IsElevated = Counter.NotPermitted;
      record.IntegrityLevel = Counter.NotPermitted;
      record.State = entry.NumberOfThreads == 0 ? ProcessState.Dead : ProcessState.Running;
      record.IsSuspended = false;

      totalThreads += (int)entry.NumberOfThreads;
      if (entry.NextEntryOffset == 0)
        break;

      offset += (int)entry.NextEntryOffset;
    }

    if (!anyPrivateBytes || !anyPrivateWorkingSet || !anyPageFaults || !anyCycles) {
      var parsed = records[..written];
      for (var i = 0; i < parsed.Length; ++i) {
        ref var record = ref parsed[i];
        if (!anyPrivateBytes)
          record.PrivateBytes = Counter.NotSupported;
        if (!anyPrivateWorkingSet)
          record.PrivateWorkingSetBytes = Counter.NotSupported;
        if (!anyPageFaults)
          record.PageFaults = Counter.NotSupported;
        if (!anyCycles)
          record.Cycles = Counter.NotSupported;
      }
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
  /// <summary>
  /// The documented KWAIT_REASON values, as words. Only the ones a user-mode thread is actually
  /// found in are named; the rest are rare enough that the number is more honest than a guess.
  /// </summary>
  private static string? MapWaitReason(uint reason) => reason switch {
    0 => "executive",
    1 => "free page",
    2 => "page in",
    3 => "pool allocation",
    4 => "delay execution",
    5 => "suspended",
    6 => "user request",
    7 => "wr executive",
    8 => "wr free page",
    9 => "wr page in",
    13 => "wr queue",
    15 => "wr virtual memory",
    27 => "wr dispatch int",
    31 => "wr keyed event",
    _ => reason.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

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
            // Windows records the start routine of every thread, which Linux does not — see
            // ThreadRecord.StartAddress. A zero here is a thread whose start address the query would
            // not give up, so it stays a hole rather than becoming the address 0x0 (PRD §72.3).
            thread.StartAddress == 0 ? Counter.NotPermitted : Counter.Of((ulong)thread.StartAddress),
            null,
            thread.Priority,
            // Windows names a thread only when the program calls SetThreadDescription, and the
            // bulk query does not carry the name even then.
            Name: null,
            UserTimeNs: Counter.Of((ulong)Math.Max(0, thread.UserTime) * 100),
            KernelTimeNs: Counter.Of((ulong)Math.Max(0, thread.KernelTime) * 100),
            ContextSwitches: Counter.Of(thread.ContextSwitches),
            LastCpu: -1,
            WaitReason: MapWaitReason(thread.WaitReason),
            // Windows counts switches but does not split them, and the bulk query carries neither an
            // affinity nor a base priority. Each has to be stated: default(Counter) is a confident
            // zero, and a thread that has never yielded voluntarily would be a remarkable claim to
            // make about every thread on the machine (PRD §72.3).
            VoluntaryContextSwitches: Counter.NotSupported,
            InvoluntaryContextSwitches: Counter.NotSupported,
            BasePriority: null,
            Policy: SchedulingPolicy.Unknown,
            Affinity: null,
            // The rest of §29 needs a handle on the thread rather than the bulk query: the module a
            // start address is in wants the process's module list, the registers want
            // GetThreadContext, and the mode wants a stack walk. None of it is written here yet, and
            // "not implemented here" is a different sentence from "Windows has no such thing" — one
            // is a fact about us and the other about the operating system (PRD §7).
            StartModule: null,
            InstructionPointer: Counter.Unknown(UnknownReason.NotImplementedHere),
            InstructionModule: null,
            StackPointer: Counter.Unknown(UnknownReason.NotImplementedHere),
            StackBytes: Counter.Unknown(UnknownReason.NotImplementedHere),
            Mode: ThreadMode.Unknown,
            // Windows dispatches system calls by number too, but the bulk query does not carry the
            // one a thread is in and there is no supported way to ask.
            SyscallNumber: Counter.NotSupported,
            QueuedNs: Counter.NotSupported
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
