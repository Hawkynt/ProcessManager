using System.Text;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The per-process things that do not change, kept for as long as the process lives.
/// </summary>
/// <remarks>
/// <para>
/// Two costs are avoided here. The first is the file paths: composing
/// <c>/proc/1234/status</c> four times per process per sample is four thousand buffers a second at a
/// thousand processes, so each is built once, as UTF-8 bytes ready for the syscall. The second is
/// the contents that never change — the command line, the image path, the cgroup — which are read
/// once per process rather than once per sample.
/// </para>
/// <para>
/// The name is the interesting case: <c>comm</c> <em>can</em> change (a process may rename itself),
/// so it is re-read every sample, but the bytes are compared against the cached string and a new one
/// is allocated only when they actually differ. Correct and free in the same move (PRD §4).
/// </para>
/// </remarks>
internal sealed class ProcessCache {

  private static ReadOnlySpan<byte> _status => "status"u8;
  private static ReadOnlySpan<byte> _io => "io"u8;
  private static ReadOnlySpan<byte> _fd => "fd"u8;
  private static ReadOnlySpan<byte> _smapsRollup => "smaps_rollup"u8;
  private static ReadOnlySpan<byte> _attrCurrent => "attr/current"u8;

  private byte[] _nameBytes = [];
  private int _nameLength;
  private bool _staticsLoaded;

  public ProcessCache(ReadOnlySpan<byte> procRoot, int pid) {
    this.StatusPath = BuildPath(procRoot, pid, _status);
    this.IoPath = BuildPath(procRoot, pid, _io);
    this.FdPath = BuildPath(procRoot, pid, _fd);
    this.SmapsRollupPath = BuildPath(procRoot, pid, _smapsRollup);
    this.SecurityContextPath = BuildPath(procRoot, pid, _attrCurrent);
    this.Pid = pid;
  }

  public int Pid { get; }

  /// <summary>NUL-terminated UTF-8, ready to hand to <c>open(2)</c>.</summary>
  public byte[] StatusPath { get; }
  public byte[] IoPath { get; }
  public byte[] FdPath { get; }
  public byte[] SmapsRollupPath { get; }
  public byte[] SecurityContextPath { get; }

  public string Name { get; private set; } = string.Empty;
  public string? CommandLine { get; private set; }
  public string? ImagePath { get; private set; }
  public string? ContainerPath { get; private set; }

  /// <summary>The sample number this entry was last seen in; older entries are pruned.</summary>
  public int Generation { get; set; }

  private static byte[] BuildPath(ReadOnlySpan<byte> procRoot, int pid, ReadOnlySpan<byte> leaf) {
    Span<byte> buffer = stackalloc byte[ProcPath.MaxLength];
    return ProcPath.Build(buffer, procRoot, pid, leaf).ToArray();
  }

  /// <summary>
  /// Extracts <c>comm</c> from a <c>stat</c> file and returns the cached string when it is unchanged.
  /// </summary>
  public string UpdateName(ReadOnlySpan<byte> statContent) {
    var open = statContent.IndexOf((byte)'(');
    var close = statContent.LastIndexOf((byte)')');
    if (open < 0 || close <= open)
      return this.Name;

    var comm = statContent[(open + 1)..close];
    if (comm.Length == this._nameLength && comm.SequenceEqual(this._nameBytes.AsSpan(0, this._nameLength)))
      return this.Name;

    if (this._nameBytes.Length < comm.Length)
      this._nameBytes = new byte[comm.Length];

    comm.CopyTo(this._nameBytes);
    this._nameLength = comm.Length;
    this.Name = Encoding.UTF8.GetString(comm);
    return this.Name;
  }

  /// <summary>Reads the things that are read once: command line, image path, cgroup.</summary>
  public void EnsureStatics(ProcFileReader reader, LinuxProbeOptions options, ReadOnlySpan<byte> procRoot, string procRootText) {
    if (this._staticsLoaded)
      return;

    this._staticsLoaded = true;

    Span<byte> buffer = stackalloc byte[ProcPath.MaxLength];
    var cmdlinePath = ProcPath.Build(buffer, procRoot, this.Pid, "cmdline"u8);
    if (reader.TryRead(cmdlinePath, out var cmdline, out _) && !cmdline.IsEmpty)
      this.CommandLine = DecodeCommandLine(cmdline);

    this.ImagePath = reader.TryReadLink($"{procRootText}/{this.Pid}/exe");

    if (!options.ReadCgroups)
      return;

    Span<byte> cgroupBuffer = stackalloc byte[ProcPath.MaxLength];
    var cgroupPath = ProcPath.Build(cgroupBuffer, procRoot, this.Pid, "cgroup"u8);
    if (!reader.TryRead(cgroupPath, out var cgroup, out _) || cgroup.IsEmpty)
      return;

    // cgroup v2 writes exactly one line, "0::/path". v1 writes several, and the one worth showing is
    // whichever names a container; taking the first line's path is right for v2 and a reasonable
    // answer for v1, and the column is documented as the cgroup path rather than as a container name.
    var scanner = new AsciiScanner(cgroup);
    var line = scanner.NextLine();
    var lastColon = line.LastIndexOf((byte)':');
    if (lastColon >= 0 && lastColon + 1 < line.Length)
      this.ContainerPath = Encoding.UTF8.GetString(line[(lastColon + 1)..]);
  }

  /// <summary>
  /// <c>cmdline</c> is NUL-separated with a trailing NUL. Joining with spaces is what every tool
  /// shows; an argument that itself contains a space is therefore indistinguishable, which is a
  /// property of the display and not of the data — the argument vector is intact in the detail view.
  /// </summary>
  private static string DecodeCommandLine(ReadOnlySpan<byte> content) {
    while (!content.IsEmpty && content[^1] == 0)
      content = content[..^1];

    if (content.IsEmpty)
      return string.Empty;

    Span<byte> copy = content.Length <= 512 ? stackalloc byte[content.Length] : new byte[content.Length];
    content.CopyTo(copy);
    for (var i = 0; i < copy.Length; ++i)
      if (copy[i] == 0)
        copy[i] = (byte)' ';

    return Encoding.UTF8.GetString(copy);
  }

}
