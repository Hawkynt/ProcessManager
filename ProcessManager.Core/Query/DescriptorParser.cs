using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a Unix file descriptor is and what it was opened with, from the two things
/// <c>/proc/[pid]</c> says about it: the target of <c>fd/[n]</c> and the contents of
/// <c>fdinfo/[n]</c> (PRD §32).
/// </summary>
/// <remarks>
/// <para>
/// A descriptor's kind is written into the name of what it points at, because most of what a process
/// holds open is not a file: <c>socket:[10999155]</c>, <c>pipe:[10999154]</c>,
/// <c>anon_inode:[eventfd]</c>. The number in brackets is the inode, and for a socket it is the join
/// key that turns "holds a socket" into "holds this connection" (PRD §40).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class DescriptorParser {

  /// <summary>Everything <c>fdinfo</c> reports that is not specific to one kind of descriptor.</summary>
  public readonly record struct DescriptorInfo(
    Counter Position,
    Counter OpenFlags,
    Counter MountId,
    Counter Inode,
    Counter TargetPid
  );

  /// <summary>Nothing was read: every field says so rather than saying zero (PRD §72.3).</summary>
  public static readonly DescriptorInfo Unread = new(
    Counter.NotSampledYet,
    Counter.NotSampledYet,
    Counter.NotSampledYet,
    Counter.NotSampledYet,
    Counter.NotSampledYet
  );

  /// <summary>Refused: the descriptor exists, this user may not look inside it.</summary>
  public static readonly DescriptorInfo Refused = new(
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted
  );

  /// <summary>
  /// The descriptor came back from the privileged helper, whose protocol carries only its name.
  /// </summary>
  /// <remarks>
  /// Deliberately not <see cref="Refused"/>: the helper is running and it may read these, so telling
  /// the reader they need more privilege would be the one piece of advice that cannot help. This says
  /// what is true — the round trip does not carry them yet (PRD §7).
  /// </remarks>
  public static readonly DescriptorInfo NotRelayed = new(
    Counter.Unknown(UnknownReason.NotImplementedHere),
    Counter.Unknown(UnknownReason.NotImplementedHere),
    Counter.Unknown(UnknownReason.NotImplementedHere),
    Counter.Unknown(UnknownReason.NotImplementedHere),
    Counter.Unknown(UnknownReason.NotImplementedHere)
  );

  /// <summary>
  /// Parses one <c>/proc/[pid]/fdinfo/[fd]</c>.
  /// </summary>
  /// <remarks>
  /// The four common lines are <c>pos</c>, <c>flags</c>, <c>mnt_id</c> and <c>ino</c>; anything after
  /// them describes one kind of descriptor only — <c>eventfd-count</c>, <c>clockid</c>, the
  /// <c>tfd:</c> list of an epoll set. <c>Pid:</c> is the exception worth reading: it is how a pidfd
  /// names the process it holds, which is the only way §32's "target process" is answerable here.
  /// A field the kernel did not write stays unknown, because <c>pos: 0</c> and "this kind of
  /// descriptor has no position" are different statements.
  /// </remarks>
  public static DescriptorInfo ParseFdInfo(ReadOnlySpan<byte> content) {
    var position = Counter.NotSupported;
    var flags = Counter.NotSupported;
    var mountId = Counter.NotSupported;
    var inode = Counter.NotSupported;
    var targetPid = Counter.NotSupported;

    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (line.IsEmpty)
        continue;

      if (TryValue(line, "pos:"u8, out var value))
        position = Counter.Of(value);
      else if (TryOctal(line, "flags:"u8, out value))
        flags = Counter.Of(value);
      else if (TryValue(line, "mnt_id:"u8, out value))
        mountId = Counter.Of(value);
      else if (TryValue(line, "ino:"u8, out value))
        inode = Counter.Of(value);
      else if (TryValue(line, "Pid:"u8, out value))
        targetPid = Counter.Of(value);
    }

    return new(position, flags, mountId, inode, targetPid);
  }

  /// <summary>
  /// What kind of thing a descriptor points at, from the target of its symlink.
  /// </summary>
  /// <param name="target">The link target, or null when it could not be read.</param>
  /// <param name="openFlags">
  /// The open flags, which are what distinguishes a directory from a file: both are plain paths, and
  /// only <c>O_DIRECTORY</c> says which. Pass <see cref="Counter.NotSampledYet"/> when they are not
  /// available and a directory will read as a file rather than as a guess.
  /// </param>
  public static HandleKind Classify(string? target, Counter openFlags) {
    if (target is null)
      return HandleKind.Unknown;

    if (target.StartsWith("socket:", StringComparison.Ordinal))
      return HandleKind.Socket;

    if (target.StartsWith("pipe:", StringComparison.Ordinal))
      return HandleKind.Pipe;

    if (target.StartsWith("anon_inode:", StringComparison.Ordinal))
      return ClassifyAnonInode(target.AsSpan("anon_inode:".Length));

    if (!target.StartsWith('/'))
      return HandleKind.Unknown;

    // A memfd has no name in any directory — the leading slash is the kernel's, not a path's — and it
    // exists to be handed to another process, so it is shared memory rather than a file that happens
    // to have been deleted.
    if (target.StartsWith("/memfd:", StringComparison.Ordinal) || target.StartsWith("/dev/shm/", StringComparison.Ordinal))
      return HandleKind.SharedMemory;

    if (openFlags.TryGetValue(out var flags) && (flags & OpenFlagValues.Directory) != 0)
      return HandleKind.Directory;

    return target.StartsWith("/dev/", StringComparison.Ordinal) ? HandleKind.Device : HandleKind.File;
  }

  /// <summary>
  /// The names the kernel gives its anonymous inodes, which are the closest thing Linux has to
  /// Windows' object types (PRD §5.3).
  /// </summary>
  /// <remarks>
  /// Written with brackets by most subsystems and without by inotify, which is a kernel inconsistency
  /// rather than a distinction — so the brackets are stripped before the comparison instead of both
  /// spellings being listed.
  /// </remarks>
  private static HandleKind ClassifyAnonInode(ReadOnlySpan<char> name) {
    if (name.Length >= 2 && name[0] == '[' && name[^1] == ']')
      name = name[1..^1];

    if (name.SequenceEqual("eventfd"))
      return HandleKind.Event;
    if (name.SequenceEqual("eventpoll"))
      return HandleKind.EventPoll;
    if (name.SequenceEqual("timerfd"))
      return HandleKind.Timer;
    if (name.SequenceEqual("signalfd"))
      return HandleKind.Signal;
    if (name.SequenceEqual("inotify") || name.SequenceEqual("fanotify"))
      return HandleKind.Notify;
    if (name.SequenceEqual("pidfd"))
      return HandleKind.Process;

    return HandleKind.AnonInode;
  }

  /// <summary>
  /// The inode in <c>socket:[10999155]</c> or <c>pipe:[10999154]</c>.
  /// </summary>
  /// <remarks>
  /// <c>fdinfo</c>'s own <c>ino:</c> line says the same thing on a current kernel, but not on one old
  /// enough to predate it — and the bracketed number has been there since <c>/proc</c> had an fd
  /// directory at all. Reading both means the socket join of §40 does not stop working on an older
  /// machine.
  /// </remarks>
  public static bool TryParsePseudoInode(string? target, out ulong inode) {
    inode = 0;
    if (target is null)
      return false;

    var open = target.IndexOf('[', StringComparison.Ordinal);
    var close = target.IndexOf(']', StringComparison.Ordinal);
    return open >= 0
      && close > open + 1
      && ulong.TryParse(target.AsSpan(open + 1, close - open - 1), out inode);
  }

  /// <summary>
  /// What the holder may do with the descriptor: <c>r</c>, <c>w</c> or <c>rw</c>.
  /// </summary>
  /// <remarks>
  /// This is the whole of §32's "access rights" on Unix. Unlike a Windows access mask it is not a set
  /// of independent bits but a three-valued field in the low two bits of the open flags, and the
  /// fourth value is not an access mode at all — <c>O_PATH</c> descriptors report it and may be used
  /// for neither reading nor writing.
  /// </remarks>
  public static string? DescribeAccess(Counter openFlags) {
    if (!openFlags.TryGetValue(out var flags))
      return null;

    return (flags & OpenFlagValues.AccessMode) switch {
      0 => "r",
      1 => "w",
      2 => "rw",
      _ => "path",
    };
  }

  /// <summary>
  /// The open flags spelled out — <c>O_APPEND|O_NONBLOCK|O_CLOEXEC</c> — or null when unknown.
  /// </summary>
  /// <remarks>
  /// The access mode is left out on purpose: it is not a flag, it has its own column, and printing it
  /// twice makes the reader wonder which one is authoritative. Bits this does not recognise are shown
  /// as a hex remainder rather than dropped, because a flag nobody named is still a flag that is set.
  /// </remarks>
  public static string? DescribeFlags(Counter openFlags) {
    if (!openFlags.TryGetValue(out var flags))
      return null;

    var rest = flags & ~OpenFlagValues.AccessMode;
    if (rest == 0)
      return string.Empty;

    var text = new StringBuilder();
    foreach (var (bit, name) in OpenFlagValues.Named) {
      if ((rest & bit) != bit)
        continue;

      rest &= ~bit;
      if (text.Length > 0)
        text.Append('|');

      text.Append(name);
    }

    if (rest == 0)
      return text.ToString();

    if (text.Length > 0)
      text.Append('|');

    return text.Append("0x").Append(rest.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).ToString();
  }

  private static bool TryValue(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong value) {
    value = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    var scanner = new AsciiScanner(line[key.Length..]);
    value = scanner.NextUInt64();
    return true;
  }

  private static bool TryOctal(ReadOnlySpan<byte> line, ReadOnlySpan<byte> key, out ulong value) {
    value = 0;
    if (!AsciiScanner.StartsWith(line, key))
      return false;

    var scanner = new AsciiScanner(line[key.Length..]);
    value = AsciiScanner.ParseOctal(scanner.NextField());
    return true;
  }

}

