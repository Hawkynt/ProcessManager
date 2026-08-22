using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Everything §53 asks about an ELF file, read from the file and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The other ELF readers in this folder each answer one question and stop: <see cref="ElfHeader"/>
/// says what a program was built for, <see cref="ElfImage"/> reads the four ranges a modules list
/// needs, <see cref="ElfSymbols"/> turns one address into one name. This is the one that walks the
/// whole structure, because that is what an inspector is — and it is deliberately separate from
/// them, so that opening it can never slow down the sampler that runs every second (PRD §5.4).
/// </para>
/// <para>
/// <b>A viewer and not a debugger.</b> It reads a file on disk; it does not attach to anything, does
/// not read another process's memory and does not disassemble. That is the line §4 draws and §25.5
/// records the argument for.
/// </para>
/// <para>
/// The file access is the caller's, through <see cref="ElfImage.ElfRead"/>, which is what keeps this
/// in Core with no platform attribute and exercised on every CI leg against bytes a test wrote
/// rather than only on the leg that has ELF files (PRD §9.2).
/// </para>
/// <para>
/// Every offset in the file is somebody else's arithmetic and is bounds-checked before it is used.
/// A section header table that names a gigabyte, a string table index past the end, a symbol table
/// whose entry size is nought — all of those are shapes a real, corrupt, or deliberately hostile
/// file has, and none of them may throw out of a viewer.
/// </para>
/// </remarks>
internal sealed class ElfInspector {

  #region shapes

  private readonly record struct Segment(
    uint Type,
    uint Flags,
    long Offset,
    ulong VirtualAddress,
    ulong PhysicalAddress,
    long FileSize,
    ulong MemorySize,
    ulong Align
  );

  private readonly record struct Section(
    uint NameOffset,
    uint Type,
    ulong Flags,
    ulong Address,
    long Offset,
    long Size,
    uint Link,
    uint Info,
    ulong Align,
    ulong EntrySize,
    string Name
  );

  private readonly record struct Symbol(
    string Name,
    ulong Value,
    ulong Size,
    byte Info,
    byte Other,
    ushort SectionIndex,
    string? Version,
    string? Library
  );

  #endregion

  #region constants

  private const int _MaxSections = 4096;
  private const int _MaxSegments = 512;

  /// <summary>A string table larger than this is not one this reads whole.</summary>
  private const int _MaxStringTableBytes = 16 * 1024 * 1024;

  /// <summary>How many symbols are walked. Beyond this the page says it was truncated.</summary>
  private const int _MaxSymbols = 200_000;

  /// <summary>How many rows any one page shows. The rest are counted rather than listed.</summary>
  private const int _MaxRows = 20_000;

  private const uint _ShtProgBits = 1;
  private const uint _ShtSymTab = 2;
  private const uint _ShtStrTab = 3;
  private const uint _ShtRela = 4;
  private const uint _ShtDynamic = 6;
  private const uint _ShtNote = 7;
  private const uint _ShtNoBits = 8;
  private const uint _ShtRel = 9;
  private const uint _ShtDynSym = 11;
  private const uint _ShtRelr = 19;
  private const uint _ShtGnuVerDef = 0x6FFFFFFD;
  private const uint _ShtGnuVerNeed = 0x6FFFFFFE;
  private const uint _ShtGnuVerSym = 0x6FFFFFFF;

  private const ushort _ShnUndef = 0;
  private const ushort _ShnAbs = 0xFFF1;
  private const ushort _ShnCommon = 0xFFF2;

  private const int _DtNull = 0;
  private const int _DtNeeded = 1;
  private const int _DtStrTab = 5;
  private const int _DtSoName = 14;
  private const int _DtRPath = 15;
  private const int _DtRunPath = 29;

  #endregion

  private readonly ElfImage.ElfRead _read;
  private readonly byte[] _header;
  private readonly bool _is64;
  private readonly bool _little;
  private readonly Segment[] _segments;
  private readonly Section[] _sections;

  /// <summary>The dynamic string table, which is where every name a loader uses lives.</summary>
  private byte[]? _dynamicStrings;

  private bool _readDynamicStrings;
  private List<(long Tag, ulong Value)>? _dynamic;

  public long Length { get; }

  private ElfInspector(ElfImage.ElfRead read, long length, byte[] header, bool is64, bool little) {
    this._read = read;
    this.Length = length;
    this._header = header;
    this._is64 = is64;
    this._little = little;
    this._segments = this.ReadSegments();
    this._sections = this.ReadSections();
  }

  /// <summary>Reads the header, or hands back null when these bytes are not an ELF file.</summary>
  public static ElfInspector? TryOpen(ElfImage.ElfRead read, long length) {
    ArgumentNullException.ThrowIfNull(read);

    var header = new byte[64];
    if (read(0, header) < 64)
      return null;

    if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
      return null;

    var klass = header[4];
    if (klass is not (1 or 2))
      return null;

    return new(read, length, header, klass == 2, header[5] != 2);
  }

  #region the tables

  private Segment[] ReadSegments() {
    var offset = this._is64 ? (long)U64(this._header, 32) : U32(this._header, 28);
    var size = U16(this._header, this._is64 ? 54 : 42);
    var count = U16(this._header, this._is64 ? 56 : 44);
    var minimum = this._is64 ? 56 : 32;
    if (offset <= 0 || size < minimum || count is 0 or > _MaxSegments)
      return [];

    var table = new byte[size * count];
    if (this._read(offset, table) < table.Length)
      return [];

    var found = new Segment[count];
    for (var i = 0; i < count; ++i) {
      var at = i * size;
      found[i] = this._is64
        ? new(
          U32(table, at),
          U32(table, at + 4),
          (long)U64(table, at + 8),
          U64(table, at + 16),
          U64(table, at + 24),
          (long)U64(table, at + 32),
          U64(table, at + 40),
          U64(table, at + 48)
        )
        : new(
          U32(table, at),
          // p_flags is the last word of a 32-bit entry and the second of a 64-bit one. This is the
          // only field of the structure whose position the two classes disagree about, and reading
          // it at the 64-bit place on a 32-bit image reports the file offset as the permissions.
          U32(table, at + 24),
          U32(table, at + 4),
          U32(table, at + 8),
          U32(table, at + 12),
          U32(table, at + 16),
          U32(table, at + 20),
          U32(table, at + 28)
        );
    }

    return found;
  }

