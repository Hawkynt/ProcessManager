using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a mapped file that is not an ELF actually is, from its own header (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ElfImage"/> stops at the four bytes that say "not an ELF" and reports the file as
/// data, which is right about the format and wrong about the process: a .NET program maps every
/// assembly it loads, and every one of them landed in the modules view under the same word as a
/// font. This reads the next few bytes and says which of the three things a non-ELF mapping
/// usually is — a managed assembly, a Windows binary under Wine, or a class-path archive.
/// </para>
/// <para>
/// By header and never by extension. <c>.dll</c> names a managed assembly and a Windows library
/// both, <c>.so</c> is occasionally a linker script, and a Wine process has files of every kind
/// mapped at once — so the name is exactly the evidence that cannot settle it (PRD §5.3).
/// </para>
/// <para>
/// No platform attribute and no file access: the reads are the caller's, through
/// <see cref="ElfImage.ElfRead"/>, so this runs on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ImageFormat {

  /// <summary>Where a PE file records the offset of its own PE header.</summary>
  private const int _PeOffsetAt = 0x3C;

  /// <summary>
  /// The COFF header, the optional header's magic, and enough of the data directory to reach the
  /// fifteenth entry. Anything shorter than this is not a PE whatever its first two bytes say.
  /// </summary>
  private const int _PeHeaderBytes = 0x120;

  /// <summary>The two optional-header shapes, which differ in where the data directory starts.</summary>
  private const ushort _Pe32 = 0x10B;

  private const ushort _Pe32Plus = 0x20B;

  /// <summary>
  /// Data directory 14, counting from nought: the CLI header, and the whole of what makes a PE a
  /// managed assembly rather than a Windows binary.
  /// </summary>
  private const int _CliDirectory = 14;

  /// <summary>
  /// A PE header further into the file than this is not one. Real ones are under a kilobyte in;
  /// the field is a full 32 bits and a corrupt file will happily name a gigabyte.
  /// </summary>
  private const int _MaxPeOffset = 1 << 20;

  /// <summary>
  /// Names the runtime a mapped file belongs to.
  /// </summary>
  /// <param name="read">The same reader <see cref="ElfImage"/> was given.</param>
  /// <param name="header">
  /// The first sixty-four bytes, already read. Every signature this looks for is in them or is
  /// reached from them, so the common answer costs no read at all.
  /// </param>
  public static ModuleRuntime Identify(ElfImage.ElfRead read, ReadOnlySpan<byte> header) {
    ArgumentNullException.ThrowIfNull(read);

    if (header.Length >= 4 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
      return ModuleRuntime.Native;

    // "PK\x03\x04" is a local file header, which is what a jar written in one pass begins with.
    // The other two spellings are an empty archive and one that has been split; neither is what a
    // JVM has open, and looking for them costs a comparison.
    if (header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K' && header[2] == 3 && header[3] == 4)
      return ModuleRuntime.Archive;

    return header.Length >= 0x40 && header[0] == (byte)'M' && header[1] == (byte)'Z'
      ? ReadPortableExecutable(read, header)
      : ModuleRuntime.NotCode;
  }

  /// <summary>
  /// Whether a PE carries a CLI header, which is what separates an assembly from a Windows binary.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The <c>MZ</c> at the front is only the DOS stub and says nothing: every PE has one, and so does
  /// a plain DOS executable that has been sitting on a disk since 1990. The offset at 0x3C is where
  /// the real header is, and a file whose first two bytes are <c>MZ</c> and whose header is not
  /// there is not a PE — which is the case that must come back as data rather than as a Windows
  /// binary nobody could describe.
  /// </para>
  /// <para>
  /// The data directory is at a different offset in the two optional-header shapes because
  /// <c>ImageBase</c> is four bytes wider in the 64-bit one. Reading the 32-bit offset out of a
  /// 64-bit image lands in the middle of the size fields, where a non-zero word is common — so
  /// getting this wrong reports half the Windows system libraries as managed assemblies.
  /// </para>
  /// </remarks>
  private static ModuleRuntime ReadPortableExecutable(ElfImage.ElfRead read, ReadOnlySpan<byte> header) {
    var peAt = BinaryPrimitives.ReadUInt32LittleEndian(header[_PeOffsetAt..]);
    if (peAt is 0 or > _MaxPeOffset)
      return ModuleRuntime.NotCode;

    var headers = new byte[_PeHeaderBytes];
    var got = read(peAt, headers);
    if (got < 0x18)
      return ModuleRuntime.NotCode;

    var pe = headers.AsSpan(0, got);
    if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
      return ModuleRuntime.NotCode;

    // Past the four signature bytes and the twenty of the COFF header. A PE this far in is a PE
    // whether or not its optional header can be read, so every answer below is one of the two
    // Windows ones and never "not code".
    var optional = pe[0x18..];
    if (optional.Length < 2)
      return ModuleRuntime.WindowsNative;

    var directoryAt = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch {
      _Pe32 => 0x60,
      _Pe32Plus => 0x70,
      _ => -1,
    };

    if (directoryAt < 0)
      return ModuleRuntime.WindowsNative;

    var entryAt = directoryAt + (_CliDirectory * 8);
    if (optional.Length < entryAt + 8)
      return ModuleRuntime.WindowsNative;

    // Both halves, because a directory entry with an address and no size describes nothing. A file
    // that names a CLI header of zero bytes has no metadata for a runtime to read, and calling it
    // managed would put a row in the list that no runtime can load.
    var address = BinaryPrimitives.ReadUInt32LittleEndian(optional[entryAt..]);
    var size = BinaryPrimitives.ReadUInt32LittleEndian(optional[(entryAt + 4)..]);
    return address != 0 && size != 0 ? ModuleRuntime.Managed : ModuleRuntime.WindowsNative;
  }

}
