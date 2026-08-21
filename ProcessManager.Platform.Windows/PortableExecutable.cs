using System.Buffers.Binary;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What a PE image says about itself: its subsystem, the machine it was built for, and the strings
/// its version resource carries (PRD §14).
/// </summary>
/// <param name="Machine">
/// The <c>IMAGE_FILE_MACHINE_*</c> value out of the COFF header — the program's own answer about
/// which instruction set it needs, which on a machine that runs more than one is not the machine's
/// answer.
/// </param>
/// <param name="Subsystem">
/// The <c>IMAGE_SUBSYSTEM_*</c> value out of the optional header: what the loader is expected to
/// give the image — a window station, a console, or nothing at all.
/// </param>
/// <param name="Description">The version resource's <c>FileDescription</c>.</param>
/// <param name="Company">Its <c>CompanyName</c>.</param>
/// <param name="Product">Its <c>ProductName</c>.</param>
/// <param name="ProductVersion">Its <c>ProductVersion</c>, as the publisher wrote it.</param>
/// <param name="FileVersion">Its <c>FileVersion</c>, likewise.</param>
/// <param name="FixedFileVersion">
/// The same file version as the four numbers <c>VS_FIXEDFILEINFO</c> carries, or
/// <see langword="null"/> where the resource has no fixed part.
/// </param>
/// <remarks>
/// The two file versions are kept apart on purpose. <see cref="FileVersion"/> is a string the
/// publisher typed and may say anything — "1.0 beta 3", "10.0.19041.1 (WinBuild.160101.0800)" —
/// while <see cref="FixedFileVersion"/> is the binary quadruple the installer and the loader compare.
/// They routinely disagree, and holding one of them would lose whichever half the reader wanted
/// (PRD §5.3). The second is also what makes this parser checkable without a Windows machine: the
/// same fact is written into the file twice, in two encodings, and a walk that lands in the wrong
/// place cannot make the two agree.
/// </remarks>
internal readonly record struct PeImageFacts(
  ushort Machine,
  ushort Subsystem,
  bool Is64Bit,
  string? Description,
  string? Company,
  string? Product,
  string? ProductVersion,
  string? FileVersion,
  string? FixedFileVersion
);

/// <summary>
/// Reads a PE image's own headers and its version resource, out of the file's bytes.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not marked as Windows-only, and deliberately taking a span rather than a path: it is
/// arithmetic over a documented on-disk format, so it is exercised on every CI leg against real PE
/// files rather than only on a machine that runs them (PRD §9.4). Every managed assembly this
/// repository builds is a PE image with a version resource in it, which is what the tests read.
/// </para>
/// <para>
/// Every offset is bounds-checked against the span before it is used. The input is a file somebody
/// else wrote, the resource tree is a tree of self-relative offsets, and an image with a cycle or a
/// forward reference past its own end is a thing that exists — so the walk is bounded by depth as
/// well as by length, and a malformed image yields no facts rather than an exception in a sampler.
/// </para>
/// </remarks>
internal static class PortableExecutable {

  /// <summary>The resource type id of a version resource: <c>RT_VERSION</c>.</summary>
  private const ushort _RT_VERSION = 16;

  /// <summary><c>VS_FIXEDFILEINFO.dwSignature</c>.</summary>
  private const uint _FIXED_FILE_INFO_SIGNATURE = 0xFEEF04BD;

  private const ushort _PE32_MAGIC = 0x010B;
  private const ushort _PE32PLUS_MAGIC = 0x020B;

  /// <summary>The data directory that points at the resource tree.</summary>
  private const int _RESOURCE_DIRECTORY_INDEX = 2;

  /// <summary>
  /// Everything this reader knows about one image, or <see langword="false"/> when the bytes are not
  /// a PE image at all.
  /// </summary>
  /// <remarks>
  /// A file that is a PE image but carries no version resource still succeeds, with the five strings
  /// null: "this program ships no version resource" is a true statement about a great many programs
  /// and is not the same as "these bytes are not a program" (PRD §72.3).
  /// </remarks>
  public static bool TryRead(ReadOnlySpan<byte> file, out PeImageFacts facts) {
    facts = default;
    if (!TryReadHeaders(file, out var machine, out var subsystem, out var is64Bit, out var resourceRva, out var sections))
      return false;

    var version = default(VersionStrings);
    if (resourceRva != 0 && TryResolve(file, sections, resourceRva, out var resourceOffset))
      TryReadVersionResource(file, sections, resourceOffset, ref version);

    facts = new(
      machine,
      subsystem,
      is64Bit,
      version.Description,
      version.Company,
      version.Product,
      version.ProductVersion,
      version.FileVersion,
      version.FixedFileVersion
    );

    return true;
  }