  private Section[] ReadSections() {
    var offset = this._is64 ? (long)U64(this._header, 0x28) : U32(this._header, 0x20);
    var size = U16(this._header, this._is64 ? 0x3A : 0x2E);
    var count = U16(this._header, this._is64 ? 0x3C : 0x30);
    var minimum = this._is64 ? 64 : 40;
    if (offset <= 0 || size < minimum || count is 0 or > _MaxSections)
      return [];

    var table = new byte[size * count];
    if (this._read(offset, table) < table.Length)
      return [];

    var raw = new Section[count];
    for (var i = 0; i < count; ++i) {
      var at = i * size;
      raw[i] = this._is64
        ? new(
          U32(table, at),
          U32(table, at + 4),
          U64(table, at + 8),
          U64(table, at + 16),
          (long)U64(table, at + 24),
          (long)U64(table, at + 32),
          U32(table, at + 40),
          U32(table, at + 44),
          U64(table, at + 48),
          U64(table, at + 56),
          string.Empty
        )
        : new(
          U32(table, at),
          U32(table, at + 4),
          U32(table, at + 8),
          U32(table, at + 12),
          U32(table, at + 16),
          U32(table, at + 20),
          U32(table, at + 24),
          U32(table, at + 28),
          U32(table, at + 32),
          U32(table, at + 36),
          string.Empty
        );
    }

    // The names come out of one of the sections that were just read, which is why this is a second
    // pass: the index of the table is in the header and the table itself is a section like any other.
    var nameIndex = U16(this._header, this._is64 ? 0x3E : 0x32);
    var names = nameIndex < raw.Length ? this.ReadWhole(raw[nameIndex].Offset, raw[nameIndex].Size) : null;
    if (names is null)
      return raw;

    for (var i = 0; i < raw.Length; ++i)
      raw[i] = raw[i] with { Name = StringAt(names, raw[i].NameOffset) ?? string.Empty };

    return raw;
  }

  /// <summary>A whole range of the file, or null when it is missing, empty or absurd.</summary>
  private byte[]? ReadWhole(long offset, long size) {
    if (offset <= 0 || size <= 0 || size > _MaxStringTableBytes)
      return null;

    if (this.Length > 0 && offset >= this.Length)
      return null;

    var bytes = new byte[size];
    var got = this._read(offset, bytes);
    if (got <= 0)
      return null;

    return got == bytes.Length ? bytes : bytes[..got];
  }

  private static string? StringAt(byte[]? table, ulong offset) {
    if (table is null || offset >= (ulong)table.Length)
      return null;

    var span = table.AsSpan((int)offset);
    var nul = span.IndexOf((byte)0);
    if (nul >= 0)
      span = span[..nul];

    return span.IsEmpty ? null : Encoding.UTF8.GetString(span);
  }

  private byte[]? DynamicStrings {
    get {
      if (this._readDynamicStrings)
        return this._dynamicStrings;

      this._readDynamicStrings = true;
      // The section table is the reliable way in when it survived. A stripped-of-sections image
      // still has DT_STRTAB, which is an address rather than an offset — that is what the segment
      // walk below is for, and it is the fallback rather than the rule because a section says its
      // own size and the dynamic entry does not always.
      foreach (var section in this._sections)
        if (section is { Type: _ShtStrTab, Name: ".dynstr" })
          return this._dynamicStrings = this.ReadWhole(section.Offset, section.Size);

      ulong address = 0, size = 0;
      foreach (var (tag, value) in this.Dynamic)
        switch (tag) {
          case _DtStrTab: address = value; break;
          case 10: size = value; break;
          default: break;
        }

      if (address == 0 || size == 0)
        return null;

      return this._dynamicStrings = this.FileOffsetOf(address) is { } at ? this.ReadWhole(at, (long)size) : null;
    }
  }

  /// <summary>Turns an address in the loaded image into an offset in the file, through PT_LOAD.</summary>
  private long? FileOffsetOf(ulong address) {
    foreach (var segment in this._segments) {
      if (segment.Type != 1 || segment.FileSize <= 0)
        continue;

      if (address < segment.VirtualAddress || address >= segment.VirtualAddress + (ulong)segment.FileSize)
        continue;

      return segment.Offset + (long)(address - segment.VirtualAddress);
    }

    return null;
  }

  private List<(long Tag, ulong Value)> Dynamic {
    get {
      if (this._dynamic is not null)
        return this._dynamic;

      this._dynamic = [];
      long offset = 0, size = 0;
      foreach (var section in this._sections)
        if (section.Type == _ShtDynamic) {
          offset = section.Offset;
          size = section.Size;
        }

      if (offset <= 0)
        foreach (var segment in this._segments)
          if (segment.Type == 2) {
            offset = segment.Offset;
            size = segment.FileSize;
          }

      var entrySize = this._is64 ? 16 : 8;
      if (offset <= 0 || size < entrySize)
        return this._dynamic;

      var bytes = this.ReadWhole(offset, size);
      if (bytes is null)
        return this._dynamic;

      for (var at = 0; at + entrySize <= bytes.Length; at += entrySize) {
        var tag = this._is64 ? (long)U64(bytes, at) : (int)U32(bytes, at);
        var value = this._is64 ? U64(bytes, at + 8) : U32(bytes, at + 4);
        this._dynamic.Add((tag, value));
        if (tag == _DtNull)
          break;
      }

      return this._dynamic;
    }
  }

  #endregion

  #region symbols

