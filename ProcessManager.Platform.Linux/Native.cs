using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

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
  private const int O_CLOEXEC = 0x80000;
  private const int O_DIRECTORY = 0x10000;

  [LibraryImport("libc", EntryPoint = "sysconf", SetLastError = true)]
  private static partial long SysConf(int name);

  [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
  private static partial int OpenCore(ref byte path, int flags);

  [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
  private static partial nint ReadCore(int fd, ref byte buffer, nuint count);

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

  public const int EPERM = 1;
  public const int ENOENT = 2;
  public const int ESRCH = 3;
  public const int EINTR = 4;
  public const int EACCES = 13;

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
  public static bool ListNumericEntries(ReadOnlySpan<byte> nulTerminatedPath, Span<byte> scratch, List<int> pids) {
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
          var valid = true;
          for (var i = 0; i < name.Length && name[i] != 0; ++i) {
            var digit = (uint)(name[i] - (byte)'0');
            if (digit > 9) {
              valid = false;
              break;
            }

            pid = pid * 10 + (int)digit;
          }

          if (valid && pid > 0)
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

  public static int LastError => Marshal.GetLastPInvokeError();

}
