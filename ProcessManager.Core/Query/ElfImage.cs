using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Reads exactly enough of an ELF file to say what it is: its class, its machine, its entry point,
/// the <c>SONAME</c> it publishes and the interpreter it asks for (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not "load the file and parse it". A process maps a couple of hundred images and the
/// biggest of them is thirty megabytes, so this reads four small ranges instead — the header, the
/// program headers, the dynamic section, and the one string in the string table that <c>DT_SONAME</c>
/// points at. Everything else in the file is skipped without ever being paged in.
/// </para>
/// <para>
/// The file access is the caller's, through <see cref="ElfRead"/>. That is what puts this in Core with
/// no platform attribute — a delegate over a byte array is as good an ELF file as a descriptor is, so
/// the parser is exercised on every CI leg (PRD §9.2) instead of only on the one that has ELF files.
/// </para>
/// </remarks>
public static class ElfImage {

  /// <summary>
  /// Reads at an absolute offset, and returns how much it got — a <c>pread</c>, in other words.
  /// </summary>
  /// <remarks>
  /// A short read is not an error: the last range this asks for runs past the end of the string table
  /// on purpose, because the string it wants is NUL-terminated and its length is not written anywhere.
  /// </remarks>
  public delegate int ElfRead(long offset, Span<byte> buffer);

  /// <summary>What the header of a mapped image says about it.</summary>
  public readonly record struct Description(
    ModuleType Type,
    string? Architecture,
    Counter EntryPoint,
    string? Soname,
    string? Interpreter
  );

  /// <summary>Nothing was read, and every field says so rather than saying zero (PRD §72.3).</summary>
  public static readonly Description Unread = new(
    ModuleType.Unknown,
    null,
    Counter.NotSampledYet,
    null,
    null
  );

  private const int _Ei_Class = 4;
  private const int _Ei_Data = 5;
  private const int _Pt_Load = 1;
  private const int _Pt_Dynamic = 2;
  private const int _Pt_Interp = 3;
  private const int _Dt_Null = 0;
  private const int _Dt_StrTab = 5;
  private const int _Dt_SoName = 14;

  /// <summary>The most program headers any real image has is about a dozen; this is the sanity cap.</summary>
  private const int _MaxProgramHeaders = 128;

  /// <summary>A dynamic section with more entries than this is not one we are going to make sense of.</summary>
  private const int _MaxDynamicEntries = 4096;

  /// <summary>The longest SONAME or interpreter path worth reading. Real ones are under sixty bytes.</summary>
  private const int _MaxNameLength = 512;

  /// <summary>
  /// Describes an image.
  /// </summary>
  /// <returns>
  /// False when nothing could be read at all. A file that reads but is not an ELF image comes back
  /// true, as <see cref="ModuleType.Data"/> — a mapped locale archive or font is a real answer, and
  /// reporting it as unreadable would be a lie about the permissions.
  /// </returns>
  public static bool TryDescribe(ElfRead read, out Description description) {
    description = Unread;

    Span<byte> header = stackalloc byte[64];
    if (read(0, header) < header.Length)
      return false;

    if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F') {
      // Readable and not an image: a locale archive, a font, a managed assembly. It has no entry
      // point in the sense this asks about, which is a different answer from "not read yet".
      description = Unread with { Type = ModuleType.Data, EntryPoint = Counter.NotSupported };
      return true;
    }

    var is64 = header[_Ei_Class] == 2;
    var isLittleEndian = header[_Ei_Data] != 2;
    var type = ReadUInt16(header[16..], isLittleEndian) switch {
      1 => ModuleType.Relocatable,
      2 => ModuleType.Executable,
      3 => ModuleType.SharedObject,
      4 => ModuleType.CoreDump,
      _ => ModuleType.Unknown,
    };

    var architecture = DescribeMachine(ReadUInt16(header[18..], isLittleEndian), is64);
    var entry = is64
      ? ReadUInt64(header[24..], isLittleEndian)
      : ReadUInt32(header[24..], isLittleEndian);

    var programHeaderOffset = is64
      ? (long)ReadUInt64(header[32..], isLittleEndian)
      : ReadUInt32(header[28..], isLittleEndian);
    var programHeaderSize = ReadUInt16(header[(is64 ? 54 : 42)..], isLittleEndian);
    var programHeaderCount = ReadUInt16(header[(is64 ? 56 : 44)..], isLittleEndian);

    // A library that declares no entry point writes zero, and zero is an address — reporting it would
    // put the load base in the column once the bias is added, which reads as a real answer. Saying
    // the image has none is the truth (PRD §72.3).
    description = new(type, architecture, entry == 0 ? Counter.NotSupported : Counter.Of(entry), null, null);
    if (programHeaderOffset <= 0 || programHeaderSize < (is64 ? 56 : 32) || programHeaderCount is 0 or > _MaxProgramHeaders)
      return true;

    var table = new byte[programHeaderSize * programHeaderCount];
    if (read(programHeaderOffset, table) < table.Length)
      return true;