  /// <summary>
  /// Reads one symbol table, with the versions the dynamic one carries beside it.
  /// </summary>
  /// <remarks>
  /// The version is not decoration. ELF records nowhere which library an undefined symbol will come
  /// from — that is the loader's business at run time — except through the version needs, which name
  /// the file each version was declared in. So <c>memcpy@GLIBC_2.14</c> is the only place a file says
  /// where its imports are expected from, and dropping it would make the imports page a list of
  /// names with no provenance at all.
  /// </remarks>
  private List<Symbol> ReadSymbols(uint wantedType, out bool truncated) {
    truncated = false;
    var found = new List<Symbol>();
    var entrySize = this._is64 ? 24 : 16;
    for (var i = 0; i < this._sections.Length; ++i) {
      var section = this._sections[i];
      if (section.Type != wantedType || section.Offset <= 0 || section.Size < entrySize)
        continue;

      if ((int)section.EntrySize != entrySize || section.Link >= (uint)this._sections.Length)
        continue;

      var strings = this.ReadWhole(this._sections[section.Link].Offset, this._sections[section.Link].Size);
      var versions = wantedType == _ShtDynSym ? this.ReadVersions(i) : null;
      var count = section.Size / entrySize;
      if (count > _MaxSymbols) {
        count = _MaxSymbols;
        truncated = true;
      }

      var bytes = this.ReadWhole(section.Offset, count * entrySize);
      if (bytes is null)
        continue;

      for (var at = 0; at + entrySize <= bytes.Length; at += entrySize) {
        var index = at / entrySize;
        var name = StringAt(strings, U32(bytes, at));
        var value = this._is64 ? U64(bytes, at + 8) : U32(bytes, at + 4);
        var size = this._is64 ? U64(bytes, at + 16) : U32(bytes, at + 8);
        var info = this._is64 ? bytes[at + 4] : bytes[at + 12];
        var other = this._is64 ? bytes[at + 5] : bytes[at + 13];
        var shndx = U16(bytes, at + (this._is64 ? 6 : 14));
        var (version, library) = versions is not null && index < versions.Length ? versions[index] : (null, null);
        found.Add(new(name ?? string.Empty, value, size, info, other, shndx, version, library));
      }
    }

    return found;
  }

  /// <summary>
  /// The version and the library each dynamic symbol is filed under, indexed as the symbols are.
  /// </summary>
  /// <remarks>
  /// Three sections between them. <c>.gnu.version</c> is one sixteen-bit index per dynamic symbol;
  /// <c>.gnu.version_r</c> declares the versions this image needs and names the file each came from;
  /// <c>.gnu.version_d</c> declares the versions it defines itself. Index 0 is a local symbol and
  /// index 1 is the unversioned global namespace, and neither is a version anybody wrote.
  /// </remarks>
  private (string? Version, string? Library)[]? ReadVersions(int dynamicSymbolSection) {
    var strings = this.DynamicStrings;
    if (strings is null)
      return null;

    byte[]? symbolIndices = null;
    foreach (var section in this._sections)
      if (section.Type == _ShtGnuVerSym && section.Link == (uint)dynamicSymbolSection)
        symbolIndices = this.ReadWhole(section.Offset, section.Size);

    if (symbolIndices is null)
      return null;

    var names = new Dictionary<ushort, (string? Version, string? Library)>();
    foreach (var section in this._sections) {
      if (section.Type is not (_ShtGnuVerNeed or _ShtGnuVerDef))
        continue;

      var bytes = this.ReadWhole(section.Offset, section.Size);
      if (bytes is null)
        continue;

      if (section.Type == _ShtGnuVerNeed)
        ReadNeeded(bytes);
      else
        ReadDefined(bytes);
    }

    var result = new (string? Version, string? Library)[symbolIndices.Length / 2];
    for (var i = 0; i < result.Length; ++i) {
      var index = (ushort)(U16(symbolIndices, i * 2) & 0x7FFF);
      // 0 is a local symbol and 1 is the base, unversioned namespace. Reporting either as a version
      // would put "@0" beside every static symbol in the file.
      if (index > 1 && names.TryGetValue(index, out var pair))
        result[i] = pair;
    }

    return result;

    void ReadNeeded(byte[] bytes) {
      for (var at = 0; at + 16 <= bytes.Length;) {
        var count = U16(bytes, at + 2);
        var file = StringAt(strings, U32(bytes, at + 4));
        var aux = (int)U32(bytes, at + 8);
        var next = (int)U32(bytes, at + 12);
        for (var i = 0; i < count; ++i) {
          var entry = at + aux;
          if (aux <= 0 || entry + 16 > bytes.Length)
            break;

          var index = U16(bytes, entry + 6);
          names[index] = (StringAt(strings, U32(bytes, entry + 8)), file);
          var step = (int)U32(bytes, entry + 12);
          if (step <= 0)
            break;

          aux += step;
        }

        if (next <= 0)
          break;

        at += next;
      }
    }

    void ReadDefined(byte[] bytes) {
      for (var at = 0; at + 20 <= bytes.Length;) {
        var index = U16(bytes, at + 4);
        var aux = (int)U32(bytes, at + 12);
        var next = (int)U32(bytes, at + 16);
        // The first auxiliary entry of a version definition is the version's own name; any that
        // follow are the versions it inherits from, which is not what a column wants.
        if (aux > 0 && at + aux + 8 <= bytes.Length)
          names[index] = (StringAt(strings, U32(bytes, at + aux)), null);

        if (next <= 0)
          break;

        at += next;
      }
    }
  }

  #endregion

  #region pages

  public BinaryView View(BinaryPage page) => page switch {
    BinaryPage.Headers => this.HeadersPage(),
    BinaryPage.Segments => this.SegmentsPage(),
    BinaryPage.Sections => this.SectionsPage(),
    BinaryPage.Dynamic => this.DynamicPage(),
    BinaryPage.Dependencies => this.DependenciesPage(),
    BinaryPage.Imports => this.ImportsPage(),
    BinaryPage.Exports => this.ExportsPage(),
    BinaryPage.Symbols => this.SymbolsPage(),
    BinaryPage.Relocations => this.RelocationsPage(),
    BinaryPage.Resources => BinaryView.Empty(
      "Resources",
      "ELF has no resource section and never has had one. What a Windows file keeps in a version "
      + "resource, a Linux machine keeps in the database that installed the package — which is where "
      + "the modules view reads a version, a description and a packager from (§31)."
    ),
    BinaryPage.Signature => BinaryView.Empty(
      "Signature",
      "**Nothing signs an ELF.** The format has no signature record and no tool in the ordinary "
      + "toolchain adds one, so what a Linux machine can say about a file's provenance is what the "
      + "database that installed it says: the file against the digest its package recorded, and "
      + "whether that package's own signature was checked at install time. That is §70's question "
      + "and the file properties box is where it is asked, because it needs the package databases "
      + "and this page has only the bytes."
    ),
    BinaryPage.Debug => this.DebugPage(),
    BinaryPage.Security => this.SecurityPage(),
    _ => this.SummaryPage(),
  };

