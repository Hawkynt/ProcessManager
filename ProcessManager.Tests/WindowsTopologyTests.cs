using System.Buffers.Binary;
using Hawkynt.ProcessManager.Platform.Windows;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The <c>GetLogicalProcessorInformationEx</c> walk (PRD §46), against hand-built buffers.
/// </summary>
/// <remarks>
/// Runs on every OS. The buffer is variable-length and self-describing, and the two ways to get it
/// wrong — stepping by a fixed size, and counting records instead of affinity bits — both produce
/// numbers that look entirely plausible on the machine you developed on (PRD §9.4).
/// </remarks>
[TestFixture]
public sealed class WindowsTopologyTests {

  #region building records

  /// <summary>A core, covering the logical processors named by the bits of one affinity mask.</summary>
  private static byte[] Core(ulong affinityMask) {
    // relationship, size, flags, efficiency class, 20 reserved, group count, one GROUP_AFFINITY.
    var record = new byte[8 + 1 + 1 + 20 + 2 + 16];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationProcessorCore);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8 + 22), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8 + 24), affinityMask);
    return record;
  }

  private static byte[] Simple(uint relationship) {
    var record = new byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(record, relationship);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    return record;
  }

  /// <summary>A cache: level, associativity, line size, size, type.</summary>
  private static byte[] Cache(byte level, uint type, uint sizeInBytes) {
    var record = new byte[48];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationCache);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    record[8] = level;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), sizeInBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), type);
    return record;
  }

  private static byte[] Concat(params byte[][] records) {
    var total = 0;
    foreach (var record in records)
      total += record.Length;

    var buffer = new byte[total];
    var offset = 0;
    foreach (var record in records) {
      record.CopyTo(buffer, offset);
      offset += record.Length;
    }

    return buffer;
  }

  #endregion

  /// <summary>
  /// Four cores with SMT: eight logical processors. Counting core records rather than affinity bits
  /// reports four, which is exactly half and looks like a reasonable answer.
  /// </summary>
  [Test]
  public void LogicalProcessorsAreCountedFromAffinityBitsNotFromRecords() {
    var buffer = Concat(Core(0b0000_0011), Core(0b0000_1100), Core(0b0011_0000), Core(0b1100_0000));
    var topology = LogicalProcessorInformation.Parse(buffer);

    Assert.That(topology.PhysicalCores.Value, Is.EqualTo(4ul));
    Assert.That(topology.LogicalProcessors.Value, Is.EqualTo(8ul));
  }

  [Test]
  public void AMachineWithoutSmtReportsAsManyThreadsAsCores() {
    var topology = LogicalProcessorInformation.Parse(Concat(Core(0b0001), Core(0b0010)));

    Assert.That(topology.PhysicalCores.Value, Is.EqualTo(2ul));
    Assert.That(topology.LogicalProcessors.Value, Is.EqualTo(2ul));
  }

  [Test]
  public void PackagesAndNumaNodesAreCounted() {
    var buffer = Concat(
      Core(0b0011),
      Simple(LogicalProcessorInformation.RelationProcessorPackage),
      Simple(LogicalProcessorInformation.RelationProcessorPackage),
      Simple(LogicalProcessorInformation.RelationNumaNode)
    );

    var topology = LogicalProcessorInformation.Parse(buffer);
    Assert.That(topology.Sockets.Value, Is.EqualTo(2ul));
    Assert.That(topology.NumaNodes.Value, Is.EqualTo(1ul));
  }

  [Test]
  public void EachCacheLevelAndKindLandsInItsOwnField() {
    var buffer = Concat(
      Cache(1, 2, 48 * 1024),      // data
      Cache(1, 1, 32 * 1024),      // instruction
      Cache(2, 0, 1280 * 1024),    // unified
      Cache(3, 0, 24 * 1024 * 1024)
    );

    var topology = LogicalProcessorInformation.Parse(buffer);
    Assert.That(topology.L1Data.Value, Is.EqualTo(48ul * 1024));
    Assert.That(topology.L1Instruction.Value, Is.EqualTo(32ul * 1024));
    Assert.That(topology.L2.Value, Is.EqualTo(1280ul * 1024));
    Assert.That(topology.L3.Value, Is.EqualTo(24ul * 1024 * 1024));
  }

  /// <summary>
  /// The buffer describes every core's caches, so an eight-core machine repeats each one eight
  /// times. The first of each kind is kept rather than the last or the sum.
  /// </summary>
  [Test]
  public void RepeatedCacheRecordsDoNotAccumulate() {
    var buffer = Concat(Cache(3, 0, 16 * 1024 * 1024), Cache(3, 0, 16 * 1024 * 1024), Cache(3, 0, 16 * 1024 * 1024));

    Assert.That(LogicalProcessorInformation.Parse(buffer).L3.Value, Is.EqualTo(16ul * 1024 * 1024));
  }

  /// <summary>
  /// Records are not all the same length, so the walk must step by each record's own size. Stepping
  /// by a constant lands in the middle of the next record and reads its bytes as a relationship.
  /// </summary>
  [Test]
  public void RecordsOfDifferentSizesAreAllVisited() {
    var buffer = Concat(
      Cache(3, 0, 8 * 1024 * 1024),                                   // 48 bytes
      Core(0b0011),                                                   // 48 bytes
      Simple(LogicalProcessorInformation.RelationProcessorPackage),   // 32 bytes
      Cache(1, 2, 32 * 1024)                                          // 48 bytes
    );

    var topology = LogicalProcessorInformation.Parse(buffer);
    Assert.That(topology.L3.Value, Is.EqualTo(8ul * 1024 * 1024));
    Assert.That(topology.LogicalProcessors.Value, Is.EqualTo(2ul));
    Assert.That(topology.Sockets.Value, Is.EqualTo(1ul));
    Assert.That(topology.L1Data.Value, Is.EqualTo(32ul * 1024), "the record after the short one");
  }

  [Test]
  public void AProcessorSpanningMoreThanOneGroupCountsAllOfIt() {
    // A machine with more than 64 logical processors reports several groups per core record.
    var record = new byte[8 + 1 + 1 + 20 + 2 + 32];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationProcessorCore);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), 2);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), 0b0011);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), 0b0111);

    Assert.That(LogicalProcessorInformation.Parse(record).LogicalProcessors.Value, Is.EqualTo(5ul));
  }

  #region refusing a buffer that does not add up

  [Test]
  public void ARecordClaimingToBeShorterThanItsHeaderStopsTheWalk() {
    var record = new byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationProcessorPackage);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 4);

    // A size of four would advance by four for ever; the walk must stop instead of hanging.
    Assert.That(LogicalProcessorInformation.Parse(record).Sockets.HasValue, Is.False);
  }

  [Test]
  public void ARecordClaimingToRunPastTheEndIsRefused() {
    var record = Simple(LogicalProcessorInformation.RelationProcessorPackage);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 4096);

    Assert.That(LogicalProcessorInformation.Parse(record).Sockets.HasValue, Is.False);
  }

  [Test]
  public void AnEmptyOrTruncatedBufferYieldsNothingRatherThanZeroes() {
    foreach (var buffer in new[] { Array.Empty<byte>(), new byte[4] }) {
      var topology = LogicalProcessorInformation.Parse(buffer);
      // Not "zero cores": we do not know, and a machine with zero cores is not a thing.
      Assert.That(topology.PhysicalCores.HasValue, Is.False);
      Assert.That(topology.LogicalProcessors.HasValue, Is.False);
      Assert.That(topology.L3.HasValue, Is.False);
    }
  }

  [Test]
  public void ACoreRecordTooShortForItsMasksIsNotReadPastTheEnd() {
    var record = new byte[8 + 1 + 1 + 20 + 2 + 4];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationProcessorCore);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), 4);

    // It is still a core; there is just no readable mask, so it contributes no logical processors.
    var topology = LogicalProcessorInformation.Parse(record);
    Assert.That(topology.PhysicalCores.Value, Is.EqualTo(1ul));
    Assert.That(topology.LogicalProcessors.HasValue, Is.False);
  }

  #endregion

}