  #region headers

  /// <summary>One section's mapping between an address in the loaded image and an offset in the file.</summary>
  private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

  /// <summary>
  /// The DOS stub, the PE signature, the COFF header, the optional header and the section table.
  /// </summary>
  /// <remarks>
  /// The one branch that matters is the optional header's magic: PE32 carries a <c>BaseOfData</c>
  /// field and a 32-bit image base where PE32+ carries neither and a 64-bit one, which moves every
  /// field after them by four bytes. The subsystem happens to land at the same offset in both — the
  /// four bytes PE32 gains in the standard fields are the four PE32+ gains in the image base — and
  /// the data directories do not, so the count is computed rather than assumed.
  /// </remarks>
  private static bool TryReadHeaders(
    ReadOnlySpan<byte> file,
    out ushort machine,
    out ushort subsystem,
    out bool is64Bit,
    out uint resourceRva,
    out Section[] sections
  ) {
    machine = 0;
    subsystem = 0;
    is64Bit = false;
    resourceRva = 0;
    sections = [];

    // "MZ", then the offset of the PE header at 0x3C — the one field of the DOS stub that is still
    // read by anything.
    if (file.Length < 0x40 || file[0] != (byte)'M' || file[1] != (byte)'Z')
      return false;

    var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[0x3C..]);
    if (peOffset > int.MaxValue || peOffset + 24 > (uint)file.Length)
      return false;

    var pe = file[(int)peOffset..];
    if (pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0)
      return false;