  private BinaryView SummaryPage() {
    ElfImage.TryDescribe(this._read, out var description);
    var dynamicSymbols = this.ReadSymbols(_ShtDynSym, out _);
    var staticSymbols = this.ReadSymbols(_ShtSymTab, out _);
    var rows = new List<string[]>();
    rows.Add(["format", "ELF"]);
    rows.Add(["class", this._is64 ? "64-bit" : "32-bit"]);
    rows.Add(["byte order", this._little ? "little endian" : "big endian"]);
    rows.Add(["os/abi", OsAbi(this._header[7])]);
    rows.Add(["type", TypeName(U16(this._header, 16))]);
    rows.Add(["machine", MachineName(U16(this._header, 18))]);
    rows.Add(["entry point", Hex(this._is64 ? U64(this._header, 24) : U32(this._header, 24))]);
    rows.Add(["file size", this.Length >= 0 ? Decimal(this.Length) + " bytes" : "unknown"]);
    rows.Add(["interpreter", description.Interpreter ?? "none — statically linked or a shared object"]);
    rows.Add(["soname", description.Soname ?? "none"]);
    rows.Add(["build id", description.BuildId ?? "none — built without --build-id"]);
    rows.Add(["segments", Decimal(this._segments.Length)]);
    rows.Add(["sections", this._sections.Length > 0 ? Decimal(this._sections.Length) : "none — the section table was stripped"]);
    rows.Add(["dependencies", Decimal(this.Needed().Count)]);
    rows.Add(["dynamic symbols", Decimal(dynamicSymbols.Count)]);
    rows.Add(["symbol table", staticSymbols.Count > 0 ? Decimal(staticSymbols.Count) + " symbols" : "stripped"]);
    rows.Add(["security", Humanize.Mitigations(description.Mitigations)]);

    return BinaryView.Facts("Summary", rows);
  }

  private BinaryView HeadersPage() {
    var rows = new List<string[]>();
    rows.Add(["magic", Convert.ToHexString(this._header.AsSpan(0, 16)).ToLowerInvariant()]);
    rows.Add(["class", this._header[4] == 2 ? "ELF64" : "ELF32"]);
    rows.Add(["data", this._little ? "2's complement, little endian" : "2's complement, big endian"]);
    rows.Add(["version", Decimal(this._header[6])]);
    rows.Add(["os/abi", OsAbi(this._header[7])]);
    rows.Add(["abi version", Decimal(this._header[8])]);
    rows.Add(["type", TypeName(U16(this._header, 16))]);
    rows.Add(["machine", MachineName(U16(this._header, 18))]);
    rows.Add(["object version", Hex(U32(this._header, 20))]);
    rows.Add(["entry point", Hex(this._is64 ? U64(this._header, 24) : U32(this._header, 24))]);
    rows.Add(["program headers at", Decimal(this._is64 ? (long)U64(this._header, 32) : U32(this._header, 28))]);
    rows.Add(["section headers at", Decimal(this._is64 ? (long)U64(this._header, 0x28) : U32(this._header, 0x20))]);
    rows.Add(["flags", Hex(U32(this._header, this._is64 ? 0x30 : 0x24))]);
    rows.Add(["header size", Decimal(U16(this._header, this._is64 ? 0x34 : 0x28))]);
    rows.Add(["program header size", Decimal(U16(this._header, this._is64 ? 0x36 : 0x2A))]);
    rows.Add(["program headers", Decimal(U16(this._header, this._is64 ? 0x38 : 0x2C))]);
    rows.Add(["section header size", Decimal(U16(this._header, this._is64 ? 0x3A : 0x2E))]);
    rows.Add(["section headers", Decimal(U16(this._header, this._is64 ? 0x3C : 0x30))]);
    rows.Add(["section names index", Decimal(U16(this._header, this._is64 ? 0x3E : 0x32))]);

    return BinaryView.Facts("ELF header", rows);
  }

  private BinaryView SegmentsPage() {
    var rows = new List<string[]>(this._segments.Length);
    foreach (var segment in this._segments)
      rows.Add([
        SegmentTypeName(segment.Type),
        Hex(segment.Offset),
        Hex(segment.VirtualAddress),
        Hex(segment.PhysicalAddress),
        Hex(segment.FileSize),
        Hex(segment.MemorySize),
        Permissions(segment.Flags),
        Hex(segment.Align),
      ]);

    return new(
      "Program headers",
      ["Type", "Offset", "Virtual", "Physical", "File size", "Memory size", "Flags", "Align"],
      rows,
      rows.Count == 0 ? "This file declares no program headers, which a relocatable object never does." : null
    );
  }

  private BinaryView SectionsPage() {
    var rows = new List<string[]>(this._sections.Length);
    for (var i = 0; i < this._sections.Length; ++i) {
      var section = this._sections[i];
      rows.Add([
        Decimal(i),
        section.Name.Length > 0 ? section.Name : "—",
        SectionTypeName(section.Type),
        Hex(section.Address),
        Hex(section.Offset),
        Hex(section.Size),
        Hex(section.EntrySize),
        SectionFlags(section.Flags),
        Decimal(section.Link),
        Decimal(section.Info),
        Decimal(section.Align),
      ]);
    }

    return new(
      "Section headers",
      ["#", "Name", "Type", "Address", "Offset", "Size", "Entry", "Flags", "Link", "Info", "Align"],
      rows,
      rows.Count == 0
        ? "There is no section header table. A loader never needs one, so an image can be shipped "
          + "without it and still run — everything a running program uses is in the program headers "
          + "and the dynamic section."
        : null
    );
  }

  private BinaryView DynamicPage() {
    var strings = this.DynamicStrings;
    var rows = new List<string[]>();
    foreach (var (tag, value) in this.Dynamic)
      rows.Add([
        Hex(tag),
        DynamicTagName(tag),
        tag switch {
          _DtNeeded or _DtSoName or _DtRPath or _DtRunPath => StringAt(strings, value) ?? Hex(value),
          _ => Hex(value),
        },
      ]);

    return new(
      "Dynamic section",
      ["Tag", "Type", "Value"],
      rows,
      rows.Count == 0
        ? "This file has no dynamic section, which is what a statically linked program and a "
          + "relocatable object both look like."
        : null
    );
  }

