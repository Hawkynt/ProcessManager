using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The firmware's memory records (PRD §47), against hand-built tables.
/// </summary>
/// <remarks>
/// The table this parser is for is root-only on Linux, so on the machine this was written on nobody
/// can look at the real one — which is exactly the situation the arm64 bit table is in, and exactly
/// why the records here are built byte by byte to the layout SMBIOS specifies rather than checked
/// against whatever this laptop happens to have in it (PRD §9.4).
/// </remarks>
[TestFixture]
public sealed class SmbiosTests {

  #region building records

  /// <summary>
  /// One type-17 memory device.
  /// </summary>
  /// <param name="sizeMegabytes">
  /// The 16-bit size field, in megabytes. Nought is an empty slot; <c>0x7FFF</c> sends the reader to
  /// the extended field.
  /// </param>
  private static byte[] Device(
    ushort sizeMegabytes,
    byte formFactor = 0x0D,
    ushort speed = 0,
    ushort configuredSpeed = 0,
    uint extendedSizeMegabytes = 0,
    int length = 0x54
  ) {
    var record = new byte[length];
    record[0] = 17;
    record[1] = (byte)length;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0x1100);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x0C), sizeMegabytes);
    record[0x0E] = formFactor;
    if (length > 0x16)
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x15), speed);

    if (length > 0x1F)
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x1C), extendedSizeMegabytes);

    if (length > 0x21)
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x20), configuredSpeed);

    return record;
  }

  private static byte[] Structure(byte type, int length = 8) {
    var record = new byte[length];
    record[0] = type;
    record[1] = (byte)length;
    return record;
  }

  /// <summary>A record followed by its string set — one string, or the bare terminator for none.</summary>
  private static byte[] WithStrings(byte[] record, params string[] strings) {
    var bytes = new List<byte>(record);
    foreach (var text in strings) {
      bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(text));
      bytes.Add(0);
    }

    bytes.Add(0);
    if (strings.Length == 0)
      bytes.Add(0);

    return [.. bytes];
  }

  private static byte[] Table(params byte[][] records) {
    var bytes = new List<byte>();
    foreach (var record in records)
      bytes.AddRange(record);

    return [.. bytes];
  }

  #endregion

  [Test]
  public void TwoModulesInFourSlotsReadAsTwoOfFour() {
    var table = Table(
      WithStrings(Device(16 * 1024, speed: 5600, configuredSpeed: 4800), "ChannelA-DIMM0"),
      WithStrings(Device(0), "ChannelA-DIMM1"),
      WithStrings(Device(16 * 1024, speed: 5600, configuredSpeed: 4800), "ChannelB-DIMM0"),
      WithStrings(Device(0), "ChannelB-DIMM1"),
      WithStrings(Structure(127))
    );

    var memory = Smbios.ReadMemory(table);
    Assert.That(memory.SlotsUsed.Value, Is.EqualTo(2ul));
    Assert.That(memory.SlotsTotal.Value, Is.EqualTo(4ul), "an empty slot is a slot");
    Assert.That(memory.InstalledBytes.Value, Is.EqualTo(32ul * 1024 * 1024 * 1024));
    Assert.That(memory.FormFactor, Is.EqualTo("SODIMM"));
  }

  /// <summary>
  /// The configured speed, not the rated one: a DDR5-5600 module the board would only train to 4800
  /// is doing 4800, and the rated figure would describe a machine nobody has.
  /// </summary>
  [Test]
  public void TheSpeedIsWhatTheModuleIsRunningAtRatherThanWhatItIsSoldAs() {
    var table = Table(WithStrings(Device(8 * 1024, speed: 5600, configuredSpeed: 4800)));

    Assert.That(Smbios.ReadMemory(table).TransfersPerSecond.Value, Is.EqualTo(4_800_000_000ul));
  }

  /// <summary>Firmware old enough to have no configured-speed field still has a rated one.</summary>
  [Test]
  public void ARecordTooShortForTheConfiguredSpeedFallsBackToTheRatedOne() {
    var table = Table(WithStrings(Device(4 * 1024, speed: 1600, length: 0x1B)));

    Assert.That(Smbios.ReadMemory(table).TransfersPerSecond.Value, Is.EqualTo(1_600_000_000ul));
  }

  /// <summary>
  /// A module too large for sixteen bits of megabytes sets the escape and puts the real figure in the
  /// 32-bit field. Reading the escape as a size reports every 32 GB module as 32 767 MB.
  /// </summary>
  [Test]
  public void AModuleTooLargeForTheShortFieldIsReadFromTheExtendedOne() {
    var table = Table(WithStrings(Device(0x7FFF, extendedSizeMegabytes: 64 * 1024, configuredSpeed: 3200)));

    var memory = Smbios.ReadMemory(table);
    Assert.That(memory.InstalledBytes.Value, Is.EqualTo(64ul * 1024 * 1024 * 1024));
    Assert.That(memory.SlotsUsed.Value, Is.EqualTo(1ul));
  }

  /// <summary>Bit 15 set means the field counts kilobytes, which is how a small region is expressed.</summary>
  [Test]
  public void ASizeInKilobytesIsNotASizeInMegabytes() {
    var table = Table(WithStrings(Device(0x8000 | 512)));

    Assert.That(Smbios.ReadMemory(table).InstalledBytes.Value, Is.EqualTo(512ul * 1024));
  }

  /// <summary>
  /// A structure cannot be skipped by its declared length: the string set after it is not counted in
  /// that length, and stepping by the length alone lands in the middle of somebody's serial number.
  /// </summary>
  [Test]
  public void TheWalkStepsOverEachRecordsStringSet() {
    var table = Table(
      WithStrings(Structure(1, 0x1B), "Some Vendor", "A Very Long Product Name Indeed", "1.0"),
      WithStrings(Device(8 * 1024, configuredSpeed: 3200), "DIMM_A1", "Vendor"),
      WithStrings(Structure(127))
    );

    var memory = Smbios.ReadMemory(table);
    Assert.That(memory.SlotsTotal.Value, Is.EqualTo(1ul), "the memory device after the long string set");
    Assert.That(memory.TransfersPerSecond.Value, Is.EqualTo(3_200_000_000ul));
  }

  [Test]
  public void ARecordWithNoStringsIsStillTerminatedByTwoNuls() {
    var table = Table(WithStrings(Structure(4, 0x30)), WithStrings(Device(16 * 1024)), WithStrings(Structure(127)));

    Assert.That(Smbios.ReadMemory(table).SlotsTotal.Value, Is.EqualTo(1ul));
  }

  #region what is not there is not nought (PRD §5.3)

  [Test]
  public void ATableWithNoMemoryDevicesIsNotAMachineWithNoMemory() {
    var memory = Smbios.ReadMemory(Table(WithStrings(Structure(1, 0x1B), "Vendor"), WithStrings(Structure(127))));

    Assert.That(memory.SlotsTotal.HasValue, Is.False);
    Assert.That(memory.SlotsUsed.HasValue, Is.False);
    Assert.That(memory.InstalledBytes.HasValue, Is.False);
    Assert.That(memory.FormFactor, Is.Null);
  }

  [Test]
  public void AnEmptyTableSaysSoRatherThanReportingNoughtSlots() {
    foreach (var table in new[] { Array.Empty<byte>(), new byte[2] })
      Assert.That(Smbios.ReadMemory(table).SlotsTotal.HasValue, Is.False);
  }

  /// <summary>
  /// Four slots with nothing in any of them: the slots are known and the total is not, because a
  /// machine reporting nought bytes of installed memory is a machine that has said something absurd.
  /// </summary>
  [Test]
  public void EmptySlotsAreCountedAndTheirTotalIsRefused() {
    var memory = Smbios.ReadMemory(Table(WithStrings(Device(0)), WithStrings(Device(0)), WithStrings(Structure(127))));

    Assert.That(memory.SlotsTotal.Value, Is.EqualTo(2ul));
    Assert.That(memory.SlotsUsed.Value, Is.EqualTo(0ul));
    Assert.That(memory.InstalledBytes.HasValue, Is.False);
    Assert.That(memory.InstalledBytes.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  /// <summary>
  /// <c>Unknown</c> is a value of the form-factor enumeration, and printing the word as though it
  /// were a shape would tell the reader the firmware answered when it declined to.
  /// </summary>
  [Test]
  public void AFormFactorTheFirmwareDeclinedToGiveIsNotAFormFactor() {
    foreach (var declined in new byte[] { 0x01, 0x02, 0x7F })
      Assert.That(Smbios.ReadMemory(Table(WithStrings(Device(8 * 1024, declined)))).FormFactor, Is.Null);
  }

  [Test]
  public void ASpeedTheFirmwareLeftAtNoughtIsNotASpeedOfNought() {
    var memory = Smbios.ReadMemory(Table(WithStrings(Device(8 * 1024))));

    Assert.That(memory.InstalledBytes.HasValue, Is.True, "the module is there");
    Assert.That(memory.TransfersPerSecond.HasValue, Is.False, "its speed is not");
  }

  /// <summary>
  /// The escape without the extended field behind it is firmware that has not said, and reading the
  /// escape itself would report 65 535 MT/s — a plausible-looking number no memory has ever run at.
  /// </summary>
  [Test]
  public void TheSpeedEscapeWithoutItsExtendedFieldIsNotASpeed() {
    var table = Table(WithStrings(Device(8 * 1024, configuredSpeed: 0xFFFF, length: 0x22)));

    Assert.That(Smbios.ReadMemory(table).TransfersPerSecond.HasValue, Is.False);
  }

  #endregion

  #region refusing a table that does not add up

  [Test]
  public void ARecordShorterThanItsOwnHeaderStopsTheWalk() {
    var table = Table(WithStrings(Device(8 * 1024)), [17, 2, 0, 0]);

    // The good record before it still counts; the walk simply stops rather than advancing by two
    // for ever.
    Assert.That(Smbios.ReadMemory(table).SlotsTotal.Value, Is.EqualTo(1ul));
  }

  [Test]
  public void ARecordClaimingToRunPastTheEndIsRefused() {
    var record = Device(8 * 1024);
    record[1] = 0xFF;

    Assert.That(Smbios.ReadMemory(Table(WithStrings(record))).SlotsTotal.HasValue, Is.False);
  }

  #endregion

}
