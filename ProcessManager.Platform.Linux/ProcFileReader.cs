namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Reads a <c>/proc</c> file into a reusable buffer, through <c>open</c>/<c>read</c>/<c>close</c>
/// directly.
/// </summary>
/// <remarks>
/// <para>
/// Files under <c>/proc</c> report a length of zero and are generated on read, so the usual
/// "stat it, allocate that, read it" does not work — the loop below reads until the kernel stops
/// producing, growing the buffer only when a file genuinely needs more.
/// </para>
/// <para>
/// The managed file APIs were the first implementation and were replaced on measurement, not on
/// principle: <c>File.OpenHandle</c> allocates a <c>SafeFileHandle</c> per call, which is four
/// thousand objects a second at a thousand processes, and a refused read arrives as an exception,
/// which on a shared machine is several hundred of those a second as well. Both showed up as most
/// of the sampling cost. Down here a refusal is an <c>errno</c> and an open is an integer
/// (PRD §4).
/// </para>
/// <para>
/// One instance per sampling thread, reused across every file and every sample.
/// </para>
/// </remarks>
internal sealed class ProcFileReader(ProcIo? io = null) {

  private readonly ProcIo _io = io ?? ProcIo.ForCurrentPlatform;
  private byte[] _buffer = new byte[16 * 1024];

  /// <summary>
  /// Reads a NUL-terminated path whole.
  /// </summary>
  /// <param name="path">The path, including its terminating NUL (see <see cref="ProcPath"/>).</param>
  /// <param name="content">The bytes read, valid until the next call on this instance.</param>
  /// <param name="errno">
  /// 0 on success. <see cref="Native.EACCES"/>/<see cref="Native.EPERM"/> mean the caller may not
  /// read this — reported as <see cref="Model.UnknownReason.NotPermitted"/>. Anything else usually
  /// means the process exited between the directory listing and this call, which is normal.
  /// </param>
  public bool TryRead(scoped ReadOnlySpan<byte> path, out ReadOnlySpan<byte> content, out int errno)
    => this.Read(path, out content, out errno, toEnd: false);

  /// <summary>Convenience for the detail queries, which are not on the sampling path.</summary>
  public bool TryRead(string path, out ReadOnlySpan<byte> content, out int errno) {
    Span<byte> buffer = stackalloc byte[ProcPath.MaxLength];
    return this.TryRead(ProcPath.FromString(buffer, path), out content, out errno);
  }

  /// <summary>
  /// Reads a file that is bigger than one page: the machine-wide tables, and a process's memory map.
  /// </summary>
  /// <remarks>
  /// <see cref="TryRead"/> treats a short read as end of file, which is what makes each per-process
  /// file cost a single syscall. That holds for a file whose content is a few hundred bytes and is
  /// wrong for a large one: a <c>seq_file</c> fills its own page-sized buffer per call, so a 16 KiB
  /// read of a 142 kB <c>/proc/net/unix</c> hands back 4090 bytes and reports no error at all.
  /// Reading one of those through <see cref="TryRead"/> loses everything past the first page in the
  /// worst possible way — a truncated table is indistinguishable from a short one, so the answer is
  /// wrong rather than missing. <c>/proc/[pid]/maps</c> is the same shape: 67 kB of it came back as
  /// 14 of 242 modules.
  /// <para>
  /// It costs one extra syscall to see the zero-length read that really is the end, which is why
  /// this is a separate method rather than a change to the one the sampler calls per process.
  /// </para>
  /// </remarks>
  public bool TryReadWhole(string path, out ReadOnlySpan<byte> content, out int errno) {
    Span<byte> buffer = stackalloc byte[ProcPath.MaxLength];
    return this.Read(ProcPath.FromString(buffer, path), out content, out errno, toEnd: true);
  }

  private bool Read(scoped ReadOnlySpan<byte> path, out ReadOnlySpan<byte> content, out int errno, bool toEnd) {
    content = default;
    var fd = this._io.OpenReadOnly(path, out errno);
    if (fd < 0)
      return false;

    try {
      var written = 0;
      while (true) {
        if (written == this._buffer.Length)
          Array.Resize(ref this._buffer, this._buffer.Length * 2);

        var room = this._buffer.Length - written;
        var read = this._io.Read(fd, this._buffer.AsSpan(written, room), out errno);
        if (read < 0)
          // The permission check on several /proc files happens at read(2), not at open(2): the
          // kernel only runs ptrace_may_access when it generates the content. /proc/[pid]/io of
          // another user's process opens cleanly and then refuses to produce a byte.
          return false;

        written += read;
        if (read == 0)
          break;

        if (!toEnd && read < room)
          // A short read from a seq_file is end of file — for the single-record files of the sampling
          // path, which is the only place this shortcut is taken. See TryReadWhole for the files it
          // is not true of. Skipping the confirming read is one syscall saved per file, three per
          // process, per sample. A file that exactly fills the buffer still gets the extra pass,
          // because that is the one case where a short read has not been seen yet.
          break;
      }

      errno = 0;
      content = this._buffer.AsSpan(0, written);
      return true;
    } finally {
      this._io.Close(fd);
    }
  }

  /// <summary>Reads the target of a symlink, or null. Used for <c>exe</c>, <c>cwd</c> and each fd.</summary>
  public string? TryReadLink(string path) => this._io.ReadLink(path);

}