  private List<string> Needed() {
    var strings = this.DynamicStrings;
    var needed = new List<string>();
    foreach (var (tag, value) in this.Dynamic)
      if (tag == _DtNeeded && StringAt(strings, value) is { Length: > 0 } name)
        needed.Add(name);

    return needed;
  }

  private BinaryView DependenciesPage() {
    var strings = this.DynamicStrings;
    var rows = new List<string[]>();
    ElfImage.TryDescribe(this._read, out var description);
    if (description.Interpreter is { Length: > 0 } interpreter)
      rows.Add(["interpreter", interpreter]);

    foreach (var (tag, value) in this.Dynamic)
      switch (tag) {
        case _DtSoName when StringAt(strings, value) is { } soname:
          rows.Add(["soname", soname]);
          break;
        case _DtRPath when StringAt(strings, value) is { } rpath:
          rows.Add(["rpath", rpath]);
          break;
        case _DtRunPath when StringAt(strings, value) is { } runpath:
          rows.Add(["runpath", runpath]);
          break;
        default:
          break;
      }

    foreach (var name in this.Needed())
      rows.Add(["needed", name]);

    return BinaryView.Facts(
      "Dependencies",
      rows,
      "What the file itself names. It is not the set of libraries the process will end up with: "
      + "`LD_PRELOAD`, a `dlopen` at run time and every dependency of every dependency are all "
      + "invisible from here, and the modules view is where the answer for a *running* process is (§31)."
    );
  }

  private BinaryView ImportsPage() {
    var symbols = this.ReadSymbols(_ShtDynSym, out var truncated);
    var rows = new List<string[]>();
    foreach (var symbol in symbols) {
      if (symbol.SectionIndex != _ShnUndef || symbol.Name.Length == 0)
        continue;

      rows.Add([
        symbol.Name,
        SymbolType(symbol.Info),
        SymbolBinding(symbol.Info),
        symbol.Version ?? "—",
        symbol.Library ?? "—",
      ]);

      if (rows.Count >= _MaxRows) {
        truncated = true;
        break;
      }
    }

    return new(
      "Imports",
      ["Name", "Type", "Binding", "Version", "From"],
      rows,
      Note(
        truncated,
        rows.Count == 0
          ? "This file imports nothing, which is what a statically linked program looks like."
          : "The undefined symbols of the dynamic table: what the loader will have to find elsewhere. "
            + "\"From\" is the file the symbol's version was declared in, which is the only place an "
            + "ELF says where an import is expected to come from — a symbol with no version has no "
            + "such statement rather than no source."
      )
    );
  }

  private BinaryView ExportsPage() {
    var symbols = this.ReadSymbols(_ShtDynSym, out var truncated);
    var rows = new List<string[]>();
    foreach (var symbol in symbols) {
      if (symbol.SectionIndex == _ShnUndef || symbol.Name.Length == 0 || SymbolBinding(symbol.Info) == "local")
        continue;

      rows.Add([
        Hex(symbol.Value),
        Decimal(symbol.Size),
        SymbolType(symbol.Info),
        SymbolBinding(symbol.Info),
        SymbolVisibility(symbol.Other),
        symbol.Version ?? "—",
        symbol.Name,
      ]);

      if (rows.Count >= _MaxRows) {
        truncated = true;
        break;
      }
    }

    return new(
      "Exports",
      ["Value", "Size", "Type", "Binding", "Visibility", "Version", "Name"],
      rows,
      Note(
        truncated,
        rows.Count == 0
          ? "This file exports nothing through a dynamic symbol table — an executable that is not "
            + "also a library normally exports nothing at all."
          : null
      )
    );
  }

  private BinaryView SymbolsPage() {
    var symbols = this.ReadSymbols(_ShtSymTab, out var truncated);
    var stripped = symbols.Count == 0;
    if (stripped)
      symbols = this.ReadSymbols(_ShtDynSym, out truncated);

    var rows = new List<string[]>();
    foreach (var symbol in symbols) {
      rows.Add([
        Hex(symbol.Value),
        Decimal(symbol.Size),
        SymbolType(symbol.Info),
        SymbolBinding(symbol.Info),
        SymbolVisibility(symbol.Other),
        SectionOf(symbol.SectionIndex),
        symbol.Name.Length > 0 ? symbol.Name : "—",
      ]);

      if (rows.Count >= _MaxRows) {
        truncated = true;
        break;
      }
    }

    return new(
      "Symbols",
      ["Value", "Size", "Type", "Binding", "Visibility", "Section", "Name"],
      rows,
      Note(
        truncated,
        rows.Count == 0
          ? "This file carries no symbol table of either kind."
          : stripped
            ? "The static symbol table was stripped, so this is the dynamic one: the names the loader "
              + "needs, which is a small fraction of what the compiler emitted."
            : null
      )
    );

    string SectionOf(ushort index) => index switch {
      _ShnUndef => "undefined",
      _ShnAbs => "absolute",
      _ShnCommon => "common",
      _ => index < this._sections.Length && this._sections[index].Name.Length > 0
        ? this._sections[index].Name
        : Decimal(index),
    };
  }

  private BinaryView RelocationsPage() {
    var machine = U16(this._header, 18);
    var rows = new List<string[]>();
    foreach (var section in this._sections) {
      if (section.Type is not (_ShtRela or _ShtRel or _ShtRelr) || section.Size <= 0)
        continue;

      var entrySize = section.EntrySize > 0
        ? (int)section.EntrySize
        : section.Type switch {
          _ShtRela => this._is64 ? 24 : 12,
          _ShtRel => this._is64 ? 16 : 8,
          _ => this._is64 ? 8 : 4,
        };

      var entries = entrySize > 0 ? section.Size / entrySize : 0;
      rows.Add([
        section.Name.Length > 0 ? section.Name : "—",
        SectionTypeName(section.Type),
        Hex(section.Offset),
        Decimal(section.Size),
        Decimal(entries),
        // RELR is a bitmap rather than a list of typed relocations — every entry it encodes is a
        // relative one — so asking it for a breakdown of types would invent a table it does not have.
        section.Type == _ShtRelr ? "all relative" : this.Breakdown(section, entrySize, machine),
      ]);
    }

    return new(
      "Relocations",
      ["Section", "Kind", "Offset", "Size", "Entries", "Types"],
      rows,
      rows.Count == 0 ? "This file has no relocation sections." : null
    );
  }

