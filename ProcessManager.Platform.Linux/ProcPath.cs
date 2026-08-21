using System.Text;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Builds NUL-terminated <c>/proc</c> paths into a caller-supplied buffer.
/// </summary>
/// <remarks>
/// The sampling loop composes four or five paths per process. As <see cref="string"/> that is four
/// thousand allocations a second at a thousand processes, and the string then has to be marshalled
/// back to UTF-8 bytes for the syscall anyway. Writing the bytes straight into a stack buffer skips
/// both (PRD §4).
/// </remarks>
internal static class ProcPath {

  /// <summary>Enough for any <c>/proc/&lt;pid&gt;/&lt;leaf&gt;</c> with room to spare.</summary>
  public const int MaxLength = 256;

  /// <summary>
  /// Writes <c>&lt;root&gt;/&lt;pid&gt;/&lt;leaf&gt;\0</c> and returns the span including the NUL,
  /// which is what the syscall wants.
  /// </summary>
  public static Span<byte> Build(Span<byte> buffer, ReadOnlySpan<byte> root, int pid, ReadOnlySpan<byte> leaf) {
    var written = 0;
    root.CopyTo(buffer);
    written += root.Length;
    buffer[written++] = (byte)'/';
    written += WriteInt32(buffer[written..], pid);
    if (!leaf.IsEmpty) {
      buffer[written++] = (byte)'/';
      leaf.CopyTo(buffer[written..]);
      written += leaf.Length;
    }

    buffer[written++] = 0;
    return buffer[..written];
  }

  /// <summary>Writes <c>&lt;root&gt;/&lt;leaf&gt;\0</c>, for the machine-wide files.</summary>
  public static Span<byte> Build(Span<byte> buffer, ReadOnlySpan<byte> root, ReadOnlySpan<byte> leaf) {
    var written = 0;
    root.CopyTo(buffer);
    written += root.Length;
    buffer[written++] = (byte)'/';
    leaf.CopyTo(buffer[written..]);
    written += leaf.Length;
    buffer[written++] = 0;
    return buffer[..written];
  }

  /// <summary>
  /// Writes <c>&lt;directory&gt;/&lt;number&gt;\0</c> for the numbered files inside a directory.
  /// </summary>
  /// <remarks>
  /// <paramref name="directory"/> is a path this class built, so it already ends in the NUL that the
  /// number has to be written over. For <c>/proc/[pid]/fdinfo/[fd]</c>, where the directory is the
  /// same for every descriptor of a process and only the last component moves.
  /// </remarks>
  public static Span<byte> Build(Span<byte> buffer, ReadOnlySpan<byte> directory, int number) {
    if (!directory.IsEmpty && directory[^1] == 0)
      directory = directory[..^1];

    directory.CopyTo(buffer);
    var written = directory.Length;
    buffer[written++] = (byte)'/';
    written += WriteInt32(buffer[written..], number);
    buffer[written++] = 0;
    return buffer[..written];
  }

  private static int WriteInt32(Span<byte> buffer, int value) {
    if (value == 0) {
      buffer[0] = (byte)'0';
      return 1;
    }

    Span<byte> digits = stackalloc byte[10];
    var count = 0;
    for (var rest = value; rest > 0; rest /= 10)
      digits[count++] = (byte)('0' + rest % 10);

    for (var i = 0; i < count; ++i)
      buffer[i] = digits[count - 1 - i];

    return count;
  }

  /// <summary>UTF-8 bytes of a path given as a string, for the non-hot detail queries.</summary>
  public static Span<byte> FromString(Span<byte> buffer, string path) {
    var written = Encoding.UTF8.GetBytes(path, buffer);
    buffer[written++] = 0;
    return buffer[..written];
  }

}
