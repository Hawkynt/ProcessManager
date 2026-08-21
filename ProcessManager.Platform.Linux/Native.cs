using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The libc calls the probe uses directly.
/// </summary>
/// <remarks>
/// <c>[LibraryImport]</c> throughout — the source generator emits the marshalling at compile time,
/// which is what keeps the NativeAOT publish clean (PRD §2). Paths are passed as NUL-terminated
/// UTF-8 spans rather than as <see cref="string"/>, because the marshaller would otherwise allocate
/// and copy on every one of the thousands of opens a sample performs (PRD §4).
/// </remarks>
internal static partial class Native {

  private const int _SC_CLK_TCK = 2;
  private const int _SC_PAGESIZE = 30;

  private const int O_RDONLY = 0;
  private const int O_WRONLY = 1;
  private const int O_TRUNC = 0x200;
  private const int O_CLOEXEC = 0x80000;
  private const int O_DIRECTORY = 0x10000;

  [LibraryImport("libc", EntryPoint = "sysconf", SetLastError = true)]
  private static partial long SysConf(int name);

  [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
  private static partial int OpenCore(ref byte path, int flags);

  [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
  private static partial nint ReadCore(int fd, ref byte buffer, nuint count);

  [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
  private static partial nint WriteCore(int fd, ref byte buffer, nuint count);

  [LibraryImport("libc", EntryPoint = "close")]
  private static partial int CloseCore(int fd);

  [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
  private static partial nint ReadLinkCore(ref byte path, ref byte buffer, nuint size);

  [LibraryImport("libc", EntryPoint = "getdents64", SetLastError = true)]
  private static partial nint GetDents64(int fd, ref byte buffer, nuint count);

  [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
  private static partial int Kill(int pid, int signal);

  [LibraryImport("libc", EntryPoint = "geteuid")]
  private static partial uint GetEuid();

  [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
  private static partial int SetPriority(int which, uint who, int value);

  [LibraryImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
  private static partial int SchedSetAffinity(int pid, nuint cpuSetSize, ref ulong mask);

  [LibraryImport("libc", EntryPoint = "sched_setscheduler", SetLastError = true)]
  private static partial int SchedSetScheduler(int pid, int policy, ref int priority);

  [LibraryImport("libc", EntryPoint = "sched_get_priority_min", SetLastError = true)]
  private static partial int SchedGetPriorityMin(int policy);

  [LibraryImport("libc", EntryPoint = "sched_get_priority_max", SetLastError = true)]
  private static partial int SchedGetPriorityMax(int policy);

  /// <summary>
  /// <c>struct rlimit64</c>: the soft limit and the ceiling on it, both unsigned 64-bit.
  /// </summary>
  /// <remarks>
  /// Always the 64-bit form, on every architecture. <c>prlimit</c>'s <c>struct rlimit</c> carries a
  /// 32-bit <c>rlim_t</c> on a 32-bit build unless the caller was compiled with
  /// <c>_FILE_OFFSET_BITS=64</c> — a compile-time switch a P/Invoke has no way to have set — so a
  /// file-size limit above 4 GiB would come back truncated on 32-bit ARM and nowhere else. glibc
  /// exports <c>prlimit64</c> unconditionally and it takes this shape everywhere.
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  public struct ResourceLimitPair {
    public ulong Soft;
    public ulong Hard;
  }

  /// <summary><c>RLIM64_INFINITY</c>: how the kernel spells "no limit".</summary>
  public const ulong ResourceLimitInfinity = ulong.MaxValue;

  /// <summary>Reads one limit. The new-limit pointer is null, which is how a read is asked for.</summary>
  [LibraryImport("libc", EntryPoint = "prlimit64", SetLastError = true)]
  private static partial int PrLimitRead(int pid, int resource, nint newLimit, out ResourceLimitPair oldLimit);

  /// <summary>Sets one limit. The old-limit pointer is null, because the caller already has it.</summary>
  [LibraryImport("libc", EntryPoint = "prlimit64", SetLastError = true)]
  private static partial int PrLimitWrite(int pid, int resource, in ResourceLimitPair newLimit, nint oldLimit);

  public const int EPERM = 1;
  public const int ENOENT = 2;
  public const int ESRCH = 3;
  public const int EINTR = 4;
  public const int EACCES = 13;
  public const int EINVAL = 22;

  public const int SIGKILL = 9;
  public const int SIGTERM = 15;
  public const int SIGCONT = 18;
  public const int SIGSTOP = 19;

  public const int PRIO_PROCESS = 0;

  /// <summary>
  /// <c>USER_HZ</c>: the unit <c>/proc/[pid]/stat</c> counts CPU time in. It is 100 nearly everywhere
  /// and is not guaranteed to be, so it is asked for rather than assumed (PRD §5.1).
  /// </summary>
  /// <remarks>
  /// Guarded on the platform, because <see cref="LinuxProbeOptions"/> reads it in a property
  /// initializer — which runs on Windows and macOS too, where the test suite replays a recorded
  /// tree and there is no libc to ask. Off Linux the constant is what a fixture was recorded with
  /// anyway, and a fixture that was not says so explicitly.
  /// </remarks>
  public static long ClockTicksPerSecond {
    get {
      if (!OperatingSystem.IsLinux())
        return 100;

      var value = SysConf(_SC_CLK_TCK);
      return value > 0 ? value : 100;
    }
  }

  public static long PageSize {
    get {
      if (!OperatingSystem.IsLinux())
        return 4096;

      var value = SysConf(_SC_PAGESIZE);
      return value > 0 ? value : 4096;
    }
  }

  /// <summary>
  /// The effective uid, used to decide whether a privileged file is worth opening at all: attempting
  /// another user's <c>io</c> or <c>fd</c> costs a failed syscall per process per sample, and on a
  /// shared machine that is most of them.
  /// </summary>
  /// <summary>
  /// Off Linux this is 0 — "root" — which is exactly right for a fixture replay: the recorded tree
  /// belongs to whoever recorded it, and refusing to read it because of a uid mismatch would make
  /// every cross-platform test fail for a reason that has nothing to do with parsing.
  /// </summary>
  public static int EffectiveUserId { get; } = OperatingSystem.IsLinux() ? (int)GetEuid() : 0;

  /// <summary>Opens a NUL-terminated path read-only. Returns -1 and sets <paramref name="errno"/>.</summary>
  public static int OpenReadOnly(ReadOnlySpan<byte> nulTerminatedPath, out int errno) {
    errno = 0;
    int fd;
    do {
      fd = OpenCore(ref MemoryMarshal.GetReference(nulTerminatedPath), O_RDONLY | O_CLOEXEC);
      if (fd >= 0)
        return fd;

      errno = Marshal.GetLastPInvokeError();
    } while (errno == EINTR);

    return -1;
  }

  /// <summary>Reads into <paramref name="buffer"/>. Returns -1 and sets <paramref name="errno"/>.</summary>
  public static int Read(int fd, Span<byte> buffer, out int errno) {
    errno = 0;
    while (true) {
      var read = ReadCore(fd, ref MemoryMarshal.GetReference(buffer), (nuint)buffer.Length);
      if (read >= 0)
        return (int)read;

      errno = Marshal.GetLastPInvokeError();
      if (errno != EINTR)
        return -1;
    }
  }

  public static void Close(int fd) => CloseCore(fd);

  /// <summary>
  /// Writes a short value to an existing control file — one in <c>/proc</c> or in
  /// <c>/sys/fs/cgroup</c> — and reports the kernel's own <c>errno</c> when it will not have it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Through <c>open</c> and <c>write</c> rather than <see cref="File.WriteAllText(string,string)"/>
  /// because the <em>reason</em> is the whole answer here. These files accept the open and refuse
  /// the write: lowering <c>oom_score_adj</c> without <c>CAP_SYS_RESOURCE</c> comes back as
  /// <c>EACCES</c> from <c>write</c>, on a file whose mode says the owner may write it. The runtime
  /// turns that into an exception whose type and message vary with the errno, and a caller that had
  /// to read the message to tell "not permitted" from "the process has gone" would be parsing
  /// English (PRD §88).
  /// </para>
  /// <para>
  /// One <c>write</c>, never a loop. A control file takes its value as a single write or not at all,
  /// and a short count on one is a refusal rather than something to continue.
  /// </para>
  /// <para>
  /// No <c>O_CREAT</c>: every file this is used on already exists, and a path that does not is a
  /// kernel that has no such control rather than an invitation to make one.
  /// </para>
  /// </remarks>
  public static bool WriteControlFile(ReadOnlySpan<byte> nulTerminatedPath, ReadOnlySpan<byte> content, out int errno) {
    errno = 0;
    int fd;
    do {
      fd = OpenCore(ref MemoryMarshal.GetReference(nulTerminatedPath), O_WRONLY | O_TRUNC | O_CLOEXEC);
      if (fd >= 0)
        break;

      errno = Marshal.GetLastPInvokeError();
      if (errno != EINTR)
        return false;
    } while (true);

    try {
      while (true) {
        var written = WriteCore(fd, ref MemoryMarshal.GetReference(content), (nuint)content.Length);
        if (written == content.Length) {
          errno = 0;
          return true;
        }

        if (written >= 0) {
          // A control file that took part of a value has been given something it cannot act on.
          errno = EINVAL;
          return false;
        }

        errno = Marshal.GetLastPInvokeError();
        if (errno != EINTR)
          return false;
      }
    } finally {
      Close(fd);
    }
  }

  /// <summary>Resolves a symlink. Null when it does not exist or may not be read.</summary>
  public static string? ReadLink(string path) {
    Span<byte> pathBytes = stackalloc byte[ProcPath.MaxLength];
    var nulTerminated = ProcPath.FromString(pathBytes, path);
    Span<byte> target = stackalloc byte[1024];
    var length = (int)ReadLinkCore(
      ref MemoryMarshal.GetReference((ReadOnlySpan<byte>)nulTerminated),
      ref MemoryMarshal.GetReference(target),
      (nuint)target.Length
    );

    // readlink does not NUL-terminate and returns -1 on failure; a result equal to the buffer size
    // means it was truncated, which for a path we are about to show is worse than showing nothing.
    return length <= 0 || length >= target.Length ? null : Encoding.UTF8.GetString(target[..length]);
  }

  /// <summary>
  /// Counts the entries of a NUL-terminated directory path, excluding <c>.</c> and <c>..</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Exists for <c>/proc/[pid]/fd</c>, which is counted once per process per sample and was, when it
  /// went through the managed enumerator, two thirds of the entire sampling cost. Through libc's
  /// <c>readdir</c> it was still 89 µs per process, because every entry crosses the managed/native
  /// boundary twice. <c>getdents64</c> fills a buffer with the whole directory in one syscall and the
  /// walk stays in managed code (PRD §4).
  /// </para>
  /// <para>
  /// <c>struct linux_dirent64</c> is <c>d_ino</c> (8), <c>d_off</c> (8), <c>d_reclen</c> (2),
  /// <c>d_type</c> (1), then a NUL-terminated <c>d_name</c> — so the record length is at offset 16
  /// and the name starts at 19. Records are walked by <c>d_reclen</c>, never by a fixed stride.
  /// </para>
  /// </remarks>
  public static int CountDirectoryEntries(ReadOnlySpan<byte> nulTerminatedPath, Span<byte> scratch, out int errno) {
    errno = 0;
    var fd = OpenCore(ref MemoryMarshal.GetReference(nulTerminatedPath), O_RDONLY | O_CLOEXEC | O_DIRECTORY);
    if (fd < 0) {
      errno = Marshal.GetLastPInvokeError();
      return -1;
    }

    try {
      var count = 0;
      while (true) {
        var read = (int)GetDents64(fd, ref MemoryMarshal.GetReference(scratch), (nuint)scratch.Length);
        if (read < 0) {
          errno = Marshal.GetLastPInvokeError();
          if (errno == EINTR)
            continue;

          return -1;
        }

        if (read == 0)
          return count;

        for (var offset = 0; offset < read;) {
          var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(scratch[(offset + 16)..]);
          if (recordLength <= 0)
            return count;

          var name = scratch[(offset + 19)..];
          if (name[0] != (byte)'.' || (name[1] != 0 && (name[1] != (byte)'.' || name[2] != 0)))
            ++count;

          offset += recordLength;
        }
      }
    } finally {
      Close(fd);
    }
  }

  /// <summary>
  /// Collects the numeric directory names of <paramref name="nulTerminatedPath"/> into
  /// <paramref name="pids"/> — that is, the process ids under <c>/proc</c>.
  /// </summary>
  /// <remarks>
  /// Same reasoning as <see cref="CountDirectoryEntries"/>: one syscall for the whole directory, and
  /// the pid is parsed out of the buffer rather than out of a string that had to be allocated first.
  /// </remarks>
  /// <param name="minimum">
  /// The smallest name worth keeping. 1 for process ids, where there is no pid 0; 0 for the
  /// descriptors under <c>fd</c>, where 0 is standard input and dropping it undercounts every
  /// process on the machine by one.
  /// </param>
  public static bool ListNumericEntries(
    ReadOnlySpan<byte> nulTerminatedPath,
    Span<byte> scratch,
    List<int> pids,
    int minimum = 1
  ) {
    var fd = OpenCore(ref MemoryMarshal.GetReference(nulTerminatedPath), O_RDONLY | O_CLOEXEC | O_DIRECTORY);
    if (fd < 0)
      return false;

    try {
      while (true) {
        var read = (int)GetDents64(fd, ref MemoryMarshal.GetReference(scratch), (nuint)scratch.Length);
        if (read < 0) {
          if (Marshal.GetLastPInvokeError() == EINTR)
            continue;

          return false;
        }

        if (read == 0)
          return true;

        for (var offset = 0; offset < read;) {
          var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(scratch[(offset + 16)..]);
          if (recordLength <= 0)
            return true;

          var name = scratch[(offset + 19)..];
          var pid = 0;
          var digits = 0;
          var valid = true;
          for (var i = 0; i < name.Length && name[i] != 0; ++i) {
            var digit = (uint)(name[i] - (byte)'0');
            if (digit > 9) {
              valid = false;
              break;
            }

            ++digits;
            pid = pid * 10 + (int)digit;
          }

          // At least one digit, or a nameless entry would be added as a zero the moment a caller
          // allows one.
          if (valid && digits > 0 && pid >= minimum)
            pids.Add(pid);

          offset += recordLength;
        }
      }
    } finally {
      Close(fd);
    }
  }

  public static int SendSignal(int pid, int signal) => Kill(pid, signal);

  public static int SetNice(int pid, int nice) => SetPriority(PRIO_PROCESS, (uint)pid, nice);

  public static int SetAffinityMask(int pid, ulong mask) => SchedSetAffinity(pid, sizeof(ulong), ref mask);

  /// <summary>
  /// <c>sched_setscheduler</c>: the class a process is run by, and its static priority inside it.
  /// </summary>
  /// <remarks>
  /// <c>struct sched_param</c> is one <c>int</c> and nothing else, so it is passed as one rather
  /// than wrapped — the layout is fixed by the ABI and has been since the call existed.
  /// </remarks>
  public static int SetScheduler(int pid, int policy, int priority)
    => OperatingSystem.IsLinux() ? SchedSetScheduler(pid, policy, ref priority) : -1;

  /// <summary>
  /// The static-priority range a class accepts, or <see langword="null"/> where the kernel does not
  /// know the class at all.
  /// </summary>
  /// <remarks>
  /// Asked rather than assumed. It is 1–99 for the real-time classes and 0–0 for the rest on every
  /// Linux anyone runs, and it is still the kernel's answer to give — a class this kernel has never
  /// heard of returns EINVAL here, which is a better refusal than a syscall that fails later with
  /// nothing to say about why.
  /// </remarks>
  public static (int Min, int Max)? SchedulerPriorityRange(int policy) {
    // Guarded on the platform for the reason ClockTicksPerSecond is: the tests replay a recorded
    // tree on Windows and macOS, where there is no libc of this shape to ask — and where the numbers
    // would mean something else anyway, since the SCHED_* constants are not the same ones.
    if (!OperatingSystem.IsLinux())
      return null;

    var min = SchedGetPriorityMin(policy);
    if (min < 0)
      return null;

    var max = SchedGetPriorityMax(policy);
    return max < 0 ? null : (min, max);
  }

  /// <summary>
  /// <c>ioprio_get</c> and <c>ioprio_set</c>, which have no glibc wrappers and must be called as
  /// raw syscalls.
  /// </summary>
  /// <remarks>
  /// The numbers are architecture-specific — 251/250 on x86-64, 30/31 on arm64 — which is why this
  /// is the only place in the program that hard-codes a syscall number. An architecture whose
  /// numbers are not known reports that rather than calling something else by accident.
  /// </remarks>
  private static (int Get, int Set)? IoPrioSyscalls => RuntimeInformation.ProcessArchitecture switch {
    Architecture.X64 => (252, 251),
    Architecture.Arm64 => (31, 30),
    Architecture.X86 => (290, 289),
    _ => null,
  };

  /// <summary>IOPRIO_WHO_PROCESS: the "who" is a pid or, for a thread, a tid.</summary>
  private const int _IoPrioWhoProcess = 1;

  [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
  private static partial long Syscall(long number, long a, long b, long c);

  /// <summary>The packed I/O priority of a process or thread, or -1 with errno set.</summary>
  public static int GetIoPriority(int pid) {
    if (IoPrioSyscalls is not { } numbers)
      return -1;

    return (int)Syscall(numbers.Get, _IoPrioWhoProcess, pid, 0);
  }

  public static int SetIoPriority(int pid, int packed) {
    if (IoPrioSyscalls is not { } numbers)
      return -1;

    return (int)Syscall(numbers.Set, _IoPrioWhoProcess, pid, packed);
  }

  /// <summary>Whether this architecture's I/O priority syscall numbers are known at all.</summary>
  public static bool SupportsIoPriority => IoPrioSyscalls is not null;

  /// <summary>
  /// Reads one of another process's resource limits, or -1 with errno set.
  /// </summary>
  /// <remarks>
  /// Only the setting side of the program uses this, to read back what it just wrote; the reading
  /// side parses <c>/proc/[pid]/limits</c>, which answers for a recorded tree as well as for a live
  /// process (see <c>Query.ProcLimitsParser</c>).
  /// </remarks>
  public static int GetResourceLimit(int pid, int resource, out ResourceLimitPair limit) {
    if (!OperatingSystem.IsLinux()) {
      limit = default;
      return -1;
    }

    return PrLimitRead(pid, resource, 0, out limit);
  }

  public static int SetResourceLimit(int pid, int resource, in ResourceLimitPair limit)
    => OperatingSystem.IsLinux() ? PrLimitWrite(pid, resource, in limit, 0) : -1;

  /// <summary>AT_HWCAP and AT_HWCAP2 of the auxiliary vector, which is where ARM publishes its
  /// feature bits.</summary>
  private const int _AtHwCap = 16;

  private const int _AtHwCap2 = 26;

  [LibraryImport("libc", EntryPoint = "getauxval")]
  private static partial ulong GetAuxVal(ulong type);

  /// <summary>
  /// The kernel's two hardware capability words.
  /// </summary>
  /// <remarks>
  /// <c>getauxval</c> returns 0 both for "this entry is absent" and for "every bit is clear", and
  /// does not distinguish them without <c>errno</c>. Both mean the same thing to a caller here —
  /// nothing to report — so the ambiguity costs nothing and is not worth an errno dance.
  /// </remarks>
  public static (ulong HwCap, ulong HwCap2) HardwareCapabilities() {
    try {
      return (GetAuxVal(_AtHwCap), GetAuxVal(_AtHwCap2));
    } catch (EntryPointNotFoundException) {
      // A libc without getauxval predates every kernel this program supports, but a musl or uclibc
      // build that lacks it should report no features rather than fail to start.
      return (0, 0);
    }
  }

  /// <summary>
  /// <c>statx</c>, for the one thing <c>stat</c> cannot answer: when a file was created (PRD §14).
  /// </summary>
  /// <remarks>
  /// The buffer is passed as raw bytes rather than as a marshalled structure. <c>struct statx</c> has
  /// grown three times since it was introduced — a subvolume id, direct-I/O alignments, atomic write
  /// units — and a structure declared here would have to be right about a layout that keeps changing
  /// at the end. Only two fields are read, both from the part that has never moved: <c>stx_mask</c>
  /// at offset 0 and <c>stx_btime</c> at 0x50, checked against this machine's own
  /// <c>linux/stat.h</c>.
  /// </remarks>
  [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
  private static partial int StatxCore(int directoryFd, ref byte path, int flags, uint mask, ref byte buffer);

  private const int _AtFdCwd = -100;
  private const uint _StatxBTime = 0x800;
  private const int _StatxBTimeOffset = 0x50;
  private const int _StatxSize = 256;

  /// <summary>Set once a libc without <c>statx</c> has been found, so the miss is paid for once.</summary>
  private static bool _statxMissing;

  /// <summary>
  /// When the file was created, or null where nothing remembers.
  /// </summary>
  /// <remarks>
  /// Null is the common answer and an honest one. The kernel returns the birth time only where the
  /// file system carries it: btrfs and xfs do, an ext4 formatted without <c>crtime</c> does not, and
  /// most network file systems do not. <c>stx_mask</c> is what says which happened — the field is
  /// left cleared otherwise — so it is checked rather than assumed, and a zero date is never
  /// reported as 1970 (PRD §72.3).
  /// </remarks>
  /// <param name="errno">
  /// 0 when the call itself succeeded, which includes the ordinary case of a file system that
  /// carries no birth time: <c>statx</c> answers happily and leaves the bit clear. Anything else is
  /// a failure to ask — <see cref="ENOENT"/> for an image replaced underneath a running process,
  /// <see cref="EACCES"/> for one this user may not reach — and the caller says which (PRD §72.3).
  /// </param>
  public static bool TryCreationTimeUtc(string path, out DateTime when, out int errno) {
    when = default;
    errno = 0;
    var created = CreationTimeUtc(path, ref errno);
    if (created is not { } value)
      return false;

    when = value;
    return true;
  }

  private static DateTime? CreationTimeUtc(string path, ref int errno) {
    if (_statxMissing || !OperatingSystem.IsLinux() || string.IsNullOrEmpty(path))
      return null;

    var length = Encoding.UTF8.GetByteCount(path);
    Span<byte> pathBytes = length < 512 ? stackalloc byte[length + 1] : new byte[length + 1];
    Encoding.UTF8.GetBytes(path, pathBytes);
    pathBytes[length] = 0;

    Span<byte> buffer = stackalloc byte[_StatxSize];
    buffer.Clear();

    try {
      if (StatxCore(
            _AtFdCwd,
            ref MemoryMarshal.GetReference(pathBytes),
            0,
            _StatxBTime,
            ref MemoryMarshal.GetReference(buffer)
          ) != 0) {
        errno = LastError;
        return null;
      }
    } catch (EntryPointNotFoundException) {
      // Before glibc 2.28 there is no statx to call. Every column that depends on it is unknown from
      // here on, which is the truth about this machine rather than a failure to report.
      _statxMissing = true;
      return null;
    } catch (DllNotFoundException) {
      _statxMissing = true;
      return null;
    }

    // The mask says what was actually filled in. A file system with no birth time answers the call
    // successfully and clears this bit, which is exactly the case that must not become a date.
    if ((MemoryMarshal.Read<uint>(buffer) & _StatxBTime) == 0)
      return null;

    var seconds = MemoryMarshal.Read<long>(buffer[_StatxBTimeOffset..]);
    var nanoseconds = MemoryMarshal.Read<uint>(buffer[(_StatxBTimeOffset + 8)..]);
    if (seconds <= 0)
      return null;

    return DateTime.UnixEpoch.AddTicks(seconds * TimeSpan.TicksPerSecond + nanoseconds / 100);
  }

  private const uint _StatxType = 0x1;
  private const int _StatxModeOffset = 0x1C;
  private const int _StatxRdevMajorOffset = 0x80;
  private const int _StatxRdevMinorOffset = 0x84;

  /// <summary>The file-type bits of <c>st_mode</c>, and the seven values POSIX puts in them.</summary>
  private const ushort _SIfmt = 0xF000;

  /// <summary>
  /// What kind of node a path is, and which device it is when it is a device (PRD §32).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The same <c>statx</c> the birth time comes from, asked for a different field. Three offsets out
  /// of the part of <c>struct statx</c> that has never moved: the mode at 0x1C and the device the
  /// node <em>is</em> at 0x80, next to — and not to be confused with — the device it is
  /// <em>on</em> at 0x88.
  /// </para>
  /// <para>
  /// Called on <c>/proc/[pid]/fd/[n]</c>, which is a symlink and is deliberately followed: the
  /// question is what the descriptor points at, and the link's own mode says only which way the
  /// descriptor was opened.
  /// </para>
  /// <para>
  /// The clear-type-bits case is the one to keep. An anonymous inode answers this call happily and
  /// reports mode <c>0600</c> with no type at all, which is <see cref="FileNodeType.None"/> and not
  /// a failure to ask (PRD §72.3).
  /// </para>
  /// </remarks>
  public static FileNodeType NodeTypeOf(string path, out string? device) {
    device = null;
    Span<byte> buffer = stackalloc byte[_StatxSize];
    if (!TryStatx(path, _StatxType, buffer))
      return FileNodeType.Unknown;

    // The mask again: a file system that could not say leaves the field cleared, and a cleared
    // field is a real answer here — so the two have to be told apart before it is read.
    if ((MemoryMarshal.Read<uint>(buffer) & _StatxType) == 0)
      return FileNodeType.Unknown;

    var type = (ushort)(MemoryMarshal.Read<ushort>(buffer[_StatxModeOffset..]) & _SIfmt);
    switch (type) {
      case 0x8000: return FileNodeType.Regular;
      case 0x4000: return FileNodeType.Directory;
      case 0xA000: return FileNodeType.SymbolicLink;
      case 0x1000: return FileNodeType.Fifo;
      case 0xC000: return FileNodeType.Socket;
      case 0x2000:
      case 0x6000:
        var major = MemoryMarshal.Read<uint>(buffer[_StatxRdevMajorOffset..]);
        var minor = MemoryMarshal.Read<uint>(buffer[_StatxRdevMinorOffset..]);
        device = string.Create(
          System.Globalization.CultureInfo.InvariantCulture,
          $"{major}:{minor}"
        );

        return type == 0x2000 ? FileNodeType.CharacterDevice : FileNodeType.BlockDevice;
      default:
        // Nought, which is what every anonymous inode reports. Anything else is a type this kernel
        // has and this program has not been taught, and both are "the bits say nothing I know"
        // rather than "nobody looked".
        return FileNodeType.None;
    }
  }

  /// <summary>One <c>statx</c> into a caller's buffer, or false with the buffer untouched.</summary>
  private static bool TryStatx(string path, uint mask, Span<byte> buffer) {
    if (_statxMissing || !OperatingSystem.IsLinux() || string.IsNullOrEmpty(path))
      return false;

    var length = Encoding.UTF8.GetByteCount(path);
    Span<byte> pathBytes = length < 512 ? stackalloc byte[length + 1] : new byte[length + 1];
    Encoding.UTF8.GetBytes(path, pathBytes);
    pathBytes[length] = 0;
    buffer.Clear();

    try {
      return StatxCore(
        _AtFdCwd,
        ref MemoryMarshal.GetReference(pathBytes),
        0,
        mask,
        ref MemoryMarshal.GetReference(buffer)
      ) == 0;
    } catch (EntryPointNotFoundException) {
      _statxMissing = true;
      return false;
    } catch (DllNotFoundException) {
      _statxMissing = true;
      return false;
    }
  }

  public static int LastError => Marshal.GetLastPInvokeError();

  #region interface addresses (PRD §49)

  /// <summary>
  /// <c>struct ifaddrs</c>: one entry per address of one interface, as a linked list.
  /// </summary>
  /// <remarks>
  /// The union of broadcast and destination address is one pointer whichever half is meant, so it
  /// needs no discriminating here. <c>ifa_flags</c> is an <c>unsigned int</c> followed by four bytes
  /// of padding on every 64-bit ABI, which the sequential layout of a struct containing pointers
  /// produces by itself — declaring the padding by hand would be wrong on 32-bit.
  /// </remarks>
  [StructLayout(LayoutKind.Sequential)]
  internal struct IfAddrs {
    public nint Next;
    public nint Name;
    public uint Flags;
    public nint Address;
    public nint Netmask;
    public nint Broadcast;
    public nint Data;
  }

  /// <summary>AF_INET and AF_INET6 as Linux numbers them, which are not the BSD ones for IPv6.</summary>
  public const ushort AF_INET = 2;

  public const ushort AF_INET6 = 10;

  [LibraryImport("libc", EntryPoint = "getifaddrs", SetLastError = true)]
  private static partial int GetIfAddrs(out nint list);

  [LibraryImport("libc", EntryPoint = "freeifaddrs")]
  private static partial void FreeIfAddrs(nint list);

  /// <summary>
  /// Every address the machine's interfaces carry, handed one at a time to <paramref name="visit"/>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// One call for the whole machine rather than an ioctl per interface, and the only way to get IPv6
  /// and IPv4 from the same place: there is no <c>/proc</c> file listing an interface's IPv4
  /// addresses, and <c>/proc/net/if_inet6</c> lists only the other half.
  /// </para>
  /// <para>
  /// The list is freed in a <c>finally</c>: it is heap memory libc allocated, and a caller that
  /// throws while walking it would leak the whole table.
  /// </para>
  /// </remarks>
  /// <param name="visit">
  /// Called with the interface name, the address family and the raw <c>sockaddr</c> bytes of the
  /// address and of its netmask. The spans are only valid for the duration of the call.
  /// </param>
  public static unsafe bool ForEachInterfaceAddress(InterfaceAddressVisitor visit) {
    ArgumentNullException.ThrowIfNull(visit);
    if (!OperatingSystem.IsLinux())
      return false;

    nint list;
    try {
      if (GetIfAddrs(out list) != 0)
        return false;
    } catch (DllNotFoundException) {
      return false;
    } catch (EntryPointNotFoundException) {
      return false;
    }

    try {
      for (var entry = list; entry != 0;) {
        ref var record = ref *(IfAddrs*)entry;
        entry = record.Next;
        if (record.Name == 0 || record.Address == 0)
          continue;

        var family = *(ushort*)record.Address;
        if (family is not (AF_INET or AF_INET6))
          continue;

        // sockaddr_in is 16 bytes and sockaddr_in6 is 28; the whole of either is handed over so the
        // reader can take the address out of the offset its own family puts it at.
        var length = family == AF_INET ? 16 : 28;
        var netmask = record.Netmask == 0
          ? ReadOnlySpan<byte>.Empty
          : new ReadOnlySpan<byte>((void*)record.Netmask, length);

        visit(
          Marshal.PtrToStringUTF8(record.Name) ?? string.Empty,
          family,
          new ReadOnlySpan<byte>((void*)record.Address, length),
          netmask
        );
      }
    } finally {
      FreeIfAddrs(list);
    }

    return true;
  }

  /// <summary>What <see cref="ForEachInterfaceAddress"/> hands to its caller.</summary>
  public delegate void InterfaceAddressVisitor(
    string interfaceName,
    ushort family,
    ReadOnlySpan<byte> address,
    ReadOnlySpan<byte> netmask
  );

  #endregion

  #region wireless (PRD §49)

  private const int AF_INET_DOMAIN = 2;
  private const int SOCK_DGRAM = 2;

  /// <summary>SIOCGIWESSID and SIOCGIWFREQ of the wireless extensions.</summary>
  public const ulong SIOCGIWESSID = 0x8B1B;

  public const ulong SIOCGIWFREQ = 0x8B05;

  [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
  private static partial int SocketCore(int domain, int type, int protocol);

  [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
  private static partial int IoctlCore(int fd, ulong request, ref byte argument);

  /// <summary>
  /// One <c>ioctl</c> on a throwaway datagram socket, which is how every wireless query is made.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>ioctl</c> is variadic in C and is declared here with a fixed third argument. That is what
  /// every caller of it does and what the ABI guarantees for an integer-class argument; a variadic
  /// declaration would buy nothing and cannot be expressed by the source generator anyway.
  /// </para>
  /// <para>
  /// The socket is a handle to the kernel's networking stack rather than a connection: nothing is
  /// sent, the family is irrelevant, and it is closed immediately. It is the only way to reach these
  /// requests, which are addressed to interfaces rather than to files.
  /// </para>
  /// </remarks>
  /// <returns>False when the call failed, which for a wired interface is the ordinary answer.</returns>
  public static bool TryInterfaceIoctl(ulong request, Span<byte> argument) {
    if (!OperatingSystem.IsLinux() || argument.IsEmpty)
      return false;

    int fd;
    try {
      fd = SocketCore(AF_INET_DOMAIN, SOCK_DGRAM, 0);
    } catch (DllNotFoundException) {
      return false;
    }

    if (fd < 0)
      return false;

    try {
      return IoctlCore(fd, request, ref MemoryMarshal.GetReference(argument)) >= 0;
    } finally {
      Close(fd);
    }
  }

  #endregion

}