  /// <summary>
  /// Which relocation types one section is made of, counted.
  /// </summary>
  /// <remarks>
  /// The counts rather than the entries. A libc has forty thousand relocations in it and printing
  /// them would be forty thousand rows nobody reads, where "38 214 RELATIVE, 1 208 GLOB_DAT" is the
  /// sentence somebody actually wanted — and it is the one that shows at a glance whether an image
  /// is doing anything unusual at load time.
  /// </remarks>
  private string Breakdown(in Section section, int entrySize, ushort machine) {
    var bytes = this.ReadWhole(section.Offset, Math.Min(section.Size, _MaxSymbols * (long)entrySize));
    if (bytes is null)
      return "—";

    var counts = new Dictionary<uint, int>();
    var typeAt = this._is64 ? 8 : 4;
    for (var at = 0; at + entrySize <= bytes.Length; at += entrySize) {
      // r_info holds the symbol index in its top half and the type in its bottom: 32 bits of each on
      // a 64-bit image and 8 bits of type on a 32-bit one.
      var type = this._is64 ? U32(bytes, at + typeAt) : U32(bytes, at + typeAt) & 0xFF;
      counts[type] = counts.TryGetValue(type, out var seen) ? seen + 1 : 1;
    }

    var names = new List<string>();
    foreach (var pair in counts.OrderByDescending(static p => p.Value).Take(6))
      names.Add($"{Decimal(pair.Value)} {RelocationTypeName(machine, pair.Key)}");

    return names.Count > 0 ? string.Join(", ", names) : "—";
  }

  private BinaryView DebugPage() {
    ElfImage.TryDescribe(this._read, out var description);
    var rows = new List<string[]>();
    rows.Add(["build id", description.BuildId ?? "none — the image was built without --build-id"]);

    var stripped = true;
    foreach (var section in this._sections)
      if (section.Type == _ShtSymTab)
        stripped = false;

    rows.Add(["symbol table", stripped ? "stripped" : "present"]);

    // .gnu_debuglink names the separate file the distribution split the debug information into, and
    // carries a CRC of it so the two cannot be mismatched. Its presence is the difference between
    // "there is no debug information" and "it is in the -dbg package".
    foreach (var section in this._sections) {
      if (section.Name != ".gnu_debuglink")
        continue;

      var bytes = this.ReadWhole(section.Offset, section.Size);
      if (bytes is null)
        continue;

      var name = StringAt(bytes, 0);
      // The name is NUL-terminated and then padded to a four-byte boundary; the CRC is the four
      // bytes after that padding.
      var crcAt = ((name?.Length ?? 0) + 4) & ~3;
      rows.Add(["debug link", name ?? "—"]);
      if (crcAt + 4 <= bytes.Length)
        rows.Add(["debug link crc", Hex(U32(bytes, crcAt))]);
    }

    var debugSections = 0;
    long debugBytes = 0;
    foreach (var section in this._sections)
      if (section.Name.StartsWith(".debug", StringComparison.Ordinal) || section.Name.StartsWith(".zdebug", StringComparison.Ordinal)) {
        ++debugSections;
        debugBytes += section.Size;
        rows.Add([section.Name, Decimal(section.Size) + " bytes"]);
      }

    if (debugSections == 0)
      rows.Add(["debug sections", "none in this file"]);
    else
      rows.Add(["debug information", $"{Decimal(debugSections)} sections, {Decimal(debugBytes)} bytes"]);

    return BinaryView.Facts(
      "Debug information",
      rows,
      "What the file says about itself. Whether a debugger could actually find the separate debug "
      + "file, and whether a build-id lookup would resolve, are questions about this machine's "
      + "installed packages rather than about these bytes."
    );
  }

  private BinaryView SecurityPage() {
    ElfImage.TryDescribe(this._read, out var description);
    var mitigations = description.Mitigations;
    var dynamicSymbols = this.ReadSymbols(_ShtDynSym, out _);
    var canary = false;
    var fortified = 0;
    foreach (var symbol in dynamicSymbols) {
      if (symbol.Name is "__stack_chk_fail" or "__stack_chk_guard")
        canary = true;

      if (symbol.SectionIndex == _ShnUndef && symbol.Name.EndsWith("_chk", StringComparison.Ordinal))
        ++fortified;
    }

    var relro = (mitigations & Model.ImageMitigations.Relro) != 0;
    var bindNow = (mitigations & Model.ImageMitigations.BindNow) != 0;
    var rows = new List<string[]>();
    rows.Add(["position independent", Yes(mitigations, Model.ImageMitigations.PositionIndependent, "ET_DYN — the kernel may place it anywhere", "ET_EXEC — it names its own addresses")]);
    rows.Add(["stack", (mitigations & Model.ImageMitigations.ExecutableStack) != 0
        ? "executable — PT_GNU_STACK asks for RWX"
        : (mitigations & Model.ImageMitigations.NonExecutableStack) != 0
          ? "not executable"
          : "not stated — the image has no PT_GNU_STACK and the ABI decides"]);
    rows.Add(["relro", relro ? (bindNow ? "full — the relocations are read-only after loading" : "partial — the GOT is still writable") : "none"]);
    rows.Add(["bind now", bindNow ? "yes" : "no — symbols are resolved lazily through the PLT"]);
    rows.Add(["stack protector", canary ? "yes — the image references __stack_chk_fail" : "no such reference"]);
    rows.Add(["fortify source", fortified > 0 ? $"{Decimal(fortified)} fortified calls imported" : "no _chk imports"]);
    rows.Add(["indirect branch tracking", Yes(mitigations, Model.ImageMitigations.IndirectBranchTracking, "IBT", "not declared")]);
    rows.Add(["shadow stack", Yes(mitigations, Model.ImageMitigations.ShadowStack, "SHSTK", "not declared")]);
    rows.Add(["branch target identification", Yes(mitigations, Model.ImageMitigations.BranchTargetIdentification, "BTI", "not declared")]);
    rows.Add(["pointer authentication", Yes(mitigations, Model.ImageMitigations.PointerAuthentication, "PAC", "not declared")]);

    var strings = this.DynamicStrings;
    foreach (var (tag, value) in this.Dynamic)
      if (tag is _DtRPath or _DtRunPath)
        rows.Add([tag == _DtRPath ? "rpath" : "runpath", StringAt(strings, value) ?? Hex(value)]);

    return BinaryView.Facts(
      "Security properties",
      rows,
      "**What the file asks for, not what it got.** A position-independent image is only randomised "
      + "if the kernel is randomising; a shadow stack is only a shadow stack on a processor that has "
      + "one. Only the first half is readable from a file on disk (§31)."
    );

    static string Yes(Model.ImageMitigations have, Model.ImageMitigations wanted, string yes, string no)
      => (have & wanted) != 0 ? yes : no;
  }

