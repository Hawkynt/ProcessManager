using System.Buffers.Binary;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Turns an address into the name of the function it is inside, using the symbol table an image
/// carries (PRD §29, §30).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a symbol server and deliberately not a loader. It reads the section header table,
/// finds whichever symbol table the image still has, and walks it looking for the one symbol whose
/// extent covers the address. Nothing is cached here and nothing is mapped: the caller asks about a
/// handful of addresses at a time, and a stack of forty frames touches at most forty ranges of a few
/// files.
/// </para>
/// <para>
/// Most binaries on a distribution are stripped of <c>.symtab</c> and keep only <c>.dynsym</c>, which
/// names exported functions and nothing else. So a resolved name is a bonus and an unresolved one is
/// the normal case — which is why §30 keeps a module-and-offset column beside the symbol column
/// rather than behind it.
/// </para>
/// <para>
/// The file access is the caller's, through <see cref="ElfImage.ElfRead"/>, which is what keeps this
/// in Core with no platform attribute and under test on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ElfSymbols {

  /// <summary>The function an address landed in, and how far into it.</summary>
  public readonly record struct Match(string Name, ulong Displacement);

  private const int _ShtSymTab = 2;
  private const int _ShtDynSym = 11;
  private const int _SttFunc = 2;
  private const int _SttGnuIFunc = 10;

  /// <summary>Section indices at or above this are the reserved ones, not sections.</summary>
  private const int _ShnLoReserve = 0xFF00;

  /// <summary>An image with more sections than this is not one worth walking.</summary>
  private const int _MaxSections = 4096;

  /// <summary>
  /// How much of a symbol table to walk. A symbol is 24 bytes, so this is a third of a million of
  /// them — an order of magnitude more than the largest thing a process maps, and a bound on what a
  /// deliberately malformed header can make this read.
  /// </summary>
  private const int _MaxSymbolTableBytes = 8 * 1024 * 1024;

  /// <summary>The longest symbol name worth reading. C++ manglings get long; they do not get this long.</summary>
  private const int _MaxNameLength = 1024;

  private const int _ChunkBytes = 64 * 1024;

  /// <summary>
  /// Resolves an address inside the image.
  /// </summary>
  /// <param name="read">Reads the image at an absolute offset.</param>
  /// <param name="fileAddress">
  /// The address in the image's own terms — that is, with the load bias already taken back off for a
  /// position-independent object. An address still carrying its bias resolves to nothing, which is
  /// the safe way for that mistake to fail.
  /// </param>
  /// <param name="match">The symbol, meaningful only when this returns true.</param>
  public static bool TryResolve(ElfImage.ElfRead read, ulong fileAddress, out Match match) {
    ArgumentNullException.ThrowIfNull(read);
    match = default;

    Span<byte> header = stackalloc byte[64];
    if (read(0, header) < header.Length)
      return false;

    if (header[0] != 0x7F || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F')
      return false;

    var is64 = header[4] == 2;
    var little = header[5] != 2;
    var sectionOffset = is64
      ? (long)ReadUInt64(header[0x28..], little)
      : ReadUInt32(header[0x20..], little);
    var sectionSize = ReadUInt16(header[(is64 ? 0x3A : 0x2E)..], little);
    var sectionCount = ReadUInt16(header[(is64 ? 0x3C : 0x30)..], little);
    var minimumSize = is64 ? 64 : 40;
    if (sectionOffset <= 0 || sectionSize < minimumSize || sectionCount is 0 or > _MaxSections)
      return false;

    var table = new byte[sectionCount * sectionSize];
    if (read(sectionOffset, table) < table.Length)
      return false;

    // .symtab first and .dynsym second, in two passes rather than one: a stripped image has only the
    // second, and an unstripped one has both — with the static table naming everything and the
    // dynamic one naming only what is exported. Taking whichever came first in the file would answer
    // a question about a local function with "no symbol" on an image that names it.
    return TryResolveIn(read, table, sectionSize, sectionCount, is64, little, _ShtSymTab, fileAddress, out match)
      || TryResolveIn(read, table, sectionSize, sectionCount, is64, little, _ShtDynSym, fileAddress, out match);
  }

  private static bool TryResolveIn(
    ElfImage.ElfRead read,
    ReadOnlySpan<byte> table,
    int sectionSize,
    int sectionCount,
    bool is64,
    bool little,
    int wantedType,
    ulong fileAddress,
    out Match match
  ) {
    match = default;
    for (var i = 0; i < sectionCount; ++i) {
      var section = table.Slice(i * sectionSize, sectionSize);
      if (ReadUInt32(section[4..], little) != wantedType)
        continue;

      var offset = is64 ? (long)ReadUInt64(section[24..], little) : ReadUInt32(section[16..], little);
      var size = is64 ? (long)ReadUInt64(section[32..], little) : ReadUInt32(section[20..], little);
      var link = (int)ReadUInt32(section[(is64 ? 40 : 24)..], little);
      var entrySize = is64 ? (long)ReadUInt64(section[56..], little) : ReadUInt32(section[36..], little);
      var expected = is64 ? 24 : 16;
      if (offset <= 0 || size <= 0 || entrySize != expected || link <= 0 || link >= sectionCount)
        continue;

      if (size > _MaxSymbolTableBytes)
        size = _MaxSymbolTableBytes - _MaxSymbolTableBytes % entrySize;

      var strings = table.Slice(link * sectionSize, sectionSize);
      var stringOffset = is64 ? (long)ReadUInt64(strings[24..], little) : ReadUInt32(strings[16..], little);
      var stringSize = is64 ? (long)ReadUInt64(strings[32..], little) : ReadUInt32(strings[20..], little);
      if (stringOffset <= 0 || stringSize <= 0)
        continue;

      if (TryScan(read, offset, size, entrySize, is64, little, fileAddress, out var name, out var displacement)
        && ReadName(read, stringOffset, stringSize, name) is { Length: > 0 } text) {
        match = new(text, displacement);
        return true;
      }
    }

    return false;
  }

  /// <summary>
  /// Walks one symbol table for the function containing <paramref name="fileAddress"/>.
  /// </summary>
  /// <remarks>
  /// Containment wins, and only a symbol that declares no size is ever used as a nearest-preceding
  /// fallback. That distinction is the whole guard: a symbol with a size says where it ends, so an
  /// address past the end of one is not in it — and letting sized symbols answer for the gaps after
  /// them is how a stack viewer names a frame after whatever happens to be linked in front of it.
  /// Sizeless symbols are the assembly stubs and entry points that genuinely never declared an
  /// extent, and for those the preceding name is the best there is.
  /// </remarks>
  private static bool TryScan(
    ElfImage.ElfRead read,
    long offset,
    long size,
    long entrySize,
    bool is64,
    bool little,
    ulong fileAddress,
    out uint name,
    out ulong displacement
  ) {
    name = 0;
    displacement = 0;

    var found = false;
    var bestValue = 0ul;
    var buffer = new byte[Math.Min(size, _ChunkBytes) / entrySize * entrySize];
    if (buffer.Length == 0)
      return false;

    for (long scanned = 0; scanned < size;) {
      var wanted = (int)Math.Min(buffer.Length, size - scanned);
      var got = read(offset + scanned, buffer.AsSpan(0, wanted));
      if (got < entrySize)
        break;

      got -= got % (int)entrySize;
      scanned += got;
      for (var at = 0; at + entrySize <= got; at += (int)entrySize) {
        var entry = buffer.AsSpan(at, (int)entrySize);
        var info = is64 ? entry[4] : entry[12];
        var shndx = ReadUInt16(entry[(is64 ? 6 : 14)..], little);
        var value = is64 ? ReadUInt64(entry[8..], little) : ReadUInt32(entry[4..], little);
        var symbolSize = is64 ? ReadUInt64(entry[16..], little) : ReadUInt32(entry[8..], little);
        var kind = info & 0xF;
        // SHN_UNDEF is a symbol this image refers to and does not define, and the reserved indices
        // above SHN_LORESERVE — SHN_ABS most of all — hold values that are not addresses in this
        // image at all. Both would otherwise name whatever code happens to sit at the same number:
        // a BOLT-instrumented binary keeps its entry point as an absolute weak symbol, and matching
        // it would put a linker's bookkeeping label on a stack frame.
        if (shndx is 0 or >= _ShnLoReserve || kind is not (_SttFunc or _SttGnuIFunc) || value > fileAddress)
          continue;

        if (symbolSize > 0 && fileAddress < value + symbolSize) {
          name = ReadUInt32(entry, little);
          displacement = fileAddress - value;
          return true;
        }

        if (symbolSize == 0 && (!found || value > bestValue)) {
          found = true;
          bestValue = value;
          name = ReadUInt32(entry, little);
          displacement = fileAddress - value;
        }
      }
    }

    return found;
  }

  private static string? ReadName(ElfImage.ElfRead read, long stringOffset, long stringSize, uint index) {
    if (index == 0 || index >= (ulong)stringSize)
      return null;

    var wanted = (int)Math.Min(_MaxNameLength, stringSize - index);
    var buffer = new byte[wanted];
    var got = read(stringOffset + index, buffer);
    if (got <= 0)
      return null;

    var end = buffer.AsSpan(0, got).IndexOf((byte)0);
    if (end < 0)
      end = got;

    return end == 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
  }

  private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool little) => little
    ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt16BigEndian(bytes);

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool little) => little
    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt32BigEndian(bytes);

  private static ulong ReadUInt64(ReadOnlySpan<byte> bytes, bool little) => little
    ? BinaryPrimitives.ReadUInt64LittleEndian(bytes)
    : BinaryPrimitives.ReadUInt64BigEndian(bytes);

}