    // COFF file header: Machine, NumberOfSections, TimeDateStamp, PointerToSymbolTable,
    // NumberOfSymbols, SizeOfOptionalHeader, Characteristics — twenty bytes.
    machine = BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]);
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(pe[6..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(pe[20..]);
    if (optionalSize < 72 || 24 + optionalSize > pe.Length)
      return false;

    var optional = pe.Slice(24, optionalSize);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
    is64Bit = magic == _PE32PLUS_MAGIC;
    if (magic != _PE32_MAGIC && magic != _PE32PLUS_MAGIC)
      return false;

    subsystem = BinaryPrimitives.ReadUInt16LittleEndian(optional[68..]);

    // NumberOfRvaAndSizes, then that many eight-byte directory entries. The count is in the file and
    // is not necessarily sixteen: a linker may write fewer, and reading a directory it did not write
    // reads whatever follows the header.
    var directoryCountOffset = is64Bit ? 108 : 92;
    if (directoryCountOffset + 4 > optional.Length)
      return false;

    var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(optional[directoryCountOffset..]);
    var directoriesOffset = directoryCountOffset + 4;
    if (directoryCount > _RESOURCE_DIRECTORY_INDEX) {
      var entry = directoriesOffset + (_RESOURCE_DIRECTORY_INDEX * 8);
      if (entry + 8 <= optional.Length)
        resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional[entry..]);
    }

    // The section table follows the optional header, whatever length that header declared.
    var tableOffset = (long)peOffset + 24 + optionalSize;
    if (tableOffset + ((long)sectionCount * 40) > file.Length)
      return false;

    var found = new Section[sectionCount];
    for (var i = 0; i < sectionCount; ++i) {
      var header = file.Slice((int)tableOffset + (i * 40), 40);
      found[i] = new(
        VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
        VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
        RawOffset: BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
        RawSize: BinaryPrimitives.ReadUInt32LittleEndian(header[16..])
      );
    }

    sections = found;
    return true;
  }

  /// <summary>
  /// Turns an address in the loaded image into an offset in the file on disk.
  /// </summary>
  /// <remarks>
  /// The whole reason this reader takes a file rather than a mapped image. A section's virtual size
  /// is routinely larger than the bytes on disk — the remainder is zero-filled by the loader — so the
  /// span that actually exists is bounded by the raw size, and an address inside the virtual tail has
  /// no file offset at all rather than one just past the section.
  /// </remarks>
  private static bool TryResolve(ReadOnlySpan<byte> file, Section[] sections, uint rva, out int offset) {
    offset = 0;
    foreach (var section in sections) {
      if (rva < section.VirtualAddress)
        continue;

      var delta = rva - section.VirtualAddress;
      if (delta >= section.VirtualSize && delta >= section.RawSize)
        continue;
      if (delta >= section.RawSize)
        return false;

      var candidate = (long)section.RawOffset + delta;
      if (candidate >= file.Length)
        return false;

      offset = (int)candidate;
      return true;
    }

    return false;
  }

  #endregion

  #region the resource tree

  private struct VersionStrings {
    public string? Description;
    public string? Company;
    public string? Product;
    public string? ProductVersion;
    public string? FileVersion;
    public string? FixedFileVersion;

    /// <summary>How good a match the table these came from was, so a better one may replace them.</summary>
    public int Rank;
  }

  /// <summary>
  /// Walks type → name → language and reads every version resource the image carries.
  /// </summary>
  /// <remarks>
  /// Every one of them rather than the first: an image may ship one version resource per language,
  /// and the first in the tree is whichever the linker wrote first rather than the one a reader here
  /// would want. <see cref="Rank"/> is what chooses between them.
  /// </remarks>
  private static void TryReadVersionResource(
    ReadOnlySpan<byte> file,
    Section[] sections,
    int rootOffset,
    ref VersionStrings version
  ) {
    // Level one: resource types. Only RT_VERSION is wanted, and it is an id rather than a name.
    foreach (var type in Entries(file, rootOffset, rootOffset)) {
      if (type.IsName || type.Id != _RT_VERSION || !type.IsDirectory)
        continue;

      // Level two: the names or ids the version resources are filed under — normally the single id 1.
      foreach (var name in Entries(file, rootOffset, rootOffset + (int)type.Offset)) {
        if (!name.IsDirectory)
          continue;

        // Level three: languages. Each leaf is a data entry pointing at one VS_VERSIONINFO block.
        foreach (var language in Entries(file, rootOffset, rootOffset + (int)name.Offset)) {
          if (language.IsDirectory)
            continue;

          var leaf = rootOffset + (int)language.Offset;
          // IMAGE_RESOURCE_DATA_ENTRY: OffsetToData, Size, CodePage, Reserved. The first field is an
          // address in the loaded image and not an offset in this tree, which is the one place the
          // resource section stops being self-relative.
          if (leaf < 0 || leaf + 16 > file.Length)
            continue;

          var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(file[leaf..]);
          var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(file[(leaf + 4)..]);
          if (dataSize is 0 or > (1 << 20) || !TryResolve(file, sections, dataRva, out var dataOffset))
            continue;

          if (dataOffset + dataSize > (uint)file.Length)
            continue;

          ReadVersionBlock(file.Slice(dataOffset, (int)dataSize), ref version);
        }
      }
    }
  }

  /// <summary>One entry of an <c>IMAGE_RESOURCE_DIRECTORY</c>.</summary>
  /// <param name="IsName">
  /// Set when the high bit of the name field is: the low bits are then an offset to a counted
  /// string rather than an integer id.
  /// </param>
  /// <param name="IsDirectory">
  /// Set when the high bit of the data field is: the low bits are then another directory rather than
  /// a leaf.
  /// </param>
  private readonly record struct DirectoryEntry(bool IsName, uint Id, bool IsDirectory, uint Offset);

  /// <summary>
  /// The entries of one resource directory, named and numbered together.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The header is Characteristics, TimeDateStamp, MajorVersion, MinorVersion, NumberOfNamedEntries
  /// and NumberOfIdEntries — sixteen bytes — followed by the named entries and then the numbered
  /// ones. Both are read: which of the two a version resource is filed under is the resource
  /// compiler's business and not a thing to depend on.
  /// </para>
  /// <para>
  /// Which of the two an entry <em>is</em> comes from the counts and the ordering, because that is
  /// the part the PE specification actually states. The high bit of the name field is the usual way
  /// to ask, and every real image agrees with it, but the specification documents that convention
  /// only for the <em>second</em> word of the pair and says nothing about it for the first — so it
  /// is taken here as corroboration rather than as the rule.
  /// </para>
  /// </remarks>
  private static List<DirectoryEntry> Entries(ReadOnlySpan<byte> file, int rootOffset, int directoryOffset) {
    var result = new List<DirectoryEntry>();
    if (directoryOffset < rootOffset || directoryOffset + 16 > file.Length)
      return result;

    var named = BinaryPrimitives.ReadUInt16LittleEndian(file[(directoryOffset + 12)..]);
    var numbered = BinaryPrimitives.ReadUInt16LittleEndian(file[(directoryOffset + 14)..]);
    var count = named + numbered;
    var first = directoryOffset + 16;
    if (first + ((long)count * 8) > file.Length)
      return result;

    for (var i = 0; i < count; ++i) {
      var entry = file.Slice(first + (i * 8), 8);
      var name = BinaryPrimitives.ReadUInt32LittleEndian(entry);
      var data = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
      result.Add(new(
        IsName: i < named || (name & 0x8000_0000) != 0,
        Id: name & 0x7FFF_FFFF,
        IsDirectory: (data & 0x8000_0000) != 0,
        Offset: data & 0x7FFF_FFFF
      ));
    }

    return result;
  }

  #endregion

  #region VS_VERSIONINFO

  /// <summary>
  /// The header every node of a version resource begins with, and where its value and its children
  /// start.
  /// </summary>
  /// <remarks>
  /// One shape for the whole format: <c>wLength</c>, <c>wValueLength</c>, <c>wType</c>, a
  /// NUL-terminated UTF-16 key, then the value — each of the three padded to a 32-bit boundary
  /// measured from the start of the block. <c>wValueLength</c> counts <em>characters</em> when
  /// <c>wType</c> says the value is text and bytes when it says binary, which is the one field of
  /// this format that is routinely got wrong.
  /// </remarks>
  private readonly record struct Node(
    int Start,
    int Length,
    int ValueLength,
    bool IsText,
    string Key,
    int ValueOffset,
    int ChildrenOffset
  ) {

    /// <summary>Just past this node's own bytes, which is where its parent's next child begins.</summary>
    public int End => this.Start + this.Length;

  }

  private static bool TryReadNode(ReadOnlySpan<byte> block, int offset, out Node node) {
    node = default;
    if (offset < 0 || offset + 6 > block.Length)
      return false;

    var length = BinaryPrimitives.ReadUInt16LittleEndian(block[offset..]);
    var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 2)..]);
    var type = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 4)..]);
    if (length < 6 || offset + length > block.Length)
      return false;

    var keyStart = offset + 6;
    var keyEnd = keyStart;
    while (keyEnd + 2 <= block.Length && BinaryPrimitives.ReadUInt16LittleEndian(block[keyEnd..]) != 0)
      keyEnd += 2;

    if (keyEnd + 2 > block.Length || keyEnd > offset + length)
      return false;

    var key = System.Text.Encoding.Unicode.GetString(block[keyStart..keyEnd]);
    var valueOffset = Align(keyEnd + 2);
    var isText = type == 1;
    var valueBytes = isText ? valueLength * 2 : valueLength;
    node = new(offset, length, valueLength, isText, key, valueOffset, Align(valueOffset + valueBytes));
    return true;
  }

  /// <summary>Every node and every value starts on a four-byte boundary within the block.</summary>
  private static int Align(int offset) => (offset + 3) & ~3;

  /// <summary>
  /// One <c>VS_VERSIONINFO</c> block: the fixed part, then the string tables.
  /// </summary>
  private static void ReadVersionBlock(ReadOnlySpan<byte> block, ref VersionStrings version) {
    if (!TryReadNode(block, 0, out var root) || root.Key != "VS_VERSION_INFO")
      return;

    var fixedVersion = ReadFixedFileInfo(block, in root);

    // The children are StringFileInfo and VarFileInfo, in whichever order the resource compiler put
    // them, and either may be absent.
    foreach (var child in Children(block, in root))
      if (child.Key == "StringFileInfo")
        ReadStringFileInfo(block, in child, fixedVersion, ref version);
  }

  /// <summary>
  /// The children of one node, bounded by the length that node declares.
  /// </summary>
  /// <remarks>
  /// Bounded by the parent and not by the block: a child whose own length is nought or runs past its
  /// parent would otherwise loop for ever or wander into whatever follows, and both of those are
  /// shapes a file somebody else wrote can have.
  /// </remarks>
  private static List<Node> Children(ReadOnlySpan<byte> block, in Node parent) {
    var result = new List<Node>();
    var end = Math.Min(block.Length, parent.End);
    for (var offset = parent.ChildrenOffset; offset < end;) {
      if (!TryReadNode(block, offset, out var child) || child.End > end)
        break;

      result.Add(child);
      var next = Align(child.End);
      if (next <= offset)
        break;

      offset = next;
    }

    return result;
  }

  /// <summary>
  /// The binary file version, as the four numbers <c>VS_FIXEDFILEINFO</c> carries.
  /// </summary>
  /// <remarks>
  /// Read through its signature rather than through its position: a block whose value length says
  /// there is a fixed part but whose bytes are something else is a malformed resource, and
  /// <c>0xFEEF04BD</c> is what tells the two apart. The version is two DWORDs, most significant
  /// first, each holding two sixteen-bit components.
  /// </remarks>
  private static string? ReadFixedFileInfo(ReadOnlySpan<byte> block, in Node root) {
    if (root.ValueLength < 52 || root.ValueOffset + 52 > block.Length)
      return null;

    var value = block[root.ValueOffset..];
    if (BinaryPrimitives.ReadUInt32LittleEndian(value) != _FIXED_FILE_INFO_SIGNATURE)
      return null;

    var most = BinaryPrimitives.ReadUInt32LittleEndian(value[8..]);
    var least = BinaryPrimitives.ReadUInt32LittleEndian(value[12..]);
    return $"{most >> 16}.{most & 0xFFFF}.{least >> 16}.{least & 0xFFFF}";
  }

  /// <summary>
  /// The string tables, one per translation, and the strings in the best of them.
  /// </summary>
  /// <remarks>
  /// A table's key is eight hexadecimal digits: a language identifier and a code page. US English is
  /// preferred, then anything else with a real language, then the language-neutral table — because a
  /// program that ships several translations of its own description ships the one this reader can
  /// present and several it cannot, and picking whichever came first would be picking at random
  /// (PRD §5.3).
  /// </remarks>
  private static void ReadStringFileInfo(
    ReadOnlySpan<byte> block,
    in Node stringFileInfo,
    string? fixedVersion,
    ref VersionStrings version
  ) {
    foreach (var table in Children(block, in stringFileInfo)) {
      var rank = Rank(table.Key);
      if (rank > version.Rank)
        ReadStringTable(block, in table, rank, fixedVersion, ref version);
    }
  }

  /// <summary>
  /// How much a translation is worth to a reader here: US English, then any other language, then the
  /// neutral table nobody chose.
  /// </summary>
  private static int Rank(string key) {
    if (key.Length < 4 || !ushort.TryParse(key.AsSpan(0, 4), System.Globalization.NumberStyles.HexNumber, null, out var language))
      return 1;

    return language switch {
      0x0409 => 3,
      0x0000 => 1,
      _ => 2,
    };
  }

  private static void ReadStringTable(
    ReadOnlySpan<byte> block,
    in Node table,
    int rank,
    string? fixedVersion,
    ref VersionStrings version
  ) {
    var found = new VersionStrings { Rank = rank, FixedFileVersion = fixedVersion };
    foreach (var entry in Children(block, in table)) {
      var text = ReadText(block, in entry);
      if (text is not { Length: > 0 })
        continue;

      switch (entry.Key) {
        case "FileDescription": found.Description = text; break;
        case "CompanyName": found.Company = text; break;
        case "ProductName": found.Product = text; break;
        case "ProductVersion": found.ProductVersion = text; break;
        case "FileVersion": found.FileVersion = text; break;
      }
    }

    // Only a table that actually said something replaces one that did: an image whose US English
    // table is empty and whose neutral one is filled should read as the neutral one rather than as
    // nothing at all.
    if (found.Description is null && found.Company is null && found.Product is null
        && found.ProductVersion is null && found.FileVersion is null)
      return;

    version = found;
  }

  /// <summary>
  /// One string value, with the trailing NUL the format writes and the character count that may or
  /// may not include it.
  /// </summary>
  /// <remarks>
  /// <c>wValueLength</c> is documented as a count of characters for a text value, and resource
  /// compilers agree about that far more often than they agree about whether the terminator is one
  /// of them. Both are accepted: the value is read to the declared length and then trimmed at the
  /// first NUL, which is right either way.
  /// </remarks>
  private static string? ReadText(ReadOnlySpan<byte> block, in Node node) {
    if (!node.IsText || node.ValueLength <= 0)
      return null;

    var bytes = node.ValueLength * 2;
    if (node.ValueOffset + bytes > block.Length)
      bytes = block.Length - node.ValueOffset;
    if (bytes <= 0)
      return null;

    var text = System.Text.Encoding.Unicode.GetString(block.Slice(node.ValueOffset, bytes & ~1));
    var terminator = text.IndexOf('\0');
    if (terminator >= 0)
      text = text[..terminator];

    return text.Length == 0 ? null : text;
  }

  #endregion

}