  /// <summary>The regions of the file that hold code, for a strings scan restricted to them (§35).</summary>
  public IReadOnlyList<(long Offset, long Length, string Name)> ExecutableRegions {
    get {
      var found = new List<(long, long, string)>();
      // The section table first, because a section is the tighter answer: .text alone rather than the
      // whole executable LOAD segment, which also carries .plt, .rodata on a merged layout and the
      // alignment padding between them.
      foreach (var section in this._sections)
        if ((section.Flags & 0x4) != 0 && section.Type != _ShtNoBits && section.Size > 0 && section.Offset > 0)
          found.Add((section.Offset, section.Size, section.Name.Length > 0 ? section.Name : "section"));

      if (found.Count > 0)
        return found;

      foreach (var segment in this._segments)
        if (segment.Type == 1 && (segment.Flags & 1) != 0 && segment.FileSize > 0)
          found.Add((segment.Offset, segment.FileSize, "LOAD"));

      return found;
    }
  }

  #endregion

  #region names

  private static string? Note(bool truncated, string? note)
    => truncated
      ? (note is { Length: > 0 } ? note + " " : string.Empty)
        + $"More than {Decimal(_MaxRows)} rows: the rest are in the file and not in this table."
      : note;

  private static string OsAbi(byte value) => value switch {
    0 => "UNIX - System V",
    1 => "HP-UX",
    2 => "NetBSD",
    3 => "Linux",
    6 => "Solaris",
    9 => "FreeBSD",
    12 => "OpenBSD",
    _ => $"ABI {Decimal(value)}",
  };

  private static string TypeName(ushort value) => value switch {
    0 => "NONE",
    1 => "REL — relocatable object",
    2 => "EXEC — executable",
    3 => "DYN — shared object or position-independent executable",
    4 => "CORE — core dump",
    _ => $"type {Decimal(value)}",
  };

  private static string MachineName(ushort machine) => machine switch {
    2 => "SPARC",
    3 => "Intel 80386",
    8 => "MIPS",
    20 => "PowerPC",
    21 => "PowerPC64",
    22 => "IBM S/390",
    40 => "ARM",
    62 => "AMD x86-64",
    183 => "AArch64",
    243 => "RISC-V",
    258 => "LoongArch",
    _ => $"machine {Decimal(machine)}",
  };

  private static string SegmentTypeName(uint type) => type switch {
    0 => "NULL",
    1 => "LOAD",
    2 => "DYNAMIC",
    3 => "INTERP",
    4 => "NOTE",
    5 => "SHLIB",
    6 => "PHDR",
    7 => "TLS",
    0x6474E550 => "GNU_EH_FRAME",
    0x6474E551 => "GNU_STACK",
    0x6474E552 => "GNU_RELRO",
    0x6474E553 => "GNU_PROPERTY",
    0x6474E554 => "GNU_SFRAME",
    0x65A3DBE6 => "OPENBSD_RANDOMIZE",
    _ => Hex(type),
  };

  private static string SectionTypeName(uint type) => type switch {
    0 => "NULL",
    _ShtProgBits => "PROGBITS",
    _ShtSymTab => "SYMTAB",
    _ShtStrTab => "STRTAB",
    _ShtRela => "RELA",
    5 => "HASH",
    _ShtDynamic => "DYNAMIC",
    _ShtNote => "NOTE",
    _ShtNoBits => "NOBITS",
    _ShtRel => "REL",
    10 => "SHLIB",
    _ShtDynSym => "DYNSYM",
    14 => "INIT_ARRAY",
    15 => "FINI_ARRAY",
    16 => "PREINIT_ARRAY",
    17 => "GROUP",
    18 => "SYMTAB_SHNDX",
    _ShtRelr => "RELR",
    0x6FFFFFF5 => "GNU_ATTRIBUTES",
    0x6FFFFFF6 => "GNU_HASH",
    0x6FFFFFF7 => "GNU_LIBLIST",
    _ShtGnuVerDef => "VERDEF",
    _ShtGnuVerNeed => "VERNEED",
    _ShtGnuVerSym => "VERSYM",
    _ => Hex(type),
  };

  /// <summary>The section flags, in the letters <c>readelf -S</c> prints.</summary>
  private static string SectionFlags(ulong flags) {
    var text = new StringBuilder();
    if ((flags & 0x1) != 0) text.Append('W');
    if ((flags & 0x2) != 0) text.Append('A');
    if ((flags & 0x4) != 0) text.Append('X');
    if ((flags & 0x10) != 0) text.Append('M');
    if ((flags & 0x20) != 0) text.Append('S');
    if ((flags & 0x40) != 0) text.Append('I');
    if ((flags & 0x80) != 0) text.Append('L');
    if ((flags & 0x200) != 0) text.Append('G');
    if ((flags & 0x400) != 0) text.Append('T');
    if ((flags & 0x800) != 0) text.Append('C');
    return text.Length > 0 ? text.ToString() : "—";
  }

  private static string Permissions(uint flags) {
    Span<char> text = ['-', '-', '-'];
    if ((flags & 4) != 0) text[0] = 'r';
    if ((flags & 2) != 0) text[1] = 'w';
    if ((flags & 1) != 0) text[2] = 'x';
    return new(text);
  }

