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
  /// <param name="Needed">
  /// The <c>SONAME</c>s of the libraries this image links against, in the order it names them. Empty
  /// for an image that links against nothing and for one whose dynamic section was not read — the
  /// two are told apart by <paramref name="Mitigations"/>, which carries
  /// <see cref="ImageMitigations.Read"/> only in the first case.
  /// </param>
  public readonly record struct Description(
    ModuleType Type,
    string? Architecture,
    Counter EntryPoint,
    string? Soname,
    string? Interpreter,
    ImageMitigations Mitigations,
    string? BuildId,
    IReadOnlyList<string> Needed
  );

  /// <summary>Nothing was read, and every field says so rather than saying zero (PRD §72.3).</summary>
  public static readonly Description Unread = new(
    ModuleType.Unknown,
    null,
    Counter.NotSampledYet,
    null,
    null,
    ImageMitigations.None,
    null,
    []
  );

  private const int _Ei_Class = 4;
  private const int _Ei_Data = 5;
  private const int _Pt_Load = 1;
  private const int _Pt_Dynamic = 2;
  private const int _Pt_Interp = 3;
  private const int _Pt_Note = 4;

  /// <summary>The three <c>PT_GNU_*</c> segments, which are how a toolchain records hardening.</summary>
  private const uint _Pt_GnuStack = 0x6474E551;
  private const uint _Pt_GnuRelro = 0x6474E552;
  private const uint _Pt_GnuProperty = 0x6474E553;

  private const int _Dt_Null = 0;
  private const int _Dt_Needed = 1;
  private const int _Dt_StrTab = 5;
  private const int _Dt_SoName = 14;
  private const int _Dt_BindNow = 24;
  private const int _Dt_Flags = 30;
  private const long _Dt_Flags1 = 0x6FFFFFFB;
  private const ulong _Df_BindNow = 0x8;
  private const ulong _Df1_Now = 0x1;

  /// <summary>The note types this reads: the build identity, and the processor feature list.</summary>
  private const uint _Nt_GnuBuildId = 3;
  private const uint _Nt_GnuPropertyType0 = 5;

  private const uint _GnuProperty_X86Feature1 = 0xC0000002;
  private const uint _GnuProperty_Aarch64Feature1 = 0xC0000000;

  /// <summary>The most program headers any real image has is about a dozen; this is the sanity cap.</summary>
  private const int _MaxProgramHeaders = 128;

  /// <summary>A dynamic section with more entries than this is not one we are going to make sense of.</summary>
  private const int _MaxDynamicEntries = 4096;

  /// <summary>The longest SONAME or interpreter path worth reading. Real ones are under sixty bytes.</summary>
  private const int _MaxNameLength = 512;

  /// <summary>
  /// A note segment longer than this is not one this reads. The two notes worth having — the build
  /// identity and the processor feature list — are a hundred bytes between them.
  /// </summary>
  private const int _MaxNoteBytes = 8 * 1024;

  /// <summary>An image linking against more libraries than this is not one worth graphing.</summary>
  private const int _MaxNeeded = 256;

  /// <summary>How many note segments are read. Three is what a current toolchain emits.</summary>
  private const int _MaxNoteSegments = 8;

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
    description = new(
      type,
      architecture,
      entry == 0 ? Counter.NotSupported : Counter.Of(entry),
      null,
      null,
      // Still None: the header alone says nothing about hardening, and the program headers below are
      // where every one of those flags is written.
      ImageMitigations.None,
      null,
      []
    );

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
    var notes = new List<(long Offset, int Size)>(_MaxNoteSegments);

    // The headers were read, so from here on the absence of a flag is a statement about the image
    // rather than about how far this got (PRD §72.3). An ET_DYN image is the one the kernel is free
    // to place where it likes, which is what makes randomisation possible at all.
    var mitigations = ImageMitigations.Read;
    if (type == ModuleType.SharedObject)
      mitigations |= ImageMitigations.PositionIndependent;

    for (var i = 0; i < programHeaderCount; ++i) {
      var entryBytes = table.AsSpan(i * programHeaderSize, programHeaderSize);
      var segmentType = ReadUInt32(entryBytes, isLittleEndian);
      // p_flags sits immediately after the type on a 64-bit image and at the very end of the entry
      // on a 32-bit one, which is the only field in this structure the two classes disagree about
      // the order of.
      var segmentFlags = ReadUInt32(entryBytes[(is64 ? 4 : 24)..], isLittleEndian);
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
        case _Pt_Note or _Pt_GnuProperty:
          // The property note is reachable both ways — it is inside a PT_NOTE segment and named
          // again by PT_GNU_PROPERTY — so both are collected and the note walk sorts out which
          // note is which. Reading only the second would miss a linker that emits only the first.
          if (notes.Count < _MaxNoteSegments && fileSize is > 0 and <= _MaxNoteBytes)
            notes.Add((fileOffset, (int)fileSize));

          break;
        case _Pt_GnuStack:
          // The execute bit here is the whole question. A segment that is not there at all leaves
          // the decision to the ABI, and neither flag is set: "this image does not say" is not the
          // same claim as either answer.
          mitigations |= (segmentFlags & 1) != 0
            ? ImageMitigations.ExecutableStack
            : ImageMitigations.NonExecutableStack;
          break;
        case _Pt_GnuRelro:
          mitigations |= ImageMitigations.Relro;
          break;
        default:
          break;
      }
    }

    var dynamic = ReadDynamic(read, is64, isLittleEndian, dynamicOffset, dynamicSize, loads);
    description = description with {
      Interpreter = ReadString(read, interpreterOffset, (int)Math.Min(interpreterSize, _MaxNameLength)),
      Soname = dynamic.Soname,
      Needed = dynamic.Needed,
      Mitigations = mitigations | dynamic.Mitigations | ReadNotes(read, is64, isLittleEndian, notes, out var buildId),
      BuildId = buildId,
    };

    return true;
  }

  /// <summary>What the dynamic section says: the name it publishes, the names it needs, how it binds.</summary>
  private readonly record struct Dynamic(string? Soname, IReadOnlyList<string> Needed, ImageMitigations Mitigations);

  /// <summary>
  /// Walks <c>PT_DYNAMIC</c> once for everything §31 wants out of it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>DT_SONAME</c> is the name a library says other binaries should link against, which is not
  /// always the name of the file it is in; <c>DT_NEEDED</c> is the same kind of name, pointing the
  /// other way, and is what makes a load reason derivable at all. Both are offsets into
  /// <c>DT_STRTAB</c>, which is given as an address — so the load segments are needed to get back to
  /// a file offset, and one walk collects the tags before any string is read.
  /// </para>
  /// <para>
  /// Binding is asked three ways because it is written three ways: the ancient <c>DT_BIND_NOW</c>,
  /// the <c>DF_BIND_NOW</c> bit of <c>DT_FLAGS</c>, and <c>DF_1_NOW</c> in <c>DT_FLAGS_1</c>, which
  /// is what a current linker emits. Reading only one of them reports full RELRO as partial on half
  /// the binaries on the machine.
  /// </para>
  /// </remarks>
  private static Dynamic ReadDynamic(
    ElfRead read,
    bool is64,
    bool isLittleEndian,
    long dynamicOffset,
    long dynamicSize,
    List<(ulong VirtualAddress, ulong Size, long FileOffset)> loads
  ) {
    var entrySize = is64 ? 16 : 8;
    if (dynamicOffset <= 0 || dynamicSize < entrySize || dynamicSize / entrySize > _MaxDynamicEntries)
      return new(null, [], ImageMitigations.None);

    var section = new byte[dynamicSize];
    if (read(dynamicOffset, section) < section.Length)
      return new(null, [], ImageMitigations.None);

    ulong stringTable = 0, nameOffset = 0;
    var haveStringTable = false;
    var haveName = false;
    var mitigations = ImageMitigations.None;
    var neededOffsets = new List<ulong>();
    for (var offset = 0; offset + entrySize <= section.Length; offset += entrySize) {
      var span = section.AsSpan(offset);
      var tag = is64 ? (long)ReadUInt64(span, isLittleEndian) : (int)ReadUInt32(span, isLittleEndian);
      var value = is64 ? ReadUInt64(span[8..], isLittleEndian) : ReadUInt32(span[4..], isLittleEndian);
      if (tag == _Dt_Null)
        break;

      switch (tag) {
        case _Dt_StrTab:
          stringTable = value;
          haveStringTable = true;
          break;
        case _Dt_SoName:
          nameOffset = value;
          haveName = true;
          break;
        case _Dt_Needed:
          if (neededOffsets.Count < _MaxNeeded)
            neededOffsets.Add(value);

          break;
        case _Dt_BindNow:
          mitigations |= ImageMitigations.BindNow;
          break;
        case _Dt_Flags when (value & _Df_BindNow) != 0:
        case _Dt_Flags1 when (value & _Df1_Now) != 0:
          mitigations |= ImageMitigations.BindNow;
          break;
        default:
          break;
      }
    }

    if (!haveStringTable)
      return new(null, [], mitigations);

    var needed = new List<string>(neededOffsets.Count);
    foreach (var entry in neededOffsets)
      if (StringAt(stringTable + entry) is { } name)
        needed.Add(name);

    return new(haveName ? StringAt(stringTable + nameOffset) : null, needed, mitigations);

    // The string at an address in the string table, found by whichever load segment covers it.
    string? StringAt(ulong address) {
      foreach (var load in loads) {
        if (address < load.VirtualAddress || address >= load.VirtualAddress + load.Size)
          continue;

        return ReadString(read, load.FileOffset + (long)(address - load.VirtualAddress), _MaxNameLength);
      }

      return null;
    }
  }

  /// <summary>
  /// The two notes worth reading: which build this is, and which processor features it was built for.
  /// </summary>
  /// <remarks>
  /// A note segment is a sequence of records — a name length, a description length, a type, then the
  /// name and the description each padded up to four bytes. The build identity is a <c>GNU</c> note
  /// of type 3, and the feature list a <c>GNU</c> note of type 5 whose description is itself a list
  /// of properties. Nothing else in either segment is read past its length field.
  /// </remarks>
  private static ImageMitigations ReadNotes(
    ElfRead read,
    bool is64,
    bool isLittleEndian,
    List<(long Offset, int Size)> segments,
    out string? buildId
  ) {
    buildId = null;
    var mitigations = ImageMitigations.None;
    foreach (var (offset, size) in segments) {
      var segment = new byte[size];
      var got = read(offset, segment);
      if (got <= 0)
        continue;

      var notes = segment.AsSpan(0, got);
      for (var at = 0; at + 12 <= notes.Length;) {
        var nameSize = (int)ReadUInt32(notes[at..], isLittleEndian);
        var descriptionSize = (int)ReadUInt32(notes[(at + 4)..], isLittleEndian);
        var type = ReadUInt32(notes[(at + 8)..], isLittleEndian);
        var nameAt = at + 12;
        var descriptionAt = nameAt + Align(nameSize, 4);
        var next = descriptionAt + Align(descriptionSize, 4);
        if (nameSize < 0 || descriptionSize < 0 || next <= at || next > notes.Length)
          break;

        at = next;
        // Every note this reads is the GNU vendor's. Another vendor may use the same type numbers
        // for something else entirely, which is exactly what the name field is for.
        if (nameSize != 4 || !notes.Slice(nameAt, 4).SequenceEqual("GNU\0"u8))
          continue;

        var description = notes.Slice(descriptionAt, descriptionSize);
        if (type == _Nt_GnuBuildId && descriptionSize > 0)
          buildId ??= Convert.ToHexStringLower(description);
        else if (type == _Nt_GnuPropertyType0)
          mitigations |= ReadProperties(description, is64, isLittleEndian);
      }
    }

    return mitigations;
  }

  /// <summary>
  /// The processor features a <c>NT_GNU_PROPERTY_TYPE_0</c> note declares.
  /// </summary>
  /// <remarks>
  /// Each property is a type, a length and its data, padded to the size of an address — eight bytes
  /// in a 64-bit image, four in a 32-bit one, which is the one thing about this structure that is
  /// not the same everywhere. The two properties read here are the "AND" feature words: a bit is set
  /// only when every object linked into the image had it set, which is what makes them a statement
  /// about the whole binary rather than about one of its files.
  /// </remarks>
  private static ImageMitigations ReadProperties(ReadOnlySpan<byte> properties, bool is64, bool isLittleEndian) {
    var alignment = is64 ? 8 : 4;
    var result = ImageMitigations.None;
    for (var at = 0; at + 8 <= properties.Length;) {
      var type = ReadUInt32(properties[at..], isLittleEndian);
      var size = (int)ReadUInt32(properties[(at + 4)..], isLittleEndian);
      var dataAt = at + 8;
      var next = dataAt + Align(size, alignment);
      if (size < 0 || next <= at || dataAt + size > properties.Length)
        break;

      at = next;
      if (size < 4)
        continue;

      var features = ReadUInt32(properties[dataAt..], isLittleEndian);
      switch (type) {
        case _GnuProperty_X86Feature1:
          if ((features & 1) != 0)
            result |= ImageMitigations.IndirectBranchTracking;
          if ((features & 2) != 0)
            result |= ImageMitigations.ShadowStack;

          break;
        case _GnuProperty_Aarch64Feature1:
          if ((features & 1) != 0)
            result |= ImageMitigations.BranchTargetIdentification;
          if ((features & 2) != 0)
            result |= ImageMitigations.PointerAuthentication;

          break;
        default:
          break;
      }
    }

    return result;
  }

  private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

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
