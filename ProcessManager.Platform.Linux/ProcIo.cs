using System.Text;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// How the probe touches the file system.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations, and the reason is a promise this document's §9.1 makes: the parsers are
/// tested against recorded <c>/proc</c> trees on <em>every</em> CI leg, so that the Linux parser is
/// exercised on the Windows and macOS runners too. That promise was broken the moment the reader
/// went to raw syscalls for speed — <c>open</c>, <c>read</c> and <c>close</c> do not exist on
/// Windows, and <c>getdents64</c> does not exist on macOS, so the fixture tests failed on two of
/// three legs with <c>DllNotFoundException</c>.
/// </para>
/// <para>
/// So: <see cref="SyscallProcIo"/> on Linux, where the speed is the whole point (PRD §4), and
/// <see cref="ManagedProcIo"/> everywhere else, where the point is only that a directory of recorded
/// files parses to the right numbers. The choice is made once, in the probe's constructor.
/// </para>
/// </remarks>
internal abstract class ProcIo {

  /// <summary>The one for the running platform: syscalls on Linux, the BCL anywhere else.</summary>
  public static ProcIo ForCurrentPlatform { get; } = OperatingSystem.IsLinux()
    ? new SyscallProcIo()
    : new ManagedProcIo();

  /// <summary>Opens a NUL-terminated path read-only. Returns -1 and sets <paramref name="errno"/>.</summary>
  public abstract int OpenReadOnly(scoped ReadOnlySpan<byte> nulTerminatedPath, out int errno);

  /// <summary>Reads into <paramref name="buffer"/>. Returns -1 and sets <paramref name="errno"/>.</summary>
  public abstract int Read(int handle, Span<byte> buffer, out int errno);

  public abstract void Close(int handle);

  /// <summary>Counts a directory's entries, excluding <c>.</c> and <c>..</c>. -1 on failure.</summary>
  public abstract int CountDirectoryEntries(scoped ReadOnlySpan<byte> nulTerminatedPath, Span<byte> scratch, out int errno);

  /// <summary>
  /// Collects the numerically named entries — the process ids under <c>/proc</c>, and the open
  /// descriptors under <c>/proc/[pid]/fdinfo</c>, which are files rather than directories.
  /// </summary>
  /// <param name="minimum">
  /// The smallest name to keep: 1 for process ids, and 0 for descriptors, where 0 is standard input
  /// and every process on the machine has one.
  /// </param>
  public abstract bool ListNumericEntries(
    scoped ReadOnlySpan<byte> nulTerminatedPath,
    Span<byte> scratch,
    List<int> pids,
    int minimum = 1
  );

  /// <summary>Resolves a symlink, or null.</summary>
  public abstract string? ReadLink(string path);

  /// <summary>UTF-8 bytes back to a path string, for the managed implementation.</summary>
  protected static string Decode(scoped ReadOnlySpan<byte> nulTerminatedPath) {
    var end = nulTerminatedPath.IndexOf((byte)0);
    return Encoding.UTF8.GetString(end < 0 ? nulTerminatedPath : nulTerminatedPath[..end]);
  }

}

/// <summary>The fast path: libc directly, no managed file object anywhere (PRD §4).</summary>
internal sealed class SyscallProcIo : ProcIo {

  public override int OpenReadOnly(scoped ReadOnlySpan<byte> nulTerminatedPath, out int errno)
    => Native.OpenReadOnly(nulTerminatedPath, out errno);

  public override int Read(int handle, Span<byte> buffer, out int errno)
    => Native.Read(handle, buffer, out errno);

  public override void Close(int handle) => Native.Close(handle);

  public override int CountDirectoryEntries(scoped ReadOnlySpan<byte> nulTerminatedPath, Span<byte> scratch, out int errno)
    => Native.CountDirectoryEntries(nulTerminatedPath, scratch, out errno);

  public override bool ListNumericEntries(
    scoped ReadOnlySpan<byte> nulTerminatedPath,
    Span<byte> scratch,
    List<int> pids,
    int minimum = 1
  ) => Native.ListNumericEntries(nulTerminatedPath, scratch, pids, minimum);

  public override string? ReadLink(string path) => Native.ReadLink(path);

}

/// <summary>
/// The portable path, for replaying a recorded tree on a machine that has no <c>/proc</c>.
/// </summary>
/// <remarks>
/// Deliberately unoptimised — it opens a <see cref="FileStream"/> per file and allocates freely.
/// Nothing that runs against a real machine ever reaches it, so the only thing it has to be is
/// correct. Handles are small integers here too, so the reader above is identical either way.
/// </remarks>
internal sealed class ManagedProcIo : ProcIo {

  private readonly Dictionary<int, FileStream> _open = [];
  private int _nextHandle = 1;

  public override int OpenReadOnly(scoped ReadOnlySpan<byte> nulTerminatedPath, out int errno) {
    errno = 0;
    var path = Decode(nulTerminatedPath);
    try {
      var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
      var handle = this._nextHandle++;
      this._open[handle] = stream;
      return handle;
    } catch (UnauthorizedAccessException) {
      errno = Native.EACCES;
      return -1;
    } catch (IOException) {
      errno = Native.ENOENT;
      return -1;
    }
  }

  public override int Read(int handle, Span<byte> buffer, out int errno) {
    errno = 0;
    if (!this._open.TryGetValue(handle, out var stream)) {
      errno = Native.ENOENT;
      return -1;
    }

    try {
      return stream.Read(buffer);
    } catch (IOException) {
      errno = Native.ENOENT;
      return -1;
    }
  }

  public override void Close(int handle) {
    if (!this._open.Remove(handle, out var stream))
      return;

    stream.Dispose();
  }

  public override int CountDirectoryEntries(scoped ReadOnlySpan<byte> nulTerminatedPath, Span<byte> scratch, out int errno) {
    errno = 0;
    var path = Decode(nulTerminatedPath);
    try {
      var count = 0;
      foreach (var _ in Directory.EnumerateFileSystemEntries(path))
        ++count;

      return count;
    } catch (UnauthorizedAccessException) {
      errno = Native.EACCES;
      return -1;
    } catch (IOException) {
      errno = Native.ENOENT;
      return -1;
    } catch (ArgumentException) {
      errno = Native.ENOENT;
      return -1;
    }
  }

  /// <remarks>
  /// Every entry, not only the directories. <c>getdents64</c> does not filter by type and neither
  /// may this, or the two paths answer differently — which they did: the pids under <c>/proc</c> are
  /// directories, but the descriptors under <c>/proc/[pid]/fdinfo</c> are files, so the portable
  /// path found none of them and the graphics figures were empty on every leg but Linux while the
  /// parser behind them was perfectly sound.
  /// </remarks>
  public override bool ListNumericEntries(
    scoped ReadOnlySpan<byte> nulTerminatedPath,
    Span<byte> scratch,
    List<int> pids,
    int minimum = 1
  ) {
    var path = Decode(nulTerminatedPath);
    try {
      foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        if (int.TryParse(Path.GetFileName(entry), out var pid) && pid >= minimum)
          pids.Add(pid);

      return true;
    } catch (IOException) {
      return false;
    } catch (UnauthorizedAccessException) {
      return false;
    }
  }

  public override string? ReadLink(string path) {
    try {
      return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
    } catch (IOException) {
      return null;
    } catch (UnauthorizedAccessException) {
      return null;
    }
  }

}