  private static string DynamicTagName(long tag) => tag switch {
    0 => "NULL",
    1 => "NEEDED",
    2 => "PLTRELSZ",
    3 => "PLTGOT",
    4 => "HASH",
    5 => "STRTAB",
    6 => "SYMTAB",
    7 => "RELA",
    8 => "RELASZ",
    9 => "RELAENT",
    10 => "STRSZ",
    11 => "SYMENT",
    12 => "INIT",
    13 => "FINI",
    14 => "SONAME",
    15 => "RPATH",
    16 => "SYMBOLIC",
    17 => "REL",
    18 => "RELSZ",
    19 => "RELENT",
    20 => "PLTREL",
    21 => "DEBUG",
    22 => "TEXTREL",
    23 => "JMPREL",
    24 => "BIND_NOW",
    25 => "INIT_ARRAY",
    26 => "FINI_ARRAY",
    27 => "INIT_ARRAYSZ",
    28 => "FINI_ARRAYSZ",
    29 => "RUNPATH",
    30 => "FLAGS",
    32 => "PREINIT_ARRAY",
    33 => "PREINIT_ARRAYSZ",
    34 => "SYMTAB_SHNDX",
    35 => "RELRSZ",
    36 => "RELR",
    37 => "RELRENT",
    0x6FFFFEF5 => "GNU_HASH",
    0x6FFFFEF6 => "TLSDESC_PLT",
    0x6FFFFEF7 => "TLSDESC_GOT",
    0x6FFFFFF9 => "RELACOUNT",
    0x6FFFFFFA => "RELCOUNT",
    0x6FFFFFFB => "FLAGS_1",
    0x6FFFFFFC => "VERDEF",
    0x6FFFFFFD => "VERDEFNUM",
    0x6FFFFFFE => "VERNEED",
    0x6FFFFFFF => "VERNEEDNUM",
    0x6FFFFFF0 => "VERSYM",
    _ => Hex(tag),
  };

  private static string SymbolType(byte info) => (info & 0xF) switch {
    0 => "notype",
    1 => "object",
    2 => "func",
    3 => "section",
    4 => "file",
    5 => "common",
    6 => "tls",
    10 => "ifunc",
    _ => Decimal(info & 0xF),
  };

  private static string SymbolBinding(byte info) => (info >> 4) switch {
    0 => "local",
    1 => "global",
    2 => "weak",
    10 => "gnu_unique",
    _ => Decimal(info >> 4),
  };

  private static string SymbolVisibility(byte other) => (other & 0x3) switch {
    0 => "default",
    1 => "internal",
    2 => "hidden",
    3 => "protected",
    _ => Decimal(other & 0x3),
  };

  /// <summary>
  /// A relocation type's name, for the machines this program ships on.
  /// </summary>
  /// <remarks>
  /// The numbers mean different things on different machines — <c>7</c> is a jump slot on x86-64 and
  /// a GOT entry on i386 — so the machine decides, and one nobody here knows is reported as its
  /// number rather than as somebody else's name (PRD §5.3).
  /// </remarks>
  private static string RelocationTypeName(ushort machine, uint type) => machine switch {
    62 => type switch {
      0 => "NONE", 1 => "64", 2 => "PC32", 3 => "GOT32", 4 => "PLT32", 5 => "COPY",
      6 => "GLOB_DAT", 7 => "JUMP_SLOT", 8 => "RELATIVE", 9 => "GOTPCREL", 10 => "32", 11 => "32S",
      16 => "DTPMOD64", 17 => "DTPOFF64", 18 => "TPOFF64", 19 => "TLSGD", 20 => "TLSLD",
      21 => "DTPOFF32", 22 => "GOTTPOFF", 23 => "TPOFF32", 24 => "PC64",
      34 => "TLSDESC_CALL", 35 => "TLSDESC", 36 => "IRELATIVE", 37 => "RELATIVE64",
      41 => "GOTPCRELX", 42 => "REX_GOTPCRELX",
      _ => Decimal(type),
    },
    3 => type switch {
      0 => "NONE", 1 => "32", 2 => "PC32", 3 => "GOT32", 4 => "PLT32", 5 => "COPY",
      6 => "GLOB_DAT", 7 => "JMP_SLOT", 8 => "RELATIVE", 9 => "GOTOFF", 10 => "GOTPC",
      14 => "TLS_TPOFF", 35 => "TLS_DTPMOD32", 36 => "TLS_DTPOFF32", 37 => "TLS_TPOFF32",
      42 => "IRELATIVE",
      _ => Decimal(type),
    },
    183 => type switch {
      0 => "NONE", 257 => "ABS64", 258 => "ABS32", 259 => "ABS16", 260 => "PREL64",
      261 => "PREL32", 262 => "PREL16",
      1024 => "COPY", 1025 => "GLOB_DAT", 1026 => "JUMP_SLOT", 1027 => "RELATIVE",
      1028 => "TLS_DTPMOD", 1029 => "TLS_DTPREL", 1030 => "TLS_TPREL", 1031 => "TLSDESC",
      1032 => "IRELATIVE",
      _ => Decimal(type),
    },
    40 => type switch {
      0 => "NONE", 2 => "ABS32", 20 => "COPY", 21 => "GLOB_DAT", 22 => "JUMP_SLOT",
      23 => "RELATIVE", 160 => "IRELATIVE",
      _ => Decimal(type),
    },
    _ => Decimal(type),
  };

  #endregion

  #region reading

  private ushort U16(byte[] bytes, int at) => at + 2 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at))
    : (ushort)0;

  private uint U32(byte[] bytes, int at) => at + 4 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at))
    : 0u;

  private ulong U64(byte[] bytes, int at) => at + 8 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at))
    : 0ul;

  // Four spellings of each rather than one, because the fields of this format are unsigned, signed
  // and short by turns: a single ulong overload makes every long call site a cast, and a long and a
  // ulong overload together are ambiguous for the uint fields, which is a compile error rather than
  // a wrong answer only because C# refuses to guess.
  private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

  private static string Hex(long value) => Hex((ulong)value);

  private static string Hex(uint value) => Hex((ulong)value);

  private static string Decimal(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(uint value) => Decimal((ulong)value);

  #endregion

}
