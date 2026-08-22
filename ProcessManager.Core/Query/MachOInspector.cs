using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Everything §53 asks about a Mach-O image, read from the file and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written and tested without a Mach-O machine.</b> §6.3 has macOS as a stub and there is not a
/// Darwin binary on the machines this is built on, so every claim here rests on the format's own
/// documentation and on fixtures a test writes byte by byte — not on holding the output against
/// <c>otool</c>. That is a weaker footing than the ELF side has and it is written down rather than
/// glossed over: the arithmetic is checkable, the coverage of real-world oddities is not (PRD §5.3).
/// </para>
/// <para>
/// A universal binary is several images in one file with a table at the front saying where each
/// begins. Everything below therefore works at an offset: the header is at the slice's start rather
/// than at the file's, and a reader that assumed nought would describe the fat header as a Mach-O
/// and get every field wrong.
/// </para>
/// <para>
/// Entitlements are read where they are, which is inside the code signature's own super-blob — the
/// one place a signed Darwin binary states what the kernel will let it do. Reading them is
/// inspection; nothing here checks the signature, because verifying one needs Apple's trust store
/// and that is the trust-chain question §70 keeps separate from every other.
/// </para>
/// </remarks>
internal sealed class MachOInspector {

  #region shapes

  private readonly record struct Command(uint Type, uint Size, long Offset);

  private readonly record struct Segment(
    string Name,
    ulong VirtualAddress,
    ulong VirtualSize,
    long FileOffset,
    long FileSize,
    uint MaximumProtection,
    uint InitialProtection,
    uint Sections
  );

  private readonly record struct SectionEntry(
    string Segment,
    string Name,
    ulong Address,
    ulong Size,
    uint Offset,
    uint Align,
    uint Flags
  );

  #endregion

  #region constants

  private const uint _Magic32 = 0xFEEDFACE;
  private const uint _Magic64 = 0xFEEDFACF;
  private const uint _Cigam32 = 0xCEFAEDFE;
  private const uint _Cigam64 = 0xCFFAEDFE;

  /// <summary>The two spellings of a universal binary's own header.</summary>
  public const uint FatMagic = 0xCAFEBABE;

  public const uint FatMagic64 = 0xCAFEBABF;

  private const int _MaxCommands = 4096;
  private const int _MaxRows = 20_000;
  private const int _MaxTableBytes = 16 * 1024 * 1024;

  private const uint _LcSegment = 0x01;
  private const uint _LcSymTab = 0x02;
  private const uint _LcLoadDylib = 0x0C;
  private const uint _LcIdDylib = 0x0D;
  private const uint _LcLoadDylinker = 0x0E;
  private const uint _LcPreboundDylib = 0x10;
  private const uint _LcLoadWeakDylib = 0x80000018;
  private const uint _LcSegment64 = 0x19;
  private const uint _LcUuid = 0x1B;
  private const uint _LcCodeSignature = 0x1D;
  private const uint _LcReexportDylib = 0x8000001F;
  private const uint _LcLoadUpwardDylib = 0x80000023;

  /// <summary>The code signature's super-blob, and the two slots this reads out of it.</summary>
  private const uint _CsMagicEmbeddedSignature = 0xFADE0CC0;

  private const uint _CsSlotCodeDirectory = 0;
  private const uint _CsSlotEntitlements = 5;
  private const uint _CsSlotEntitlementsDer = 7;
  private const uint _CsMagicCodeDirectory = 0xFADE0C02;

  #endregion

  private readonly ElfImage.ElfRead _read;
  private readonly long _base;
  private readonly byte[] _header;
  private readonly bool _is64;
  private readonly bool _little;
  private readonly Command[] _commands;

  public long Length { get; }

  private MachOInspector(ElfImage.ElfRead read, long length, long start, byte[] header, bool is64, bool little) {
    this._read = read;
    this.Length = length;
    this._base = start;
    this._header = header;
    this._is64 = is64;
    this._little = little;
    this._commands = this.ReadCommands();
  }

  /// <summary>Reads the header of one image, or hands back null when it is not a Mach-O.</summary>
  /// <param name="start">
  /// Where this image begins. Non-zero for one slice of a universal binary, and every offset the
  /// header carries is relative to it rather than to the file.
  /// </param>
  public static MachOInspector? TryOpen(ElfImage.ElfRead read, long length, long start = 0) {
    ArgumentNullException.ThrowIfNull(read);

    var header = new byte[32];
    if (read(start, header) < 28)
      return null;

    // Read big-endian on purpose: the magic is stored in the image's own byte order, so the word
    // that comes back says both what the file is and which way round it is. FEEDFACF is a big-endian
    // 64-bit image and CFFAEDFE is the same image little-endian.
    var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
    if (magic is not (_Magic32 or _Magic64 or _Cigam32 or _Cigam64))
      return null;

    return new(read, length, start, header, magic is _Magic64 or _Cigam64, magic is _Cigam32 or _Cigam64);
  }