    // Two passes' worth of information from one walk: where the dynamic section is, where the
    // interpreter string is, and the load segments needed to turn a virtual address into a file
    // offset. DT_STRTAB is given as an address, and without the load segments there is no way back.
    long dynamicOffset = 0, dynamicSize = 0, interpreterOffset = 0, interpreterSize = 0;
    var loads = new List<(ulong VirtualAddress, ulong Size, long FileOffset)>(programHeaderCount);
    for (var i = 0; i < programHeaderCount; ++i) {
      var entryBytes = table.AsSpan(i * programHeaderSize, programHeaderSize);
      var segmentType = ReadUInt32(entryBytes, isLittleEndian);
      var fileOffset = is64
        ? (long)ReadUInt64(entryBytes[8..], isLittleEndian)
        : ReadUInt32(entryBytes[4..], isLittleEndian);
      var virtualAddress = is64
        ? ReadUInt64(entryBytes[16..], isLittleEndian)
        : ReadUInt32(entryBytes[8..], isLittleEndian);
      var fileSize = is64
        ? (long)ReadUInt64(entryBytes[32..], isLittleEndian)
        : ReadUInt32(entryBytes[16..], isLittleEndian);

      switch (segmentType) {
        case _Pt_Load:
          loads.Add((virtualAddress, (ulong)fileSize, fileOffset));
          break;
        case _Pt_Dynamic:
          dynamicOffset = fileOffset;
          dynamicSize = fileSize;
          break;
        case _Pt_Interp:
          interpreterOffset = fileOffset;
          interpreterSize = fileSize;
          break;
        default:
          break;
      }
    }

    description = description with {
      Interpreter = ReadString(read, interpreterOffset, (int)Math.Min(interpreterSize, _MaxNameLength)),
      Soname = ReadSoname(read, is64, isLittleEndian, dynamicOffset, dynamicSize, loads),
    };

    return true;
  }

  /// <summary>
  /// The <c>DT_SONAME</c> string: the name a library says other binaries should link against, which
  /// is not always the name of the file it is in.
  /// </summary>
  private static string? ReadSoname(
    ElfRead read,
    bool is64,
    bool isLittleEndian,
    long dynamicOffset,
    long dynamicSize,
    List<(ulong VirtualAddress, ulong Size, long FileOffset)> loads
  ) {
    var entrySize = is64 ? 16 : 8;
    if (dynamicOffset <= 0 || dynamicSize < entrySize || dynamicSize / entrySize > _MaxDynamicEntries)
      return null;

    var section = new byte[dynamicSize];
    if (read(dynamicOffset, section) < section.Length)
      return null;

    ulong stringTable = 0, nameOffset = 0;
    var haveStringTable = false;
    var haveName = false;
    for (var offset = 0; offset + entrySize <= section.Length; offset += entrySize) {
      var span = section.AsSpan(offset);
      var tag = is64 ? (long)ReadUInt64(span, isLittleEndian) : (int)ReadUInt32(span, isLittleEndian);
      var value = is64 ? ReadUInt64(span[8..], isLittleEndian) : ReadUInt32(span[4..], isLittleEndian);
      if (tag == _Dt_Null)
        break;

      if (tag == _Dt_StrTab) {
        stringTable = value;
        haveStringTable = true;
      } else if (tag == _Dt_SoName) {
        nameOffset = value;
        haveName = true;
      }
    }

    if (!haveStringTable || !haveName)
      return null;

    foreach (var load in loads) {
      var address = stringTable + nameOffset;
      if (address < load.VirtualAddress || address >= load.VirtualAddress + load.Size)
        continue;

      return ReadString(read, load.FileOffset + (long)(address - load.VirtualAddress), _MaxNameLength);
    }

    return null;
  }

  /// <summary>A NUL-terminated string at a file offset, or null when there is nothing there.</summary>
  private static string? ReadString(ElfRead read, long offset, int maximum) {
    if (offset <= 0 || maximum <= 0)
      return null;

    var buffer = new byte[maximum];
    var got = read(offset, buffer);
    if (got <= 0)
      return null;

    var text = buffer.AsSpan(0, got);
    var nul = text.IndexOf((byte)0);
    if (nul >= 0)
      text = text[..nul];

    return text.IsEmpty ? null : Encoding.UTF8.GetString(text);
  }

  /// <summary>
  /// <c>e_machine</c> as a name people use.
  /// </summary>
  /// <remarks>
  /// A machine that is not in the list is reported as its number rather than as nothing: "the header
  /// says 187" is a true statement about the file, and an empty cell would say instead that we were
  /// not allowed to look (PRD §72.3).
  /// </remarks>
  private static string DescribeMachine(ushort machine, bool is64) => machine switch {
    2 => "sparc",
    3 => "x86",
    8 => is64 ? "mips64" : "mips",
    20 => "ppc",
    21 => "ppc64",
    22 => "s390x",
    40 => "arm",
    62 => "x86-64",
    183 => "aarch64",
    243 => is64 ? "riscv64" : "riscv32",
    258 => is64 ? "loongarch64" : "loongarch32",
    _ => string.Create(CultureInfo.InvariantCulture, $"machine {machine}"),
  };

  private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool isLittleEndian) => isLittleEndian
    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt16BigEndian(bytes);

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool isLittleEndian) => isLittleEndian
    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt32BigEndian(bytes);

  private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, bool isLittleEndian) => isLittleEndian
    ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt64BigEndian(bytes);

}
