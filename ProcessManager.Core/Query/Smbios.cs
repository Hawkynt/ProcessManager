using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What the firmware says about the memory modules, from the SMBIOS structure table (PRD §47).
/// </summary>
/// <remarks>
/// <para>
/// The four Task-Manager-style hardware facts — installed total, transfer rate, form factor and how
/// many slots are filled — exist nowhere the kernel publishes. They are firmware facts, and the
/// firmware publishes them once, at boot, as a table of variable-length records: type 17 is one
/// memory device, one per socket on the board, whether or not a module is in it.
/// </para>
/// <para>
/// A span of the table bytes and nothing else, so the same function answers on Linux — where the
/// table is <c>/sys/firmware/dmi/tables/DMI</c> and root-only — and on Windows, where
/// <c>GetSystemFirmwareTable</c> hands back the same bytes with an anchor in front of them. That is
/// what makes it testable on a machine whose own table nobody may read (PRD §9.4).
/// </para>
/// <para>
/// Every field is bounds-checked against the record's own declared length rather than against the
/// version in the anchor. Firmware in the field is routinely a revision behind its own version
/// number, and a record that stops before <c>Configured Memory Speed</c> is ordinary; reading past
/// its end would report the next record's handle as a clock rate.
/// </para>
/// </remarks>
public static class Smbios {

  /// <summary>Type 17 — one memory device, populated or not.</summary>
  private const byte _MemoryDevice = 17;

  /// <summary>Type 127 — end of table. Everything after it is padding.</summary>
  private const byte _EndOfTable = 127;

  /// <summary>The header every structure starts with: type, length, handle.</summary>
  private const int _HeaderLength = 4;

  /// <summary>
  /// What one walk of the table found.
  /// </summary>
  /// <remarks>
  /// Counters rather than numbers, because "no table" and "a table with nothing in it" are different
  /// answers and a machine with no readable firmware must not report nought slots (PRD §5.3).
  /// </remarks>
  public readonly record struct MemoryHardware(
    Counter InstalledBytes,
    Counter TransfersPerSecond,
    string? FormFactor,
    Counter SlotsUsed,
    Counter SlotsTotal
  ) {

    /// <summary>What every field says when the table could not be read at all.</summary>
    public static MemoryHardware Unreadable(UnknownReason reason) => new(
      Counter.Unknown(reason),
      Counter.Unknown(reason),
      null,
      Counter.Unknown(reason),
      Counter.Unknown(reason)
    );

  }

  /// <summary>
  /// Walks the structure table and totals what its memory devices say.
  /// </summary>
  /// <param name="table">
  /// The structures themselves, starting at the first one's type byte. Not the 2.x <c>_SM_</c> or
  /// 3.x <c>_SM3_</c> anchor, which is a separate file on Linux and a separate header on Windows:
  /// every structure states its own length, so the table is walkable without it.
  /// </param>
  public static MemoryHardware ReadMemory(ReadOnlySpan<byte> table) {
    if (table.Length < _HeaderLength)
      return MemoryHardware.Unreadable(UnknownReason.NotSupportedOnPlatform);

    ulong installed = 0;
    var slots = 0;
    var used = 0;
    var speed = 0ul;
    string? formFactor = null;
    var sawDevice = false;

    var offset = 0;
    while (offset + _HeaderLength <= table.Length) {
      var type = table[offset];
      var length = table[offset + 1];

      // A record shorter than its own header would never advance, and one that overruns the buffer is
      // not a record. Either means the bytes are not a structure table, and guessing where the next
      // one starts is exactly what a parser reading firmware must not do.
      if (length < _HeaderLength || offset + length > table.Length)
        break;

      if (type == _MemoryDevice) {
        sawDevice = true;
        ++slots;
        var record = table.Slice(offset, length);
        var size = SizeOf(record);
        if (size > 0) {
          ++used;
          installed += size;
          // The first populated module decides the two facts that are properties of a module rather
          // than of the machine. Mixed modules exist, but a page with room for one line has to pick
          // one, and every other tool picks the same one.
          if (speed == 0)
            speed = SpeedOf(record);

          formFactor ??= FormFactorOf(record);
        }
      }

      // The formatted area is followed by its string set, terminated by two nul bytes — and an empty
      // string set is a single pair, not nothing at all.
      offset = EndOfStrings(table, offset + length);
      if (type == _EndOfTable)
        break;
    }

    if (!sawDevice)
      return MemoryHardware.Unreadable(UnknownReason.NotSupportedOnPlatform);

    return new(
      installed > 0 ? Counter.Of(installed) : Counter.Unknown(UnknownReason.CounterInvalid),
      speed > 0 ? Counter.Of(speed) : Counter.Unknown(UnknownReason.NotSupportedOnPlatform),
      formFactor,
      Counter.Of((ulong)used),
      Counter.Of((ulong)slots)
    );
  }