  /// <summary>
  /// Where each image of a universal binary begins, or empty when this is not one.
  /// </summary>
  /// <remarks>
  /// The fat header is big-endian whatever the images inside it are, which is the one thing about
  /// this format that never varies. Its entries are architecture, offset and size; nothing else in
  /// the file says how many images there are.
  /// </remarks>
  public static IReadOnlyList<(long Offset, long Size, string Architecture)> Slices(ElfImage.ElfRead read) {
    ArgumentNullException.ThrowIfNull(read);

    var header = new byte[8];
    if (read(0, header) < 8)
      return [];

    var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
    if (magic is not (FatMagic or FatMagic64))
      return [];

    var wide = magic == FatMagic64;
    var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
    if (count is 0 or > 64)
      return [];

    var width = wide ? 32 : 20;
    var table = new byte[count * width];
    if (read(8, table) < table.Length)
      return [];

    var found = new List<(long, long, string)>((int)count);
    for (var i = 0; i < count; ++i) {
      var at = i * width;
      var cpu = BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(at));
      var offset = wide
        ? (long)BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(at + 8))
        : BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at + 8));
      var size = wide
        ? (long)BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(at + 16))
        : BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at + 12));
      found.Add((offset, size, CpuName(cpu, BinaryPrimitives.ReadInt32BigEndian(table.AsSpan(at + 4)))));
    }

    return found;
  }

  private Command[] ReadCommands() {
    var count = this.U32(this._header, 16);
    var size = this.U32(this._header, 20);
    if (count is 0 or > _MaxCommands || size is 0 or > _MaxTableBytes)
      return [];

    var start = this._is64 ? 32 : 28;
    var table = new byte[size];
    if (this._read(this._base + start, table) < table.Length)
      return [];

    var found = new List<Command>((int)count);
    for (var at = 0; found.Count < count && at + 8 <= table.Length;) {
      var type = this.U32(table, at);
      var length = this.U32(table, at + 4);
      // A command shorter than its own header, or one that runs past the table, ends the walk. Both
      // are shapes a truncated or hostile file has, and either would loop for ever.
      if (length < 8 || at + length > table.Length)
        break;

      found.Add(new(type, length, this._base + start + at));
      at += (int)length;
    }

    this._commandTable = table;
    this._commandStart = this._base + start;
    return [.. found];
  }

  private byte[]? _commandTable;
  private long _commandStart;

  /// <summary>One command's bytes, out of the table that was already read.</summary>
  private ReadOnlySpan<byte> Body(in Command command) {
    if (this._commandTable is null)
      return default;

    var at = (int)(command.Offset - this._commandStart);
    return at < 0 || at + command.Size > this._commandTable.Length
      ? default
      : this._commandTable.AsSpan(at, (int)command.Size);
  }

  #region pages

  public BinaryView View(BinaryPage page) => page switch {
    BinaryPage.Headers => this.HeadersPage(),
    BinaryPage.Segments => this.SegmentsPage(),
    BinaryPage.Sections => this.SectionsPage(),
    BinaryPage.Dynamic => this.CommandsPage(),
    BinaryPage.Dependencies => this.DependenciesPage(),
    BinaryPage.Imports => this.SymbolPage(imports: true),
    BinaryPage.Exports => this.SymbolPage(imports: false),
    BinaryPage.Symbols => this.SymbolsPage(),
    BinaryPage.Relocations => BinaryView.Empty(
      "Relocations",
      "A modern Mach-O carries chained fixups rather than a relocation table, and the load commands "
      + "page names the LC_DYLD_CHAINED_FIXUPS record where they are. Walking the chains needs the "
      + "image's own pointer format decoded, which is past what a viewer of the file structure does."
    ),
    BinaryPage.Resources => BinaryView.Empty(
      "Resources",
      "Mach-O has no resource section. A Darwin application keeps its icons, its strings and its "
      + "Info.plist in the bundle directory around the binary rather than inside it."
    ),
    BinaryPage.Signature => this.SignaturePage(),
    BinaryPage.Debug => this.DebugPage(),
    BinaryPage.Security => this.SecurityPage(),
    _ => this.SummaryPage(),
  };

  private BinaryView SummaryPage() {
    var rows = new List<string[]>();
    rows.Add(["format", "Mach-O"]);
    rows.Add(["class", this._is64 ? "64-bit" : "32-bit"]);
    rows.Add(["byte order", this._little ? "little endian" : "big endian"]);
    rows.Add(["architecture", CpuName(this.I32(this._header, 4), this.I32(this._header, 8))]);
    rows.Add(["type", FileTypeName(this.U32(this._header, 12))]);
    rows.Add(["load commands", Decimal(this._commands.Length)]);
    rows.Add(["flags", $"{Hex(this.U32(this._header, 24))}  {Flags(this.U32(this._header, 24))}"]);
    rows.Add(["file size", this.Length >= 0 ? Decimal(this.Length) + " bytes" : "unknown"]);
    rows.Add(["dependencies", Decimal(this.Dylibs().Count)]);
    rows.Add(["signed", this.CodeSignature() is { } signature
      ? $"a code signature of {Decimal(signature.Size)} bytes"
      : "no LC_CODE_SIGNATURE"]);

    return BinaryView.Facts("Summary", rows);
  }

  private BinaryView HeadersPage() {
    var rows = new List<string[]>();
    rows.Add(["magic", Hex(BinaryPrimitives.ReadUInt32BigEndian(this._header))]);
    rows.Add(["cpu type", CpuName(this.I32(this._header, 4), this.I32(this._header, 8))]);
    rows.Add(["cpu subtype", Hex(this.U32(this._header, 8))]);
    rows.Add(["file type", FileTypeName(this.U32(this._header, 12))]);
    rows.Add(["load commands", Decimal(this.U32(this._header, 16))]);
    rows.Add(["load command bytes", Decimal(this.U32(this._header, 20))]);
    rows.Add(["flags", $"{Hex(this.U32(this._header, 24))}  {Flags(this.U32(this._header, 24))}"]);
    return BinaryView.Facts("Mach-O header", rows);
  }

  private BinaryView CommandsPage() {
    var rows = new List<string[]>(this._commands.Length);
    foreach (var command in this._commands)
      rows.Add([CommandName(command.Type), Hex(command.Type), Decimal(command.Size), Hex(command.Offset)]);

    return new(
      "Load commands",
      ["Command", "Type", "Size", "File offset"],
      rows,
      "Everything the kernel and the dynamic linker are told about this image, in the order they are "
      + "told it. A command whose high bit is set is one the linker must understand or refuse to load."
    );
  }

  private List<Segment> Segments() {
    var found = new List<Segment>();
    foreach (var command in this._commands) {
      if (command.Type is not (_LcSegment or _LcSegment64))
        continue;

      var body = this.Body(in command);
      var wide = command.Type == _LcSegment64;
      var minimum = wide ? 72 : 56;
      if (body.Length < minimum)
        continue;

      found.Add(new(
        Name(body.Slice(8, 16)),
        wide ? this.U64(body, 24) : this.U32(body, 24),
        wide ? this.U64(body, 32) : this.U32(body, 28),
        wide ? (long)this.U64(body, 40) : this.U32(body, 32),
        wide ? (long)this.U64(body, 48) : this.U32(body, 36),
        this.U32(body, wide ? 56 : 40),
        this.U32(body, wide ? 60 : 44),
        this.U32(body, wide ? 64 : 48)
      ));
    }

    return found;
  }

  private BinaryView SegmentsPage() {
    var rows = new List<string[]>();
    foreach (var segment in this.Segments())
      rows.Add([
        segment.Name,
        Hex(segment.VirtualAddress),
        Hex(segment.VirtualSize),
        Hex(segment.FileOffset),
        Hex(segment.FileSize),
        Protection(segment.InitialProtection),
        Protection(segment.MaximumProtection),
        Decimal(segment.Sections),
      ]);

    return new(
      "Segments",
      ["Name", "Address", "Virtual size", "File offset", "File size", "Initial", "Maximum", "Sections"],
      rows,
      rows.Count == 0 ? "This image declares no segments." : null
    );
  }

  private List<SectionEntry> Sections() {
    var found = new List<SectionEntry>();
    foreach (var command in this._commands) {
      if (command.Type is not (_LcSegment or _LcSegment64))
        continue;

      var body = this.Body(in command);
      var wide = command.Type == _LcSegment64;
      var header = wide ? 72 : 56;
      var width = wide ? 80 : 68;
      var count = body.Length >= header ? this.U32(body, wide ? 64 : 48) : 0;
      for (var i = 0; i < count && found.Count < _MaxRows; ++i) {
        var at = header + (i * width);
        if (at + width > body.Length)
          break;

        var entry = body.Slice(at, width);
        found.Add(new(
          Name(entry.Slice(16, 16)),
          Name(entry[..16]),
          wide ? this.U64(entry, 32) : this.U32(entry, 32),
          wide ? this.U64(entry, 40) : this.U32(entry, 36),
          this.U32(entry, wide ? 48 : 40),
          this.U32(entry, wide ? 52 : 44),
          this.U32(entry, wide ? 64 : 56)
        ));
      }
    }

    return found;
  }

  private BinaryView SectionsPage() {
    var rows = new List<string[]>();
    foreach (var section in this.Sections())
      rows.Add([
        section.Segment,
        section.Name,
        Hex(section.Address),
        Hex(section.Size),
        Hex(section.Offset),
        Decimal(section.Align),
        Hex(section.Flags),
      ]);

    return new(
      "Sections",
      ["Segment", "Name", "Address", "Size", "File offset", "Align", "Flags"],
      rows,
      rows.Count == 0 ? "This image declares no sections." : null
    );
  }

  /// <summary>The libraries this image names, and how it names them.</summary>
  private List<(string Name, string Kind, string Version)> Dylibs() {
    var found = new List<(string, string, string)>();
    foreach (var command in this._commands) {
      var kind = command.Type switch {
        _LcLoadDylib => "load",
        _LcIdDylib => "this library's own name",
        _LcLoadWeakDylib => "weak",
        _LcReexportDylib => "re-export",
        _LcLoadUpwardDylib => "upward",
        _LcPreboundDylib => "prebound",
        _ => null,
      };

      if (kind is null)
        continue;

      var body = this.Body(in command);
      if (body.Length < 24)
        continue;

      // A dylib command carries its name as an offset from the command's own start, which is how one
      // variable-length string is packed into a fixed structure.
      var at = (int)this.U32(body, 8);
      var name = at > 0 && at < body.Length ? Name(body[at..], terminated: true) : "—";
      // current_version at 16 and compatibility_version at 20, each packed as X.Y.Z into one word.
      // The second is the one a loader enforces, so both are reported rather than whichever came
      // first (PRD §5.3).
      found.Add((name, kind, $"{Version(this.U32(body, 16))}, compatible from {Version(this.U32(body, 20))}"));
    }

    return found;
  }

  private BinaryView DependenciesPage() {
    var rows = new List<string[]>();
    foreach (var command in this._commands) {
      if (command.Type != _LcLoadDylinker)
        continue;

      var body = this.Body(in command);
      var at = body.Length >= 12 ? (int)this.U32(body, 8) : 0;
      if (at > 0 && at < body.Length)
        rows.Add(["dynamic linker", Name(body[at..], terminated: true)]);
    }

    foreach (var (name, kind, version) in this.Dylibs())
      rows.Add([kind, $"{name}  (version {version})"]);

    return BinaryView.Facts(
      "Dependencies",
      rows,
      rows.Count == 0
        ? "This image names no libraries, which a statically linked program and a kernel extension "
          + "both look like."
        : "What the file itself names. `DYLD_INSERT_LIBRARIES` and anything opened with `dlopen` at "
          + "run time are invisible from here."
    );
  }

  /// <summary>
  /// The symbol table, as <c>LC_SYMTAB</c> points at it.
  /// </summary>
  /// <remarks>
  /// One structure for everything: a symbol is a name offset, a type byte, a section number, a
  /// sixteen-bit description and a value. Whether it is imported or exported is the bottom three bits
  /// of the type — <c>N_UNDF</c> against anything else — rather than a separate table, which is why
  /// the imports and exports pages here are two filters over one walk.
  /// </remarks>
  private List<(string Name, byte Type, byte Section, ushort Description, ulong Value)> Symbols() {
    var found = new List<(string, byte, byte, ushort, ulong)>();
    foreach (var command in this._commands) {
      if (command.Type != _LcSymTab)
        continue;

      var body = this.Body(in command);
      if (body.Length < 24)
        continue;

      var symbolOffset = this.U32(body, 8);
      var count = this.U32(body, 12);
      var stringOffset = this.U32(body, 16);
      var stringSize = this.U32(body, 20);
      var width = this._is64 ? 16 : 12;
      if (count is 0 or > 2_000_000u)
        continue;

      var symbols = this.ReadAt(this._base + symbolOffset, (long)count * width);
      var strings = this.ReadAt(this._base + stringOffset, Math.Min(stringSize, _MaxTableBytes));
      if (symbols is null)
        continue;

      for (var i = 0; (i * width) + width <= symbols.Length && found.Count < _MaxRows; ++i) {
        var at = i * width;
        var name = strings is not null ? StringAt(strings, this.U32(symbols, at)) : null;
        found.Add((
          name ?? string.Empty,
          symbols[at + 4],
          symbols[at + 5],
          this.U16(symbols, at + 6),
          this._is64 ? this.U64(symbols, at + 8) : this.U32(symbols, at + 8)
        ));
      }
    }

    return found;
  }

  private BinaryView SymbolPage(bool imports) {
    var rows = new List<string[]>();
    foreach (var (name, type, section, description, value) in this.Symbols()) {
      // N_TYPE is the middle three bits; N_UNDF is nought and means the symbol is somebody else's.
      var undefined = (type & 0x0E) == 0;
      if (undefined != imports || name.Length == 0)
        continue;

      rows.Add(imports
        ? [name, SymbolType(type), (description >> 8) == 0 ? "—" : Decimal(description >> 8)]
        : [Hex(value), SymbolType(type), Decimal(section), name]);
    }

    return imports
      ? new(
        "Imports",
        ["Name", "Type", "Library ordinal"],
        rows,
        rows.Count == 0
          ? "This image imports nothing through the symbol table."
          : "The undefined symbols. The library ordinal indexes the dependencies page, which is where "
            + "a Mach-O says which library each import is expected from — unlike ELF, it says so per "
            + "symbol rather than through a version."
      )
      : new(
        "Exports",
        ["Value", "Type", "Section", "Name"],
        rows,
        rows.Count == 0
          ? "This image defines no external symbols in its symbol table. A recent linker puts the "
            + "exports in an LC_DYLD_EXPORTS_TRIE instead, which the load commands page names."
          : null
      );
  }

  private BinaryView SymbolsPage() {
    var rows = new List<string[]>();
    foreach (var (name, type, section, _, value) in this.Symbols())
      rows.Add([
        Hex(value),
        SymbolType(type),
        (type & 0x01) != 0 ? "external" : "local",
        section == 0 ? "—" : Decimal(section),
        name.Length > 0 ? name : "—",
      ]);

    return new(
      "Symbols",
      ["Value", "Type", "Scope", "Section", "Name"],
      rows,
      rows.Count == 0 ? "This image carries no LC_SYMTAB, so it has no symbol table at all." : null
    );
  }

  private Command? CodeSignature() {
    foreach (var command in this._commands)
      if (command.Type == _LcCodeSignature)
        return command;

    return null;
  }

  private BinaryView SignaturePage() {
    if (this.CodeSignature() is not { } command)
      return BinaryView.Empty(
        "Signature",
        "There is no LC_CODE_SIGNATURE. The image is unsigned — which on Darwin is a stronger "
        + "statement than it is elsewhere, because an unsigned binary will not run on an Apple "
        + "silicon machine at all."
      );

    var body = this.Body(in command);
    if (body.Length < 16)
      return BinaryView.Empty("Signature", "The LC_CODE_SIGNATURE record is truncated.");

    var offset = this.U32(body, 8);
    var size = this.U32(body, 12);
    var rows = new List<string[]>();
    rows.Add(["code signature at", Hex(this._base + offset)]);
    rows.Add(["code signature size", Decimal(size)]);

    // The super-blob and everything in it is big-endian whatever the image is, which is the one part
    // of Mach-O that never follows the header's byte order.
    var blob = this.ReadAt(this._base + offset, Math.Min(size, _MaxTableBytes));
    if (blob is not { Length: >= 12 } || BinaryPrimitives.ReadUInt32BigEndian(blob) != _CsMagicEmbeddedSignature) {
      rows.Add(["super-blob", "not the embedded-signature magic; nothing further was read"]);
      return BinaryView.Facts("Signature", rows);
    }

    var count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
    if (count > 64)
      count = 64;

    rows.Add(["blobs", Decimal(count)]);
    for (var i = 0u; i < count; ++i) {
      var at = 12 + ((int)i * 8);
      if (at + 8 > blob.Length)
        break;

      var slot = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at));
      var where = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(at + 4));
      if (where < 0 || where + 8 > blob.Length)
        continue;

      var length = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(where + 4));
      rows.Add([SlotName(slot), $"{Decimal(length)} bytes at {Hex(where)}"]);

      if (slot == _CsSlotCodeDirectory && length >= 44 && where + 44 <= blob.Length
          && BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(where)) == _CsMagicCodeDirectory) {
        // The identifier is the reverse-DNS name the signature is filed under, at an offset from the
        // directory's own start.
        var identifier = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(where + 20));
        if (identifier > 0 && where + identifier < blob.Length)
          rows.Add(["  identifier", Ascii(blob.AsSpan(where + identifier)) ?? "—"]);

        rows.Add(["  hash type", HashType(blob[where + 37])]);
        rows.Add(["  pages hashed", Decimal(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(where + 24)))]);
      }

      if (slot is _CsSlotEntitlements or _CsSlotEntitlementsDer && where + 8 + length <= blob.Length)
        rows.Add([
          slot == _CsSlotEntitlements ? "  entitlements" : "  entitlements (DER)",
          slot == _CsSlotEntitlements
            ? Ascii(blob.AsSpan(where + 8, Math.Max(0, length - 8))) ?? "—"
            : $"{Decimal(length - 8)} bytes of DER, not decoded here",
        ]);
    }

    return BinaryView.Facts(
      "Signature",
      rows,
      "**What is here, not whether it is good.** Verifying a Darwin code signature needs Apple's own "
      + "trust store and the notarisation service, which is the trust-chain question §70 keeps apart "
      + "from every other. The entitlements are read because they are a plain property list inside "
      + "the file and they are the only place it states what the kernel will let it do."
    );
  }

  private BinaryView DebugPage() {
    var rows = new List<string[]>();
    foreach (var command in this._commands) {
      if (command.Type != _LcUuid)
        continue;

      // The sixteen bytes are written in order rather than in the mixed-endian layout Windows uses,
      // which is what the big-endian flag on this constructor is for.
      var body = this.Body(in command);
      if (body.Length >= 24)
        rows.Add(["uuid", new Guid(body.Slice(8, 16), bigEndian: true).ToString()]);
    }

    foreach (var section in this.Sections())
      if (section.Name.StartsWith("__debug", StringComparison.Ordinal))
        rows.Add([section.Name, Decimal(section.Size) + " bytes"]);

    if (rows.Count == 0)
      return BinaryView.Empty(
        "Debug information",
        "This image carries neither a UUID nor any __debug sections, so it names no build and holds "
        + "no debug information."
      );

    return BinaryView.Facts(
      "Debug information",
      rows,
      "The UUID is what a dSYM bundle is matched against. Whether the matching bundle is anywhere "
      + "this machine can reach is not a question these bytes answer."
    );
  }

  private BinaryView SecurityPage() {
    var flags = this.U32(this._header, 24);
    var rows = new List<string[]>();
    rows.Add(["position independent", (flags & 0x00200000) != 0 ? "yes — MH_PIE" : "no — MH_PIE is not set"]);
    rows.Add(["stack execution", (flags & 0x00020000) != 0 ? "allowed — MH_ALLOW_STACK_EXECUTION" : "not allowed"]);
    rows.Add(["heap execution", (flags & 0x01000000) != 0 ? "no protection — MH_NO_HEAP_EXECUTION" : "protected"]);
    rows.Add(["restricted", (flags & 0x00800000) != 0
      ? "yes — MH_ROOT_SAFE or a __RESTRICT segment; the environment is scrubbed on exec"
      : "no"]);
    rows.Add(["two level namespace", (flags & 0x00000080) != 0 ? "yes" : "flat — every symbol is global"]);
    rows.Add(["binds at load", (flags & 0x00000008) != 0 ? "yes — MH_BINDATLOAD" : "lazily"]);
    rows.Add(["code signature", this.CodeSignature() is not null ? "present" : "none"]);

    var writableExecutable = new List<string>();
    foreach (var segment in this.Segments())
      if ((segment.InitialProtection & 0x2) != 0 && (segment.InitialProtection & 0x4) != 0)
        writableExecutable.Add(segment.Name);

    rows.Add(["writable and executable", writableExecutable.Count > 0
      ? string.Join(", ", writableExecutable)
      : "no segment is both"]);

    return BinaryView.Facts(
      "Security properties",
      rows,
      "**What the file asks for, not what it got.** A PIE image is only randomised if the kernel is "
      + "randomising, and the entitlements in the signature can undo several of these at run time."
    );
  }

  /// <summary>The regions of the file that hold code, for a strings scan restricted to them (§35).</summary>
  public IReadOnlyList<(long Offset, long Length, string Name)> ExecutableRegions {
    get {
      var found = new List<(long, long, string)>();
      foreach (var segment in this.Segments())
        if ((segment.InitialProtection & 0x4) != 0 && segment.FileSize > 0)
          found.Add((this._base + segment.FileOffset, segment.FileSize, segment.Name));

      return found;
    }
  }

  #endregion

  #region names

  private static string CpuName(int cpu, int subtype) => cpu switch {
    7 => "i386",
    0x0100_0007 => subtype == 8 ? "x86_64h" : "x86_64",
    12 => "arm",
    0x0100_000C => subtype switch { 2 => "arm64e", 1 => "arm64v8", _ => "arm64" },
    0x0200_000C => "arm64_32",
    18 => "ppc",
    0x0100_0012 => "ppc64",
    _ => $"cpu {Decimal(cpu)}",
  };

  private static string FileTypeName(uint type) => type switch {
    1 => "MH_OBJECT — a relocatable object",
    2 => "MH_EXECUTE — a program",
    3 => "MH_FVMLIB",
    4 => "MH_CORE — a core dump",
    5 => "MH_PRELOAD",
    6 => "MH_DYLIB — a shared library",
    7 => "MH_DYLINKER — the dynamic linker itself",
    8 => "MH_BUNDLE — a loadable bundle",
    9 => "MH_DYLIB_STUB",
    10 => "MH_DSYM — debug information only",
    11 => "MH_KEXT_BUNDLE — a kernel extension",
    12 => "MH_FILESET",
    _ => $"type {Decimal(type)}",
  };

  private static string CommandName(uint type) => (type & 0x7FFF_FFFF) switch {
    0x01 => "LC_SEGMENT",
    0x02 => "LC_SYMTAB",
    0x03 => "LC_SYMSEG",
    0x04 => "LC_THREAD",
    0x05 => "LC_UNIXTHREAD",
    0x0B => "LC_DYSYMTAB",
    0x0C => "LC_LOAD_DYLIB",
    0x0D => "LC_ID_DYLIB",
    0x0E => "LC_LOAD_DYLINKER",
    0x0F => "LC_ID_DYLINKER",
    0x10 => "LC_PREBOUND_DYLIB",
    0x11 => "LC_ROUTINES",
    0x12 => "LC_SUB_FRAMEWORK",
    0x14 => "LC_SUB_CLIENT",
    0x18 => "LC_LOAD_WEAK_DYLIB",
    0x19 => "LC_SEGMENT_64",
    0x1A => "LC_ROUTINES_64",
    0x1B => "LC_UUID",
    0x1C => "LC_RPATH",
    0x1D => "LC_CODE_SIGNATURE",
    0x1E => "LC_SEGMENT_SPLIT_INFO",
    0x1F => "LC_REEXPORT_DYLIB",
    0x20 => "LC_LAZY_LOAD_DYLIB",
    0x21 => "LC_ENCRYPTION_INFO",
    0x22 => "LC_DYLD_INFO",
    0x23 => "LC_LOAD_UPWARD_DYLIB",
    0x24 => "LC_VERSION_MIN_MACOSX",
    0x25 => "LC_VERSION_MIN_IPHONEOS",
    0x26 => "LC_FUNCTION_STARTS",
    0x27 => "LC_DYLD_ENVIRONMENT",
    0x28 => "LC_MAIN",
    0x29 => "LC_DATA_IN_CODE",
    0x2A => "LC_SOURCE_VERSION",
    0x2B => "LC_DYLIB_CODE_SIGN_DRS",
    0x2C => "LC_ENCRYPTION_INFO_64",
    0x2D => "LC_LINKER_OPTION",
    0x2E => "LC_LINKER_OPTIMIZATION_HINT",
    0x2F => "LC_VERSION_MIN_TVOS",
    0x30 => "LC_VERSION_MIN_WATCHOS",
    0x31 => "LC_NOTE",
    0x32 => "LC_BUILD_VERSION",
    0x33 => "LC_DYLD_EXPORTS_TRIE",
    0x34 => "LC_DYLD_CHAINED_FIXUPS",
    0x35 => "LC_FILESET_ENTRY",
    _ => Hex(type),
  };

  private static string Flags(uint flags) {
    var names = new List<string>();
    Add(0x00000001, "NOUNDEFS");
    Add(0x00000002, "INCRLINK");
    Add(0x00000004, "DYLDLINK");
    Add(0x00000008, "BINDATLOAD");
    Add(0x00000010, "PREBOUND");
    Add(0x00000020, "SPLIT_SEGS");
    Add(0x00000080, "TWOLEVEL");
    Add(0x00000100, "FORCE_FLAT");
    Add(0x00001000, "SUBSECTIONS_VIA_SYMBOLS");
    Add(0x00020000, "ALLOW_STACK_EXECUTION");
    Add(0x00080000, "WEAK_DEFINES");
    Add(0x00100000, "BINDS_TO_WEAK");
    Add(0x00200000, "PIE");
    Add(0x00800000, "ROOT_SAFE");
    Add(0x01000000, "NO_HEAP_EXECUTION");
    Add(0x02000000, "APP_EXTENSION_SAFE");
    return names.Count > 0 ? string.Join(" · ", names) : "none";

    void Add(uint bit, string name) {
      if ((flags & bit) != 0)
        names.Add(name);
    }
  }

  /// <summary>A Mach-O version word, which packs three components into thirty-two bits.</summary>
  private static string Version(uint value)
    => $"{Decimal(value >> 16)}.{Decimal((value >> 8) & 0xFF)}.{Decimal(value & 0xFF)}";

  private static string Protection(uint value) {
    Span<char> text = ['-', '-', '-'];
    if ((value & 1) != 0) text[0] = 'r';
    if ((value & 2) != 0) text[1] = 'w';
    if ((value & 4) != 0) text[2] = 'x';
    return new(text);
  }

  /// <summary>
  /// What one <c>n_type</c> byte says.
  /// </summary>
  /// <remarks>
  /// The middle three bits are the kind and the top three say the entry is a debugging stab rather
  /// than a symbol at all. The stab check comes first because a stab's kind bits mean something
  /// else entirely, and reading them as a kind puts a source file name in the undefined list.
  /// </remarks>
  private static string SymbolType(byte type) => (type & 0xE0) != 0 ? "debug" : (type & 0x0E) switch {
    0x00 => "undefined",
    0x02 => "absolute",
    0x0A => "indirect",
    0x0C => "prebound undefined",
    0x0E => "defined in a section",
    _ => Hex(type),
  };

  private static string SlotName(uint slot) => slot switch {
    0 => "code directory",
    1 => "info.plist hash",
    2 => "requirements",
    3 => "resource directory hash",
    4 => "application specific",
    5 => "entitlements",
    7 => "entitlements, DER",
    0x1000 => "alternate code directory",
    0x10000 => "signature blob",
    _ => $"slot {Hex(slot)}",
  };

  private static string HashType(byte type) => type switch {
    1 => "SHA-1",
    2 => "SHA-256",
    3 => "SHA-256, truncated to 20 bytes",
    4 => "SHA-384",
    _ => Decimal(type),
  };

  /// <summary>A fixed-width name, which Mach-O pads with NULs and does not always terminate.</summary>
  private static string Name(ReadOnlySpan<byte> bytes, bool terminated = false) {
    var nul = bytes.IndexOf((byte)0);
    var span = nul < 0 ? bytes : bytes[..nul];
    if (terminated && span.Length > 1024)
      span = span[..1024];

    return span.IsEmpty ? "—" : Encoding.UTF8.GetString(span);
  }

  private static string? Ascii(ReadOnlySpan<byte> bytes) {
    var nul = bytes.IndexOf((byte)0);
    var span = nul < 0 ? bytes : bytes[..nul];
    if (span.Length > 8192)
      span = span[..8192];

    return span.IsEmpty ? null : Encoding.UTF8.GetString(span);
  }

  private static string? StringAt(byte[] table, uint offset)
    => offset >= (uint)table.Length ? null : Ascii(table.AsSpan((int)offset));

  private byte[]? ReadAt(long offset, long size) {
    if (offset <= 0 || size <= 0 || size > _MaxTableBytes)
      return null;

    var bytes = new byte[size];
    var got = this._read(offset, bytes);
    return got <= 0 ? null : got == bytes.Length ? bytes : bytes[..got];
  }

  private ushort U16(ReadOnlySpan<byte> bytes, int at) => at + 2 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16BigEndian(bytes[at..])
    : (ushort)0;

  private uint U32(ReadOnlySpan<byte> bytes, int at) => at + 4 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[at..])
    : 0u;

  private int I32(ReadOnlySpan<byte> bytes, int at) => (int)this.U32(bytes, at);

  private ulong U64(ReadOnlySpan<byte> bytes, int at) => at + 8 <= bytes.Length
    ? this._little ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt64BigEndian(bytes[at..])
    : 0ul;

  private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

  private static string Hex(long value) => Hex((ulong)value);

  private static string Hex(uint value) => Hex((ulong)value);

  private static string Decimal(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(uint value) => Decimal((ulong)value);

  #endregion

}