/// <summary>
/// The <c>O_*</c> constants, as the kernel defines them for the architectures this program runs on.
/// </summary>
/// <remarks>
/// <para>
/// <c>fcntl.h</c> writes them in octal and <c>fdinfo</c> prints them in octal; C# has no octal
/// literal, so each is given in hex with its octal spelling beside it — <c>0100000</c> and
/// <c>0x8000</c> are the same bit, and only one of them can be compared with the file by eye.
/// </para>
/// <para>
/// A handful differ on architectures nobody builds this for — alpha, sparc and hppa each move a bit
/// or two — which is why an unrecognised remainder is printed rather than assumed to be nothing.
/// </para>
/// </remarks>
internal static class OpenFlagValues {

  public const ulong AccessMode = 0x3;          // 0003
  public const ulong Directory = 0x10000;       // 0200000

  /// <summary>
  /// Most specific first: <c>O_SYNC</c> and <c>O_TMPFILE</c> are each defined as a bit <em>plus</em>
  /// another flag, so matching them after their halves would name the half and print the rest as an
  /// unknown remainder.
  /// </summary>
  public static readonly (ulong Bit, string Name)[] Named = [
    (0x410000, "O_TMPFILE"),                    // 020000000 | O_DIRECTORY
    (0x101000, "O_SYNC"),                       // 04000000 | O_DSYNC
    (0x40, "O_CREAT"),                          // 0100
    (0x80, "O_EXCL"),                           // 0200
    (0x100, "O_NOCTTY"),                        // 0400
    (0x200, "O_TRUNC"),                         // 01000
    (0x400, "O_APPEND"),                        // 02000
    (0x800, "O_NONBLOCK"),                      // 04000
    (0x1000, "O_DSYNC"),                        // 010000
    (0x2000, "O_ASYNC"),                        // 020000
    (0x4000, "O_DIRECT"),                       // 040000
    (0x8000, "O_LARGEFILE"),                    // 0100000
    (0x10000, "O_DIRECTORY"),                   // 0200000
    (0x20000, "O_NOFOLLOW"),                    // 0400000
    (0x40000, "O_NOATIME"),                     // 01000000
    (0x80000, "O_CLOEXEC"),                     // 02000000
    (0x200000, "O_PATH"),                       // 010000000
  ];

}