  /// <summary>
  /// How large the module is, in bytes, or nought where the slot is empty.
  /// </summary>
  /// <remarks>
  /// Three encodings in one field, and all three are met on real machines. The 16-bit size at 0x0C
  /// counts megabytes, unless bit 15 is set, in which case it counts kilobytes — that is how a 32 MB
  /// NVDIMM region is expressed. <c>0x7FFF</c> means the module is too large for sixteen bits and the
  /// real figure is the 32-bit extended size at 0x1C, which is what every 32 GB module reports.
  /// </remarks>
  private static ulong SizeOf(ReadOnlySpan<byte> record) {
    if (record.Length < 0x0E)
      return 0;

    var size = BinaryPrimitives.ReadUInt16LittleEndian(record[0x0C..]);
    if (size == 0)
      return 0;

    if (size != 0x7FFF)
      return (size & 0x8000) != 0
        ? (ulong)(size & 0x7FFF) * 1024
        : (ulong)size * 1024 * 1024;

    if (record.Length < 0x20)
      return 0;

    // The extended size counts megabytes and its top bit is reserved.
    var extended = BinaryPrimitives.ReadUInt32LittleEndian(record[0x1C..]) & 0x7FFF_FFFF;
    return (ulong)extended * 1024 * 1024;
  }

  /// <summary>
  /// The module's transfer rate, in transfers per second.
  /// </summary>
  /// <remarks>
  /// The configured speed at 0x20 rather than the rated speed at 0x15 wherever the record is long
  /// enough to have it: a DDR5-5600 module running at 4800 because the board would not train it
  /// higher is doing 4800, and the rated figure would describe a machine nobody has. Both are stated
  /// in MT/s and both go to <c>0xFFFF</c> when the real figure needs the 32-bit extended fields of
  /// SMBIOS 3.3.
  /// </remarks>
  private static ulong SpeedOf(ReadOnlySpan<byte> record) {
    var configured = Rate(record, 0x20, 0x58);
    return configured > 0 ? configured : Rate(record, 0x15, 0x54);
  }

  private static ulong Rate(ReadOnlySpan<byte> record, int shortOffset, int extendedOffset) {
    if (record.Length < shortOffset + 2)
      return 0;

    var transfers = (ulong)BinaryPrimitives.ReadUInt16LittleEndian(record[shortOffset..]);

    // Nought is the firmware saying it does not know, and is not a clock rate of nothing.
    if (transfers == 0)
      return 0;

    // 0xFFFF is the escape: the real figure did not fit in sixteen bits and lives in the 32-bit
    // field SMBIOS 3.3 added. Firmware that sets the escape without carrying the extended field is
    // firmware that has not said, rather than one that has said 65535 MT/s.
    if (transfers == 0xFFFF)
      transfers = record.Length >= extendedOffset + 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(record[extendedOffset..])
        : 0;

    return transfers * 1_000_000;
  }

  /// <summary>
  /// The shape of the module, from the enumeration at 0x0E.
  /// </summary>
  /// <remarks>
  /// Null for the two values that mean the firmware declined to say — <c>Other</c> and
  /// <c>Unknown</c> — so that the row reports not knowing rather than printing the word "Unknown" as
  /// though it were a form factor (PRD §5.3).
  /// </remarks>
  private static string? FormFactorOf(ReadOnlySpan<byte> record) {
    if (record.Length < 0x0F)
      return null;

    return record[0x0E] switch {
      0x03 => "SIMM",
      0x04 => "SIP",
      0x05 => "Chip",
      0x06 => "DIP",
      0x07 => "ZIP",
      0x08 => "Proprietary card",
      0x09 => "DIMM",
      0x0A => "TSOP",
      0x0B => "Row of chips",
      0x0C => "RIMM",
      0x0D => "SODIMM",
      0x0E => "SRIMM",
      0x0F => "FB-DIMM",
      0x10 => "Die",
      0x11 => "CAMM",
      _ => null,
    };
  }

  /// <summary>
  /// Where the record after this one begins: past the string set's terminating double nul.
  /// </summary>
  /// <remarks>
  /// The string set is the reason a structure cannot be skipped by its declared length. A record with
  /// no strings still carries the pair, and firmware that ends the table without one leaves the walk
  /// at the end of the buffer rather than past it.
  /// </remarks>
  private static int EndOfStrings(ReadOnlySpan<byte> table, int start) {
    for (var i = start; i + 1 < table.Length; ++i)
      if (table[i] == 0 && table[i + 1] == 0)
        return i + 2;

    return table.Length;
  }

}
