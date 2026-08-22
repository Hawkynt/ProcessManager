using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Everything §53 asks about a Portable Executable, read from the file and from nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Ranges rather than a whole file, which is what separates this from the two PE readers that
/// already exist. <see cref="ImageFormat"/> reads four bytes to say whether a mapping is a managed
/// assembly; the Windows probe's version-resource walk takes the file as one array because §14 asks
/// it about small images. An inspector is pointed at whatever somebody double-clicked, and a
/// three-hundred-megabyte image is a thing that exists — so every read here is a range through
/// <see cref="ElfImage.ElfRead"/>, the same reader the ELF side takes (PRD §5.4, §9.2).
/// </para>
/// <para>
/// <b>The certificate table is a file offset and not an address.</b> Data directory four is the one
/// entry of the sixteen whose first field is not an RVA, because the signature is appended after
/// everything the loader maps and therefore has no address at all. Resolving it through the section
/// table — which is right for the other fifteen — lands in the middle of whatever section happens to
/// cover that number, and reports a signature that is not there.
/// </para>
/// <para>
/// Every offset in the file is somebody else's arithmetic. The import and resource structures are
/// trees of self-relative offsets, and an image with a cycle or a forward reference past its own end
/// is a shape that exists — so every walk is bounded by count as well as by length, and a malformed
/// image yields fewer rows rather than an exception in a viewer.
/// </para>
/// </remarks>
internal sealed class PeInspector {

  #region shapes

  private readonly record struct Section(
    string Name,
    uint VirtualSize,
    uint VirtualAddress,
    uint RawSize,
    uint RawOffset,
    uint Characteristics
  );

  private readonly record struct Directory(uint Rva, uint Size);

  #endregion

  #region constants

  private const int _MaxSections = 4096;

  /// <summary>How far into a file a PE header may be. Real ones are under a kilobyte in.</summary>
  private const int _MaxPeOffset = 1 << 20;

  /// <summary>How much of one structure is read whole. Beyond it a table is not one this walks.</summary>
  private const int _MaxTableBytes = 16 * 1024 * 1024;

  private const int _MaxRows = 20_000;

  /// <summary>A resource tree deeper than this is a cycle, whatever it claims to be.</summary>
  private const int _MaxResourceDepth = 3;

  /// <summary>How much of a manifest is shown. Real ones are a few hundred bytes of XML.</summary>
  private const int _MaxManifestBytes = 8 * 1024;

  private const ushort _Pe32 = 0x010B;
  private const ushort _Pe32Plus = 0x020B;

  private const int _DirectoryExport = 0;
  private const int _DirectoryImport = 1;
  private const int _DirectoryResource = 2;
  private const int _DirectoryCertificate = 4;
  private const int _DirectoryBaseRelocation = 5;
  private const int _DirectoryDebug = 6;
  private const int _DirectoryLoadConfig = 10;
  private const int _DirectoryDelayImport = 13;
  private const int _DirectoryClr = 14;

  private const ushort _ResourceVersion = 16;
  private const ushort _ResourceManifest = 24;

  #endregion

  private readonly ElfImage.ElfRead _read;
  private readonly byte[] _coff;
  private readonly byte[] _optional;
  private readonly Section[] _sections;
  private readonly Directory[] _directories;
  private readonly bool _is64;

  public long Length { get; }

  private PeInspector(
    ElfImage.ElfRead read,
    long length,
    byte[] coff,
    byte[] optional,
    Section[] sections,
    Directory[] directories,
    bool is64
  ) {
    this._read = read;
    this.Length = length;
    this._coff = coff;
    this._optional = optional;
    this._sections = sections;
    this._directories = directories;
    this._is64 = is64;
  }

  /// <summary>Reads the headers, or hands back null when these bytes are not a PE image.</summary>
  /// <remarks>
  /// The <c>MZ</c> at the front proves nothing: every PE has one and so does a DOS program from 1990.
  /// The offset at <c>0x3C</c> is where the real header is, and a file whose first two bytes are
  /// <c>MZ</c> and whose header is not there is not a PE.
  /// </remarks>
  public static PeInspector? TryOpen(ElfImage.ElfRead read, long length) {
    ArgumentNullException.ThrowIfNull(read);

    var dos = new byte[64];
    if (read(0, dos) < 64 || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
      return null;

    var peAt = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3C));
    if (peAt is 0 or > _MaxPeOffset)
      return null;

    // The signature, the twenty-byte COFF header, and enough of what follows to hold the largest
    // optional header anybody writes.
    var head = new byte[24];
    if (read(peAt, head) < 24)
      return null;

    if (head[0] != (byte)'P' || head[1] != (byte)'E' || head[2] != 0 || head[3] != 0)
      return null;

