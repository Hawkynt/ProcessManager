using System.Buffers.Binary;
using Hawkynt.ProcessManager.Model;
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
  private static byte[] Core(ulong affinityMask, byte efficiencyClass = 0, ushort group = 0)
    => Processor(LogicalProcessorInformation.RelationProcessorCore, affinityMask, efficiencyClass, group);

  /// <summary>A socket, covering every logical processor in it. The same record layout as a core.</summary>
  private static byte[] Package(ulong affinityMask, ushort group = 0)
    => Processor(LogicalProcessorInformation.RelationProcessorPackage, affinityMask, 0, group);

  private static byte[] Processor(uint relationship, ulong affinityMask, byte efficiencyClass, ushort group) {
    // relationship, size, flags, efficiency class, 20 reserved, group count, one GROUP_AFFINITY.
    var record = new byte[8 + 1 + 1 + 20 + 2 + 16];
    BinaryPrimitives.WriteUInt32LittleEndian(record, relationship);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    record[9] = efficiencyClass;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8 + 22), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8 + 24), affinityMask);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8 + 32), group);
    return record;
  }

  /// <summary>A NUMA node: its number at 8, and the processors on it in the mask at 32.</summary>
  private static byte[] Numa(uint node, ulong affinityMask) {
    var record = new byte[8 + 4 + 18 + 2 + 16];
    BinaryPrimitives.WriteUInt32LittleEndian(record, LogicalProcessorInformation.RelationNumaNode);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)record.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), node);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), affinityMask);
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

  #region where each processor sits (PRD §46)

  /// <summary>
  /// The heat map's whole reason for existing: two logical processors on one core do not do twice
  /// the work of one, and the map has to put them next to each other to show it.
  /// </summary>
  [Test]
  public void SmtSiblingsShareACoreNumber() {
    var topology = LogicalProcessorInformation.ParseTopology(Concat(Core(0b0011), Core(0b1100)));

    Assert.That(topology.Cores, Has.Count.EqualTo(4));
    Assert.That(topology.Cores[0].Core, Is.EqualTo(topology.Cores[1].Core), "0 and 1 share a core");
    Assert.That(topology.Cores[2].Core, Is.EqualTo(topology.Cores[3].Core), "2 and 3 share a core");
    Assert.That(topology.Cores[0].Core, Is.Not.EqualTo(topology.Cores[2].Core));
  }

  [Test]
  public void EachProcessorIsPutInTheSocketWhoseMaskNamesIt() {
    var buffer = Concat(
      Core(0b0011), Core(0b1100),
      Package(0b0011), Package(0b1100)
    );

    var topology = LogicalProcessorInformation.ParseTopology(buffer);
    Assert.That(topology.Packages, Is.EqualTo(new[] { 0, 1 }));
    Assert.That(topology.Of(0).Count, Is.EqualTo(2));
    Assert.That(topology.Of(1).Count, Is.EqualTo(2));
    foreach (var core in topology.Of(1))
      Assert.That(core.Logical, Is.GreaterThan(1));
  }

  /// <summary>
  /// Windows ranks cores by an efficiency class rather than naming two kinds, and higher is faster.
  /// A machine whose cores all share one class is not hybrid, and calling every core a performance
  /// core would put a distinction on the page the silicon does not have (PRD §5.3).
  /// </summary>
  [Test]
  public void OneEfficiencyClassIsAMachineThatIsNotHybrid() {
    var topology = LogicalProcessorInformation.ParseTopology(Concat(Core(0b0001, 1), Core(0b0010, 1)));

    Assert.That(topology.IsHybrid, Is.False);
    foreach (var core in topology.Cores)
      Assert.That(core.Kind, Is.EqualTo(CoreKind.Unknown));
  }

  [Test]
  public void TheFastestEfficiencyClassIsThePerformanceCores() {
    var buffer = Concat(Core(0b0011, 1), Core(0b0100, 0), Core(0b1000, 0));
    var topology = LogicalProcessorInformation.ParseTopology(buffer);

    Assert.That(topology.IsHybrid, Is.True);
    Assert.That(topology.Cores[0].Kind, Is.EqualTo(CoreKind.Performance));
    Assert.That(topology.Cores[1].Kind, Is.EqualTo(CoreKind.Performance), "its SMT sibling");
    Assert.That(topology.Cores[2].Kind, Is.EqualTo(CoreKind.Efficiency));
    Assert.That(topology.Cores[3].Kind, Is.EqualTo(CoreKind.Efficiency));

    // And the order the map draws them in: fast cores first, siblings adjacent.
    var drawn = new List<int>();
    foreach (var core in topology.Of(-1))
      drawn.Add(core.Logical);

    Assert.That(drawn, Is.EqualTo(new[] { 0, 1, 2, 3 }), "no package records, so every core is in the same unnamed one");
  }

  [Test]
  public void EachProcessorGetsTheNodeWhoseMaskNamesIt() {
    var buffer = Concat(Core(0b0001), Core(0b0010), Numa(0, 0b0001), Numa(1, 0b0010));
    var topology = LogicalProcessorInformation.ParseTopology(buffer);

    Assert.That(topology.Nodes, Is.EqualTo(new[] { 0, 1 }));
    Assert.That(topology.OnNode(0)[0].Logical, Is.EqualTo(0));
    Assert.That(topology.OnNode(1)[0].Logical, Is.EqualTo(1));
  }

  /// <summary>
  /// Past sixty-four processors Windows splits them into groups, and a processor's number is its
  /// position in its group plus sixty-four per group before it. Reading the bit index alone would
  /// report group 1's processors as though they were group 0's — sixty-four cells drawn twice.
  /// </summary>
  [Test]
  public void ProcessorsInASecondGroupAreNumberedPastTheFirst() {
    var topology = LogicalProcessorInformation.ParseTopology(Concat(Core(0b0001, 0, 0), Core(0b0001, 0, 1)));

    Assert.That(topology.Cores, Has.Count.EqualTo(2));
    Assert.That(topology.Cores[0].Logical, Is.EqualTo(0));
    Assert.That(topology.Cores[1].Logical, Is.EqualTo(64));
  }

  [Test]
  public void ABufferWithNoProcessorRecordsIsNoTopologyRatherThanAnEmptyOne() {
    Assert.That(LogicalProcessorInformation.ParseTopology([]), Is.SameAs(CpuTopology.Empty));
    Assert.That(LogicalProcessorInformation.ParseTopology(Cache(3, 0, 1024)), Is.SameAs(CpuTopology.Empty));
  }

  #endregion

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