    var coff = head[4..24];
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(2));
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(16));
    if (sectionCount > _MaxSections)
      return null;

    var optional = new byte[optionalSize];
    if (optionalSize > 0 && read(peAt + 24, optional) < optional.Length)
      optional = [];

    var is64 = optional.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(optional) == _Pe32Plus;
    var directories = ReadDirectories(optional, is64);

    var table = new byte[sectionCount * 40];
    var sections = Array.Empty<Section>();
    if (sectionCount > 0 && read(peAt + 24 + optionalSize, table) == table.Length) {
      sections = new Section[sectionCount];
      for (var i = 0; i < sectionCount; ++i) {
        var entry = table.AsSpan(i * 40, 40);
        var nul = entry[..8].IndexOf((byte)0);
        sections[i] = new(
          Encoding.ASCII.GetString(entry[..(nul < 0 ? 8 : nul)]),
          BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
          BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
          BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]),
          BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]),
          BinaryPrimitives.ReadUInt32LittleEndian(entry[36..])
        );
      }
    }

    return new(read, length, coff, optional, sections, directories, is64);
  }

  /// <summary>
  /// The data directories, however many the file says it wrote.
  /// </summary>
  /// <remarks>
  /// The count is in the file and is not necessarily sixteen. Reading a directory a linker did not
  /// write reads whatever follows the header, which on a small image is the section table — and a
  /// section name read as an address is a resource tree at a plausible-looking offset.
  /// </remarks>
  private static Directory[] ReadDirectories(byte[] optional, bool is64) {
    var countAt = is64 ? 108 : 92;
    if (countAt + 4 > optional.Length)
      return [];

    var count = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(countAt));
    if (count > 16)
      count = 16;

    var found = new List<Directory>((int)count);
    for (var i = 0; i < count; ++i) {
      var at = countAt + 4 + (i * 8);
      if (at + 8 > optional.Length)
        break;

      found.Add(new(
        BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(at)),
        BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(at + 4))
      ));
    }

    return [.. found];
  }

  #region reading

  /// <summary>
  /// Turns an address in the loaded image into an offset in the file.
  /// </summary>
  /// <remarks>
  /// A section's virtual size is routinely larger than its bytes on disk — the loader zero-fills the
  /// remainder — so an address inside that tail has no file offset at all rather than one just past
  /// the section.
  /// </remarks>
  private long? OffsetOf(uint rva) {
    foreach (var section in this._sections) {
      if (rva < section.VirtualAddress)
        continue;

      var delta = rva - section.VirtualAddress;
      if (delta >= section.VirtualSize && delta >= section.RawSize)
        continue;

      if (delta >= section.RawSize)
        return null;

      return section.RawOffset + delta;
    }

    // Everything below the first section's address is in the headers, which are mapped one to one.
    return this._sections.Length > 0 && rva < this._sections[0].VirtualAddress ? rva : null;
  }

  private byte[]? ReadAt(long offset, long size) {
    if (offset <= 0 || size <= 0 || size > _MaxTableBytes)
      return null;

    if (this.Length > 0 && offset >= this.Length)
      return null;

    var bytes = new byte[size];
    var got = this._read(offset, bytes);
    return got <= 0 ? null : got == bytes.Length ? bytes : bytes[..got];
  }

  private byte[]? ReadRva(uint rva, long size) => this.OffsetOf(rva) is { } at ? this.ReadAt(at, size) : null;

  /// <summary>A NUL-terminated ASCII string at an address, which is how PE writes every name.</summary>
  private string? StringAtRva(uint rva, int maximum = 512) {
    var bytes = this.ReadRva(rva, maximum);
    if (bytes is null)
      return null;

    var nul = bytes.AsSpan().IndexOf((byte)0);
    var span = nul < 0 ? bytes.AsSpan() : bytes.AsSpan(0, nul);
    return span.IsEmpty ? null : Encoding.UTF8.GetString(span);
  }

  private Directory DirectoryAt(int index)
    => index < this._directories.Length ? this._directories[index] : default;

  #endregion

  #region pages

  public BinaryView View(BinaryPage page) => page switch {
    BinaryPage.Headers => this.HeadersPage(),
    BinaryPage.Segments => BinaryView.Empty(
      "Program headers",
      "PE has no segment table. What a loader needs is the section table and the sixteen data "
      + "directories, and both are pages of their own here."
    ),
    BinaryPage.Sections => this.SectionsPage(),
    BinaryPage.Dynamic => this.DirectoriesPage(),
    BinaryPage.Dependencies => this.DependenciesPage(),
    BinaryPage.Imports => this.ImportsPage(),
    BinaryPage.Exports => this.ExportsPage(),
    BinaryPage.Symbols => this.SymbolsPage(),
    BinaryPage.Relocations => this.RelocationsPage(),
    BinaryPage.Resources => this.ResourcesPage(),
    BinaryPage.Debug => this.DebugPage(),
    BinaryPage.Security => this.SecurityPage(),
    BinaryPage.Signature => this.SignaturePage(),
    _ => this.SummaryPage(),
  };

  private BinaryView SummaryPage() {
    var rows = new List<string[]>();
    rows.Add(["format", "Portable Executable"]);
    rows.Add(["class", this._is64 ? "PE32+ (64-bit)" : "PE32 (32-bit)"]);
    rows.Add(["machine", MachineName(U16(this._coff, 0))]);
    rows.Add(["kind", (U16(this._coff, 18) & 0x2000) != 0 ? "dynamic-link library" : "executable"]);
    rows.Add(["runtime", this.DirectoryAt(_DirectoryClr) is { Rva: > 0, Size: > 0 }
      ? "managed — it carries a CLI header"
      : "native Windows code"]);
    rows.Add(["subsystem", SubsystemName(U16(this._optional, 68))]);
    rows.Add(["linked", Timestamp(U32(this._coff, 4))]);
    rows.Add(["entry point", Hex(U32(this._optional, 16))]);
    rows.Add(["image base", Hex(this._is64 ? U64(this._optional, 24) : U32(this._optional, 28))]);
    rows.Add(["image size", Decimal(U32(this._optional, 56)) + " bytes when loaded"]);
    rows.Add(["file size", this.Length >= 0 ? Decimal(this.Length) + " bytes" : "unknown"]);
    rows.Add(["sections", Decimal(this._sections.Length)]);
    rows.Add(["data directories", Decimal(this._directories.Length)]);
    rows.Add(["imports", this.ImportedLibraries().Count is var count && count > 0
      ? Decimal(count) + " libraries"
      : "none"]);
    rows.Add(["signed", this.DirectoryAt(_DirectoryCertificate) is { Rva: > 0, Size: > 0 } certificate
      ? $"a certificate table of {Decimal(certificate.Size)} bytes is appended"
      : "no certificate table — it is unsigned, or signed by a catalogue rather than in the file"]);

    return BinaryView.Facts("Summary", rows);
  }

  private BinaryView HeadersPage() {
    var rows = new List<string[]>();
    rows.Add(["dos signature", "MZ"]);
    rows.Add(["machine", MachineName(U16(this._coff, 0))]);
    rows.Add(["sections", Decimal(U16(this._coff, 2))]);
    rows.Add(["time date stamp", Timestamp(U32(this._coff, 4))]);
    rows.Add(["symbol table at", Hex(U32(this._coff, 8))]);
    rows.Add(["symbols", Decimal(U32(this._coff, 12))]);
    rows.Add(["optional header size", Decimal(U16(this._coff, 16))]);
    rows.Add(["characteristics", $"{Hex(U16(this._coff, 18))}  {FileCharacteristics(U16(this._coff, 18))}"]);

    if (this._optional.Length < 2)
      return BinaryView.Facts("PE headers", rows, "This image has no optional header, which only a relocatable object omits.");

    rows.Add(["magic", this._is64 ? "PE32+" : "PE32"]);
    rows.Add(["linker version", $"{Decimal(this._optional[2])}.{Decimal(this._optional[3])}"]);
    rows.Add(["code size", Decimal(U32(this._optional, 4))]);
    rows.Add(["initialised data", Decimal(U32(this._optional, 8))]);
    rows.Add(["uninitialised data", Decimal(U32(this._optional, 12))]);
    rows.Add(["entry point", Hex(U32(this._optional, 16))]);
    rows.Add(["base of code", Hex(U32(this._optional, 20))]);
    // BaseOfData exists only in PE32: the four bytes it occupies are the top half of the 64-bit
    // image base in PE32+, which is why every field after this one is at a different offset in the
    // two shapes.
    if (!this._is64)
      rows.Add(["base of data", Hex(U32(this._optional, 24))]);

    rows.Add(["image base", Hex(this._is64 ? U64(this._optional, 24) : U32(this._optional, 28))]);
    rows.Add(["section alignment", Hex(U32(this._optional, 32))]);
    rows.Add(["file alignment", Hex(U32(this._optional, 36))]);
    rows.Add(["os version", $"{Decimal(U16(this._optional, 40))}.{Decimal(U16(this._optional, 42))}"]);
    rows.Add(["image version", $"{Decimal(U16(this._optional, 44))}.{Decimal(U16(this._optional, 46))}"]);
    rows.Add(["subsystem version", $"{Decimal(U16(this._optional, 48))}.{Decimal(U16(this._optional, 50))}"]);
    rows.Add(["size of image", Decimal(U32(this._optional, 56))]);
    rows.Add(["size of headers", Decimal(U32(this._optional, 60))]);
    rows.Add(["checksum", Hex(U32(this._optional, 64))]);
    rows.Add(["subsystem", SubsystemName(U16(this._optional, 68))]);
    rows.Add(["dll characteristics", $"{Hex(U16(this._optional, 70))}  {DllCharacteristics(U16(this._optional, 70))}"]);
    rows.Add(["stack reserve", Decimal(this._is64 ? U64(this._optional, 72) : U32(this._optional, 72))]);
    rows.Add(["stack commit", Decimal(this._is64 ? U64(this._optional, 80) : U32(this._optional, 76))]);
    rows.Add(["heap reserve", Decimal(this._is64 ? U64(this._optional, 88) : U32(this._optional, 80))]);
    rows.Add(["heap commit", Decimal(this._is64 ? U64(this._optional, 96) : U32(this._optional, 84))]);
    rows.Add(["data directories", Decimal(U32(this._optional, this._is64 ? 108 : 92))]);
    return BinaryView.Facts("PE headers", rows);
  }

  private BinaryView SectionsPage() {
    var rows = new List<string[]>(this._sections.Length);
    foreach (var section in this._sections)
      rows.Add([
        section.Name.Length > 0 ? section.Name : "—",
        Hex(section.VirtualAddress),
        Hex(section.VirtualSize),
        Hex(section.RawOffset),
        Hex(section.RawSize),
        SectionCharacteristics(section.Characteristics),
      ]);

    return new(
      "Sections",
      ["Name", "Address", "Virtual size", "File offset", "File size", "Characteristics"],
      rows,
      rows.Count == 0 ? "This image declares no sections." : null
    );
  }

  private BinaryView DirectoriesPage() {
    var rows = new List<string[]>(this._directories.Length);
    for (var i = 0; i < this._directories.Length; ++i) {
      var directory = this._directories[i];
      rows.Add([
        Decimal(i),
        DirectoryName(i),
        Hex(directory.Rva),
        Decimal(directory.Size),
        directory.Rva == 0 && directory.Size == 0 ? "not written" : this.WhichSection(i, directory),
      ]);
    }

    return new(
      "Data directories",
      ["#", "Directory", "Address", "Size", "In"],
      rows,
      "The certificate table is the one entry whose first field is a **file offset** rather than an "
      + "address: the signature is appended after everything the loader maps, so it has no address "
      + "at all. Resolving it as an address lands in whichever section happens to cover that number."
    );
  }

  private string WhichSection(int index, in Directory directory) {
    if (index == _DirectoryCertificate)
      return "appended after the image";

    foreach (var section in this._sections)
      if (directory.Rva >= section.VirtualAddress && directory.Rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.RawSize))
        return section.Name.Length > 0 ? section.Name : "—";

    return "no section covers it";
  }

  /// <summary>The libraries the import and delay-import tables name, in the order they name them.</summary>
  private List<(string Name, bool Delayed)> ImportedLibraries() {
    var found = new List<(string, bool)>();
    var imports = this.DirectoryAt(_DirectoryImport);
    if (imports is { Rva: > 0, Size: >= 20 } && this.ReadRva(imports.Rva, imports.Size) is { } table)
      // IMAGE_IMPORT_DESCRIPTOR is five 32-bit words and the array ends at an all-zero one.
      for (var at = 0; at + 20 <= table.Length; at += 20) {
        var name = U32(table, at + 12);
        if (U32(table, at) == 0 && name == 0 && U32(table, at + 16) == 0)
          break;

        found.Add((this.StringAtRva(name) ?? "—", false));
      }

    var delayed = this.DirectoryAt(_DirectoryDelayImport);
    if (delayed is { Rva: > 0, Size: >= 32 } && this.ReadRva(delayed.Rva, delayed.Size) is { } delay)
      // IMAGE_DELAYLOAD_DESCRIPTOR is eight 32-bit words. Its addresses are RVAs whenever the low
      // bit of Attributes is set, which every linker since Visual C++ 6 has set.
      for (var at = 0; at + 32 <= delay.Length; at += 32) {
        var name = U32(delay, at + 4);
        if (name == 0 && U32(delay, at + 12) == 0)
          break;

        found.Add((this.StringAtRva(name) ?? "—", true));
      }

    return found;
  }

  private BinaryView DependenciesPage() {
    var rows = new List<string[]>();
    foreach (var (name, delayed) in this.ImportedLibraries())
      rows.Add([delayed ? "delay-loaded" : "imported", name]);

    if (this.DirectoryAt(_DirectoryClr) is { Rva: > 0, Size: >= 72 } clr && this.ReadRva(clr.Rva, clr.Size) is { Length: >= 72 } header) {
      rows.Add(["cli runtime", $"{Decimal(U16(header, 4))}.{Decimal(U16(header, 6))}"]);
      rows.Add(["cli flags", Hex(U32(header, 16))]);
      rows.Add(["cli metadata", $"{Hex(U32(header, 8))}, {Decimal(U32(header, 12))} bytes"]);
    }

    return BinaryView.Facts(
      "Dependencies",
      rows,
      rows.Count == 0
        ? "This image imports nothing by name, which a driver and a fully statically linked program "
          + "both look like."
        : "What the file itself names. A managed assembly's real dependencies are its metadata "
          + "references rather than this table, which for a .NET image names one library and no more."
    );
  }

  private BinaryView ImportsPage() {
    var rows = new List<string[]>();
    var truncated = false;
    var imports = this.DirectoryAt(_DirectoryImport);
    if (imports is { Rva: > 0, Size: >= 20 } && this.ReadRva(imports.Rva, imports.Size) is { } table)
      for (var at = 0; at + 20 <= table.Length && !truncated; at += 20) {
        var lookup = U32(table, at);
        var name = U32(table, at + 12);
        var address = U32(table, at + 16);
        if (lookup == 0 && name == 0 && address == 0)
          break;

        // The lookup table is the one to walk: the address table holds the same entries before the
        // loader runs and the resolved addresses after it, so an image that was dumped from memory
        // has names in one of them and pointers in the other.
        this.WalkThunks(lookup != 0 ? lookup : address, this.StringAtRva(name) ?? "—", false, rows, ref truncated);
      }

    var delayed = this.DirectoryAt(_DirectoryDelayImport);
    if (delayed is { Rva: > 0, Size: >= 32 } && this.ReadRva(delayed.Rva, delayed.Size) is { } delay)
      for (var at = 0; at + 32 <= delay.Length && !truncated; at += 32) {
        var name = U32(delay, at + 4);
        var names = U32(delay, at + 16);
        if (name == 0 && names == 0)
          break;

        this.WalkThunks(names, this.StringAtRva(name) ?? "—", true, rows, ref truncated);
      }

    return new(
      "Imports",
      ["Library", "Kind", "Ordinal", "Hint", "Name"],
      rows,
      Note(
        truncated,
        rows.Count == 0 ? "This image imports nothing by name." : null
      )
    );
  }

  /// <summary>
  /// One import lookup table: an array of thunks, ending at a nought.
  /// </summary>
  /// <remarks>
  /// A thunk with its top bit set is an import by ordinal and has no name anywhere in the file —
  /// which is a real answer and the whole reason the ordinal has a column of its own. Everything
  /// else is an address of a hint/name pair: a sixteen-bit hint the loader may use to skip a search,
  /// then the name.
  /// </remarks>
  private void WalkThunks(uint rva, string library, bool delayed, List<string[]> rows, ref bool truncated) {
    if (rva == 0)
      return;

    var width = this._is64 ? 8 : 4;
    var ordinalBit = this._is64 ? 0x8000_0000_0000_0000ul : 0x8000_0000ul;
    for (var i = 0; i < _MaxRows; ++i) {
      var thunk = this.ReadRva((uint)(rva + (i * width)), width);
      if (thunk is null || thunk.Length < width)
        return;

      var value = this._is64 ? U64(thunk, 0) : U32(thunk, 0);
      if (value == 0)
        return;

      if (rows.Count >= _MaxRows) {
        truncated = true;
        return;
      }

      if ((value & ordinalBit) != 0) {
        rows.Add([library, delayed ? "delayed, ordinal" : "ordinal", Decimal(value & 0xFFFF), "—", "—"]);
        continue;
      }

      var pair = this.ReadRva((uint)value, 2 + 512);
      var hint = pair is { Length: >= 2 } ? U16(pair, 0) : 0;
      var name = pair is { Length: > 2 } ? Ascii(pair.AsSpan(2)) : null;
      rows.Add([library, delayed ? "delayed" : "name", "—", Decimal(hint), name ?? "—"]);
    }
  }

  private BinaryView ExportsPage() {
    var directory = this.DirectoryAt(_DirectoryExport);
    if (directory is not { Rva: > 0, Size: >= 40 } || this.ReadRva(directory.Rva, 40) is not { Length: >= 40 } header)
      return BinaryView.Empty(
        "Exports",
        "This image has no export directory, which is what an executable that is not also a library "
        + "looks like."
      );

    var ordinalBase = U32(header, 16);
    var functionCount = Math.Min(U32(header, 20), _MaxRows);
    var nameCount = Math.Min(U32(header, 24), _MaxRows);
    var functions = this.ReadRva(U32(header, 28), functionCount * 4L);
    var names = this.ReadRva(U32(header, 32), nameCount * 4L);
    var ordinals = this.ReadRva(U32(header, 36), nameCount * 2L);

    // Ordinal → name, because the two tables are parallel to each other and not to the function
    // table: an export may have an ordinal and no name, and reading the name table as though it were
    // indexed by ordinal puts every name against the wrong function.
    var byOrdinal = new Dictionary<uint, string>();
    if (names is not null && ordinals is not null)
      for (var i = 0; i < nameCount; ++i) {
        if ((i * 4) + 4 > names.Length || (i * 2) + 2 > ordinals.Length)
          break;

        if (this.StringAtRva(U32(names, i * 4)) is { } name)
          byOrdinal[U16(ordinals, i * 2)] = name;
      }

    var rows = new List<string[]>();
    for (var i = 0u; i < functionCount; ++i) {
      if (functions is null || (i * 4) + 4 > functions.Length)
        break;

      var address = U32(functions, (int)i * 4);
      if (address == 0)
        continue;

      // An address inside the export directory itself is not code: it is the name of another
      // library's export, and this one is a forwarder to it. Reporting it as an address would be an
      // entry point into the middle of a string table.
      var forwarded = address >= directory.Rva && address < directory.Rva + directory.Size;
      rows.Add([
        Decimal(ordinalBase + i),
        byOrdinal.TryGetValue(i, out var named) ? named : "—",
        forwarded ? "forwarder" : Hex(address),
        forwarded ? this.StringAtRva(address) ?? "—" : "—",
      ]);
    }

    return new(
      "Exports",
      ["Ordinal", "Name", "Address", "Forwards to"],
      rows,
      $"Exported as \"{this.StringAtRva(U32(header, 12)) ?? "—"}\"."
    );
  }

  private BinaryView SymbolsPage() {
    var pointer = U32(this._coff, 8);
    var count = U32(this._coff, 12);
    if (pointer == 0 || count == 0)
      return BinaryView.Empty(
        "Symbols",
        "This image carries no COFF symbol table, which is the normal case: the linker strips it and "
        + "puts the names in a PDB instead. The exports page is what a PE publishes by name, and the "
        + "debug page says which PDB the names went into."
      );

    var rows = new List<string[]>();
    var symbols = this.ReadAt(pointer, Math.Min(count, (uint)_MaxRows) * 18L);
    // The string table follows the symbols and holds every name longer than eight characters; its
    // first four bytes are its own length, counting themselves.
    var stringsAt = pointer + (count * 18L);
    var strings = this.ReadAt(stringsAt, Math.Min(_MaxTableBytes, Math.Max(0, this.Length - stringsAt)));
    for (var i = 0; symbols is not null && (i * 18) + 18 <= symbols.Length; ++i) {
      var entry = symbols.AsSpan(i * 18, 18);
      var name = U32(symbols, i * 18) == 0 && strings is not null
        ? StringAt(strings, U32(symbols, (i * 18) + 4))
        : Ascii(entry[..8]);

      rows.Add([
        name ?? "—",
        Hex(U32(symbols, (i * 18) + 8)),
        Decimal(BinaryPrimitives.ReadInt16LittleEndian(entry[12..])),
        Hex(U16(symbols, (i * 18) + 14)),
        Decimal(entry[16]),
      ]);

      // The auxiliary records that follow a symbol are its own, not symbols: skipping them is what
      // keeps the walk in step with the table.
      i += entry[17];
    }

    return new("Symbols", ["Name", "Value", "Section", "Type", "Class"], rows);
  }

  private BinaryView RelocationsPage() {
    var directory = this.DirectoryAt(_DirectoryBaseRelocation);
    if (directory is not { Rva: > 0, Size: > 0 } || this.ReadRva(directory.Rva, directory.Size) is not { } table)
      return BinaryView.Empty(
        "Relocations",
        (U16(this._coff, 18) & 0x0001) != 0
          ? "The relocations were stripped, so this image can only load at the address it names — "
            + "which is also why it cannot be randomised."
          : "This image has no base relocation table."
      );

    var rows = new List<string[]>();
    long entries = 0;
    for (var at = 0; at + 8 <= table.Length;) {
      var page = U32(table, at);
      var size = (int)U32(table, at + 4);
      if (size < 8 || at + size > table.Length)
        break;

      var count = (size - 8) / 2;
      var kinds = new Dictionary<int, int>();
      for (var i = 0; i < count; ++i) {
        var word = U16(table, at + 8 + (i * 2));
        // The top four bits are the fixup type and the bottom twelve the offset into the page. A
        // type of nought is padding to a four-byte boundary rather than a relocation.
        var type = word >> 12;
        kinds[type] = kinds.TryGetValue(type, out var seen) ? seen + 1 : 1;
      }

      entries += count;
      var names = new List<string>();
      foreach (var pair in kinds.OrderByDescending(static p => p.Value))
        names.Add($"{Decimal(pair.Value)} {RelocationTypeName(pair.Key)}");

      rows.Add([Hex(page), Decimal(size), Decimal(count), string.Join(", ", names)]);
      at += size;
      if (rows.Count >= _MaxRows)
        break;
    }

    return new(
      "Relocations",
      ["Page", "Block size", "Entries", "Types"],
      rows,
      $"{Decimal(entries)} fixups across {Decimal(rows.Count)} pages."
    );
  }

  private BinaryView ResourcesPage() {
    var directory = this.DirectoryAt(_DirectoryResource);
    if (directory is not { Rva: > 0, Size: > 0 } || this.ReadRva(directory.Rva, directory.Size) is not { } tree)
      return BinaryView.Empty("Resources", "This image carries no resource directory.");

    var rows = new List<string[]>();
    Walk(0, 0, null, null);
    return new(
      "Resources",
      ["Type", "Name", "Language", "Address", "Size"],
      rows,
      "The tree as the resource compiler wrote it. A version resource is where a Windows file keeps "
      + "its version, description and company; a manifest is where it declares the runtime it wants "
      + "and whether it needs elevation."
    );

    void Walk(int offset, int depth, string? type, string? name) {
      if (depth > _MaxResourceDepth || offset < 0 || offset + 16 > tree.Length || rows.Count >= _MaxRows)
        return;

      var named = U16(tree, offset + 12);
      var numbered = U16(tree, offset + 14);
      var count = named + numbered;
      var first = offset + 16;
      if (first + (count * 8L) > tree.Length)
        return;

      for (var i = 0; i < count; ++i) {
        var entry = first + (i * 8);
        var key = U32(tree, entry);
        var data = U32(tree, entry + 4);
        // The high bit of the first word says the low bits are an offset to a counted UTF-16 name
        // rather than an integer id; the high bit of the second says the low bits are another
        // directory rather than a leaf.
        var label = i < named || (key & 0x8000_0000) != 0
          ? ReadName(key & 0x7FFF_FFFF) ?? "—"
          : depth == 0 ? ResourceTypeName((ushort)key) : Decimal(key & 0x7FFF_FFFF);

        if ((data & 0x8000_0000) != 0) {
          Walk((int)(data & 0x7FFF_FFFF), depth + 1, depth == 0 ? label : type, depth == 1 ? label : name);
          continue;
        }

        var leaf = (int)(data & 0x7FFF_FFFF);
        if (leaf < 0 || leaf + 16 > tree.Length)
          continue;

        var address = U32(tree, leaf);
        var size = U32(tree, leaf + 4);
        rows.Add([type ?? label, name ?? (depth >= 1 ? label : "—"), depth >= 2 ? label : "—", Hex(address), Decimal(size)]);

        // The manifest is the one resource whose *content* answers a question somebody asks of an
        // inspector: which runtime the image wants, and whether it demands elevation. It is plain
        // XML, so it is shown rather than parsed — a substring search for an attribute would be a
        // reader that is right until somebody's resource compiler writes the same thing differently.
        if ((type ?? label) == ResourceTypeName(_ResourceManifest) && this.ReadRva(address, Math.Min(size, _MaxManifestBytes)) is { } manifest)
          rows.Add(["  manifest", Ascii(manifest) ?? "—"]);
      }
    }

    string? ReadName(uint at) {
      if (at + 2 > (uint)tree.Length)
        return null;

      var length = U16(tree, (int)at);
      return (at + 2 + (length * 2)) > (uint)tree.Length
        ? null
        : Encoding.Unicode.GetString(tree, (int)at + 2, length * 2);
    }
  }

  private BinaryView DebugPage() {
    var rows = new List<string[]>();
    var directory = this.DirectoryAt(_DirectoryDebug);
    if (directory is not { Rva: > 0, Size: >= 28 } || this.ReadRva(directory.Rva, directory.Size) is not { } table)
      return BinaryView.Empty(
        "Debug information",
        "This image has no debug directory, so it names no PDB and carries no build identity."
      );

    for (var at = 0; at + 28 <= table.Length; at += 28) {
      var type = U32(table, at + 12);
      var size = U32(table, at + 16);
      var pointer = U32(table, at + 24);
      rows.Add([DebugTypeName(type), Timestamp(U32(table, at + 4)), Decimal(size), Hex(pointer)]);

      var payload = this.ReadAt(pointer, Math.Min(size, 4096));
      switch (type) {
        // CodeView: "RSDS", a GUID, an age, then the path of the PDB as the linker saw it. That path
        // is the machine the image was built on, which is why it is worth reading and worth being
        // careful about — it is somebody's directory layout.
        case 2 when payload is { Length: >= 25 } && payload[0] == (byte)'R' && payload[1] == (byte)'S' && payload[2] == (byte)'D' && payload[3] == (byte)'S':
          rows.Add(["  pdb signature", new Guid(payload.AsSpan(4, 16)).ToString()]);
          rows.Add(["  pdb age", Decimal(U32(payload, 20))]);
          rows.Add(["  pdb path", Ascii(payload.AsSpan(24)) ?? "—"]);
          break;
        // The extended DLL characteristics, which is where a shadow-stack declaration lives: there
        // is no bit for it in the optional header, so an image built for CET says so here or
        // nowhere.
        case 20 when payload is { Length: >= 2 }:
          rows.Add(["  extended", ExtendedCharacteristics(U16(payload, 0))]);
          break;
        case 16:
          rows.Add(["  reproducible", "the build is deterministic; this is its content hash"]);
          break;
        default:
          break;
      }
    }

    return new(
      "Debug information",
      ["Type", "Stamped", "Size", "File offset"],
      rows,
      "What the file says about itself. Whether the PDB it names is anywhere this machine can reach "
      + "it is a different question, and not one these bytes answer."
    );
  }

  private BinaryView SecurityPage() {
    var characteristics = U16(this._optional, 70);
    var file = U16(this._coff, 18);
    var rows = new List<string[]>();
    rows.Add(["aslr", (characteristics & 0x0040) != 0
      ? (characteristics & 0x0020) != 0 ? "yes, with high-entropy 64-bit addresses" : "yes"
      : (file & 0x0001) != 0
        ? "no — the relocations are stripped, so it can only load where it says"
        : "no — DYNAMIC_BASE is not set"]);
    rows.Add(["dep", (characteristics & 0x0100) != 0 ? "yes — NX_COMPAT" : "not declared"]);
    rows.Add(["control flow guard", (characteristics & 0x4000) != 0 ? "yes — GUARD_CF" : "not declared"]);
    rows.Add(["safe seh", (characteristics & 0x0400) != 0
      ? "no exception handlers at all — NO_SEH"
      : this._is64 ? "n/a on a 64-bit image; the handlers are a table rather than a chain" : "see the load configuration"]);
    rows.Add(["force integrity", (characteristics & 0x0080) != 0 ? "yes — the loader must verify the signature" : "no"]);
    rows.Add(["appcontainer", (characteristics & 0x1000) != 0 ? "yes" : "no"]);
    rows.Add(["isolation", (characteristics & 0x0200) != 0 ? "the manifest is ignored — NO_ISOLATION" : "the manifest applies"]);
    rows.Add(["large address aware", (file & 0x0020) != 0 ? "yes" : "no"]);

    // The extended characteristics live in a debug directory entry rather than in the optional
    // header, because the sixteen bits there were used up before shadow stacks existed.
    var extended = this.ExtendedDllCharacteristics();
    rows.Add(["shadow stack", extended is { } bits
      ? (bits & 0x0001) != 0 ? "compatible — CET_COMPAT" : "declared and not compatible"
      : "not declared — the image has no extended characteristics record"]);

    var config = this.DirectoryAt(_DirectoryLoadConfig);
    if (config is { Rva: > 0, Size: > 0 } && this.ReadRva(config.Rva, config.Size) is { } load) {
      var cookieAt = this._is64 ? 88 : 60;
      rows.Add(["security cookie", load.Length >= cookieAt + (this._is64 ? 8 : 4)
        ? Hex(this._is64 ? U64(load, cookieAt) : U32(load, cookieAt))
        : "—"]);

      // GuardFlags sits at the far end of the structure, past the four control-flow pointers. At
      // 0x70 — which is where the *check function pointer* is on a 64-bit image — the value read is
      // an address, and every image on the machine reports flags it does not have.
      var guardAt = this._is64 ? 144 : 88;
      if (load.Length >= guardAt + 4)
        rows.Add(["guard flags", $"{Hex(U32(load, guardAt))}  {GuardFlags(U32(load, guardAt))}"]);

      rows.Add(["load config size", Decimal(load.Length >= 4 ? U32(load, 0) : 0)]);
    } else {
      rows.Add(["load configuration", "none — there is no IMAGE_LOAD_CONFIG_DIRECTORY in this image"]);
    }

    return BinaryView.Facts(
      "Security properties",
      rows,
      "**What the file asks for, not what it got.** A DYNAMIC_BASE image is only randomised if the "
      + "system is randomising, and a GUARD_CF image is only guarded where the loader supports it. "
      + "Only the first half is readable from a file on disk."
    );
  }

  private ushort? ExtendedDllCharacteristics() {
    var directory = this.DirectoryAt(_DirectoryDebug);
    if (directory is not { Rva: > 0, Size: >= 28 } || this.ReadRva(directory.Rva, directory.Size) is not { } table)
      return null;

    for (var at = 0; at + 28 <= table.Length; at += 28) {
      if (U32(table, at + 12) != 20)
        continue;

      var payload = this.ReadAt(U32(table, at + 24), Math.Min(U32(table, at + 16), 16));
      if (payload is { Length: >= 2 })
        return U16(payload, 0);
    }

    return null;
  }

  private BinaryView SignaturePage() {
    var directory = this.DirectoryAt(_DirectoryCertificate);
    if (directory is not { Rva: > 0, Size: > 0 })
      return BinaryView.Empty(
        "Signature",
        "There is no certificate table. The image is unsigned, or it is covered by a catalogue file "
        + "rather than by a signature inside itself — which is how a great deal of Windows is signed "
        + "and is a different finding from \"nobody signed it\" (§70)."
      );

    // Directory four holds a file offset. See the class remarks: it is the only one of the sixteen
    // that does, and treating it as an address is the classic way to report a signature that is not
    // where this says it is.
    var rows = new List<string[]>();
    long at = directory.Rva;
    var end = at + directory.Size;
    while (at + 8 <= end) {
      var header = this.ReadAt(at, 8);
      if (header is not { Length: 8 })
        break;

      var length = U32(header, 0);
      if (length < 8 || at + length > end)
        break;

      rows.Add([
        Hex(at),
        Decimal(length),
        CertificateRevision(U16(header, 4)),
        CertificateType(U16(header, 6)),
      ]);

      // Every entry starts on an eight-byte boundary, and a length that does not is padded up to
      // one rather than followed exactly.
      var step = (length + 7) & ~7u;
      if (step == 0)
        break;

      at += step;
    }

    return new(
      "Signature",
      ["File offset", "Length", "Revision", "Type"],
      rows,
      "**What is here, not whether it is good.** This says a signature is attached and how big it "
      + "is; whether it still covers these bytes, who signed them and whether anybody on this "
      + "machine trusts that signer are three further questions, and §70 keeps all four apart."
    );
  }

  /// <summary>The regions of the file that hold code, for a strings scan restricted to them (§35).</summary>
  public IReadOnlyList<(long Offset, long Length, string Name)> ExecutableRegions {
    get {
      var found = new List<(long, long, string)>();
      foreach (var section in this._sections)
        if ((section.Characteristics & (0x0000_0020 | 0x2000_0000)) != 0 && section.RawSize > 0)
          found.Add((section.RawOffset, section.RawSize, section.Name.Length > 0 ? section.Name : "section"));

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

  private static string MachineName(ushort machine) => machine switch {
    0x014C => "Intel 386",
    0x0166 => "MIPS little endian",
    0x01C0 => "ARM",
    0x01C4 => "ARM Thumb-2",
    0x0200 => "Intel Itanium",
    0x5032 => "RISC-V 32",
    0x5064 => "RISC-V 64",
    0x6232 => "LoongArch 32",
    0x6264 => "LoongArch 64",
    0x8664 => "AMD x86-64",
    0xAA64 => "ARM64",
    0x0EBC => "EFI byte code",
    0 => "unknown — any machine",
    _ => $"machine {Hex(machine)}",
  };

  private static string SubsystemName(ushort subsystem) => subsystem switch {
    1 => "native — no subsystem, which is what a driver is",
    2 => "Windows GUI",
    3 => "Windows console",
    5 => "OS/2 console",
    7 => "POSIX console",
    9 => "Windows CE GUI",
    10 => "EFI application",
    11 => "EFI boot service driver",
    12 => "EFI runtime driver",
    13 => "EFI ROM",
    14 => "Xbox",
    16 => "Windows boot application",
    _ => $"subsystem {Decimal(subsystem)}",
  };

  private static string DirectoryName(int index) => index switch {
    0 => "export",
    1 => "import",
    2 => "resource",
    3 => "exception",
    4 => "certificate",
    5 => "base relocation",
    6 => "debug",
    7 => "architecture",
    8 => "global pointer",
    9 => "thread local storage",
    10 => "load configuration",
    11 => "bound import",
    12 => "import address table",
    13 => "delay import",
    14 => "CLI header",
    15 => "reserved",
    _ => Decimal(index),
  };

  private static string FileCharacteristics(ushort value) {
    var names = new List<string>();
    Add(0x0001, "RELOCS_STRIPPED");
    Add(0x0002, "EXECUTABLE_IMAGE");
    Add(0x0020, "LARGE_ADDRESS_AWARE");
    Add(0x0100, "32BIT_MACHINE");
    Add(0x0200, "DEBUG_STRIPPED");
    Add(0x1000, "SYSTEM");
    Add(0x2000, "DLL");
    Add(0x4000, "UP_SYSTEM_ONLY");
    return names.Count > 0 ? string.Join(" · ", names) : "none";

    void Add(int bit, string name) {
      if ((value & bit) != 0)
        names.Add(name);
    }
  }

  private static string DllCharacteristics(ushort value) {
    var names = new List<string>();
    Add(0x0020, "HIGH_ENTROPY_VA");
    Add(0x0040, "DYNAMIC_BASE");
    Add(0x0080, "FORCE_INTEGRITY");
    Add(0x0100, "NX_COMPAT");
    Add(0x0200, "NO_ISOLATION");
    Add(0x0400, "NO_SEH");
    Add(0x0800, "NO_BIND");
    Add(0x1000, "APPCONTAINER");
    Add(0x2000, "WDM_DRIVER");
    Add(0x4000, "GUARD_CF");
    Add(0x8000, "TERMINAL_SERVER_AWARE");
    return names.Count > 0 ? string.Join(" · ", names) : "none";

    void Add(int bit, string name) {
      if ((value & bit) != 0)
        names.Add(name);
    }
  }

  private static string ExtendedCharacteristics(ushort value) {
    var names = new List<string>();
    if ((value & 0x0001) != 0)
      names.Add("CET_COMPAT");

    if ((value & 0x0002) != 0)
      names.Add("CET_COMPAT_STRICT_MODE");

    if ((value & 0x0004) != 0)
      names.Add("CET_SET_CONTEXT_IP_VALIDATION_RELAXED");

    if ((value & 0x0008) != 0)
      names.Add("CET_DYNAMIC_APIS_ALLOW_IN_PROC");

    return names.Count > 0 ? string.Join(" · ", names) : Hex(value);
  }

  private static string GuardFlags(uint value) {
    var names = new List<string>();
    Add(0x0000_0100, "CF_INSTRUMENTED");
    Add(0x0000_0200, "CFW_INSTRUMENTED");
    Add(0x0000_0400, "CF_FUNCTION_TABLE_PRESENT");
    Add(0x0000_0800, "SECURITY_COOKIE_UNUSED");
    Add(0x0000_1000, "PROTECT_DELAYLOAD_IAT");
    Add(0x0000_2000, "DELAYLOAD_IAT_OWN_SECTION");
    Add(0x0000_4000, "CF_EXPORT_SUPPRESSION_INFO");
    Add(0x0000_8000, "CF_ENABLE_EXPORT_SUPPRESSION");
    Add(0x0001_0000, "CF_LONGJUMP_TABLE_PRESENT");
    Add(0x0002_0000, "RF_INSTRUMENTED");
    Add(0x0004_0000, "RF_ENABLE");
    Add(0x0008_0000, "RF_STRICT");
    Add(0x0010_0000, "RETPOLINE_PRESENT");
    Add(0x0040_0000, "EH_CONTINUATION_TABLE_PRESENT");
    return names.Count > 0 ? string.Join(" · ", names) : "none";

    void Add(uint bit, string name) {
      if ((value & bit) != 0)
        names.Add(name);
    }
  }

  private static string SectionCharacteristics(uint value) {
    var names = new List<string>();
    Add(0x0000_0020, "CODE");
    Add(0x0000_0040, "INITIALIZED_DATA");
    Add(0x0000_0080, "UNINITIALIZED_DATA");
    Add(0x0200_0000, "DISCARDABLE");
    Add(0x0400_0000, "NOT_CACHED");
    Add(0x0800_0000, "NOT_PAGED");
    Add(0x1000_0000, "SHARED");
    Add(0x2000_0000, "EXECUTE");
    Add(0x4000_0000, "READ");
    Add(0x8000_0000, "WRITE");
    return names.Count > 0 ? string.Join(" · ", names) : "none";

    void Add(uint bit, string name) {
      if ((value & bit) != 0)
        names.Add(name);
    }
  }

  private static string DebugTypeName(uint type) => type switch {
    0 => "unknown",
    1 => "COFF",
    2 => "CodeView",
    3 => "FPO",
    4 => "misc",
    5 => "exception",
    6 => "fixup",
    7 => "OMAP to source",
    8 => "OMAP from source",
    9 => "Borland",
    12 => "VC feature",
    13 => "POGO",
    14 => "IL-to-native",
    16 => "reproducible",
    17 => "embedded PDB",
    19 => "PDB checksum",
    20 => "extended DLL characteristics",
    _ => $"type {Decimal(type)}",
  };

  private static string ResourceTypeName(ushort type) => type switch {
    1 => "cursor",
    2 => "bitmap",
    3 => "icon",
    4 => "menu",
    5 => "dialog",
    6 => "string table",
    7 => "font directory",
    8 => "font",
    9 => "accelerator",
    10 => "raw data",
    11 => "message table",
    12 => "cursor group",
    14 => "icon group",
    _ResourceVersion => "version",
    17 => "dialog include",
    19 => "plug and play",
    20 => "VxD",
    21 => "animated cursor",
    22 => "animated icon",
    23 => "html",
    _ResourceManifest => "manifest",
    _ => Decimal(type),
  };

  private static string RelocationTypeName(int type) => type switch {
    0 => "ABSOLUTE (padding)",
    1 => "HIGH",
    2 => "LOW",
    3 => "HIGHLOW",
    4 => "HIGHADJ",
    5 => "MIPS_JMPADDR or ARM_MOV32",
    7 => "THUMB_MOV32",
    10 => "DIR64",
    _ => Decimal(type),
  };

  private static string CertificateRevision(ushort value) => value switch {
    0x0100 => "1.0",
    0x0200 => "2.0",
    _ => Hex(value),
  };

  private static string CertificateType(ushort value) => value switch {
    0x0001 => "X.509",
    0x0002 => "PKCS#7 signed data — Authenticode",
    0x0003 => "reserved",
    0x0004 => "PKCS#1 module signature",
    _ => Hex(value),
  };

  /// <summary>A UTC time stamp as the COFF header writes it: seconds since 1970.</summary>
  private static string Timestamp(uint seconds) => seconds switch {
    0 => "not stamped",
    // A deterministic build writes a content hash here rather than a time, which is why a .NET
    // assembly routinely claims to have been linked in 2078. Saying so beats printing the date.
    > 0x7FFF_FFFF => $"{Hex(seconds)} — not a time; a deterministic build writes a content hash here",
    _ => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z",
  };

  private static string? Ascii(ReadOnlySpan<byte> bytes) {
    var nul = bytes.IndexOf((byte)0);
    var span = nul < 0 ? bytes : bytes[..nul];
    return span.IsEmpty ? null : Encoding.UTF8.GetString(span);
  }

  private static string? StringAt(byte[] table, uint offset) {
    if (offset >= (uint)table.Length)
      return null;

    return Ascii(table.AsSpan((int)offset));
  }

  private static ushort U16(byte[] bytes, int at)
    => at + 2 <= bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at)) : (ushort)0;

  private static uint U32(byte[] bytes, int at)
    => at + 4 <= bytes.Length ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)) : 0u;

  private static ulong U64(byte[] bytes, int at)
    => at + 8 <= bytes.Length ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) : 0ul;

  private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);

  private static string Hex(long value) => Hex((ulong)value);

  private static string Hex(uint value) => Hex((ulong)value);

  private static string Decimal(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(ulong value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static string Decimal(uint value) => Decimal((ulong)value);

  #endregion

}
