using System.Buffers.Binary;
using System.Numerics;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What <c>GetLogicalProcessorInformationEx</c> returns, read out of its buffer.
/// </summary>
/// <remarks>
/// A span rather than a pointer, and no platform attribute, so the walk is tested on every CI leg
/// against hand-built buffers rather than only on a machine that has the API — the same reason the
/// bulk-query parser takes one (PRD §9.4). The layout is variable-length and self-describing: every
/// record states its own size, and skipping by anything else walks into the middle of the next one.
/// </remarks>
internal static class LogicalProcessorInformation {

  public const int RelationProcessorCore = 0;
  public const int RelationNumaNode = 1;
  public const int RelationCache = 2;
  public const int RelationProcessorPackage = 3;

  private const int _CacheUnified = 0;
  private const int _CacheInstruction = 1;
  private const int _CacheData = 2;

  /// <summary>What one walk of the buffer found.</summary>
  public readonly record struct Topology(
    Counter PhysicalCores,
    Counter LogicalProcessors,
    Counter Sockets,
    Counter NumaNodes,
    Counter L1Data,
    Counter L1Instruction,
    Counter L2,
    Counter L3
  );

  public static Topology Parse(ReadOnlySpan<byte> buffer) {
    int cores = 0, sockets = 0, numaNodes = 0;
    var logical = 0;
    ulong l1Data = 0, l1Instruction = 0, l2 = 0, l3 = 0;

    var offset = 0;
    while (offset + 8 <= buffer.Length) {
      var relationship = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);

      // A record that claims to be shorter than its own header, or longer than what is left, would
      // loop forever or read past the end. Neither is worth trusting a kernel buffer about.
      if (size < 8 || offset + size > buffer.Length)
        break;

      var body = buffer.Slice(offset, size);
      switch (relationship) {
        case RelationProcessorCore:
          ++cores;
          logical += CountLogical(body);
          break;

        case RelationProcessorPackage:
          ++sockets;
          break;

        case RelationNumaNode:
          ++numaNodes;
          break;

        case RelationCache: {
          if (size < 24)
            break;

          var level = body[8];
          var type = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
          var cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);

          // First one of each kind wins: the buffer describes every core's caches, and they are the
          // same size on any machine this program will meet. Task Manager shows one of them too.
          switch (level, type) {
            case (1, _CacheData) when l1Data == 0: l1Data = cacheSize; break;
            case (1, _CacheInstruction) when l1Instruction == 0: l1Instruction = cacheSize; break;
            case (1, _CacheUnified) when l1Data == 0: l1Data = cacheSize; break;
            case (2, _) when l2 == 0: l2 = cacheSize; break;
            case (3, _) when l3 == 0: l3 = cacheSize; break;
            default: break;
          }

          break;
        }

        default: break;
      }

      offset += size;
    }

    return new(
      Count(cores),
      Count(logical),
      Count(sockets),
      Count(numaNodes),
      Bytes(l1Data),
      Bytes(l1Instruction),
      Bytes(l2),
      Bytes(l3)
    );
  }

  #region where each logical processor sits (PRD §46)

  /// <summary>PROCESSOR_RELATIONSHIP and NUMA_NODE_RELATIONSHIP both put their group count here.</summary>
  private const int _GroupCountOffset = 8 + 1 + 1 + 20;

  /// <summary>…and the masks themselves immediately after it.</summary>
  private const int _FirstMaskOffset = _GroupCountOffset + 2;

  /// <summary>GROUP_AFFINITY: a 64-bit mask, the group number, and three reserved words.</summary>
  private const int _GroupAffinitySize = 16;

  /// <summary>
  /// The same buffer read for placement rather than for counts (PRD §46).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The heat map needs to know which logical processors share a core, which socket each is in,
  /// which NUMA node its memory is on and whether it is a performance or an efficiency core. Windows
  /// publishes all four in the buffer <see cref="Parse"/> already walks — it was simply never read
  /// out of it, which left the map one flat row on every Windows machine.
  /// </para>
  /// <para>
  /// Cores are numbered by the order the kernel reports them, because Windows does not publish a
  /// core id the way <c>/sys</c> does. The number is only ever used to say that two logical
  /// processors share one core, which it does exactly.
  /// </para>
  /// </remarks>
  public static CpuTopology ParseTopology(ReadOnlySpan<byte> buffer) {
    var kinds = new Dictionary<int, byte>();
    var coreOf = new Dictionary<int, int>();
    var packageOf = new Dictionary<int, int>();
    var nodeOf = new Dictionary<int, int>();
    var order = new List<int>();
    var core = 0;
    var package = 0;

    var offset = 0;
    while (offset + 8 <= buffer.Length) {
      var relationship = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..]);
      var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(offset + 4)..]);
      if (size < 8 || offset + size > buffer.Length)
        break;

      var body = buffer.Slice(offset, size);
      switch (relationship) {
        case RelationProcessorCore: {
          // The efficiency class is a rank rather than a kind: Windows documents higher as more
          // performant and says nothing about how many there are. Which of them count as efficiency
          // cores is decided below, once every core has been seen — a single class is a machine
          // whose cores are all alike, not a machine of efficiency cores.
          var efficiency = size > 9 ? body[9] : (byte)0;
          foreach (var logical in Members(body)) {
            kinds[logical] = efficiency;
            coreOf[logical] = core;
            if (!order.Contains(logical))
              order.Add(logical);
          }

          ++core;
          break;
        }

        case RelationProcessorPackage: {
          foreach (var logical in Members(body))
            packageOf[logical] = package;

          ++package;
          break;
        }

        case RelationNumaNode: {
          var node = size > 12 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) : -1;
          foreach (var logical in Members(body))
            nodeOf[logical] = node;

          break;
        }

        default: break;
      }

      offset += size;
    }

    if (order.Count == 0)
      return CpuTopology.Empty;

    order.Sort();
    var fastest = byte.MinValue;
    var slowest = byte.MaxValue;
    foreach (var rank in kinds.Values) {
      fastest = Math.Max(fastest, rank);
      slowest = Math.Min(slowest, rank);
    }

    var hybrid = fastest != slowest;
    var cores = new List<CoreDescriptor>(order.Count);
    foreach (var logical in order)
      cores.Add(new(
        logical,
        packageOf.GetValueOrDefault(logical, -1),
        coreOf.GetValueOrDefault(logical, -1),
        !hybrid ? CoreKind.Unknown
          : kinds.GetValueOrDefault(logical) == fastest ? CoreKind.Performance
          : CoreKind.Efficiency,
        nodeOf.GetValueOrDefault(logical, -1)
      ));

    return new(cores);
  }

  /// <summary>
  /// The logical processors one record's group affinity masks name.
  /// </summary>
  /// <remarks>
  /// A processor's number is its position in its group plus sixty-four per group before it, which is
  /// how every per-processor counter on Windows is keyed. A machine with more than sixty-four
  /// processors is the only one where that differs from the bit index, and it is exactly the machine
  /// a heat map is for.
  /// </remarks>
  private static List<int> Members(ReadOnlySpan<byte> record) {
    var members = new List<int>();
    if (record.Length < _FirstMaskOffset)
      return members;

    var groups = BinaryPrimitives.ReadUInt16LittleEndian(record[_GroupCountOffset..]);
    for (var i = 0; i < groups; ++i) {
      var maskOffset = _FirstMaskOffset + (i * _GroupAffinitySize);
      if (maskOffset + 10 > record.Length)
        break;

      var mask = BinaryPrimitives.ReadUInt64LittleEndian(record[maskOffset..]);
      var group = BinaryPrimitives.ReadUInt16LittleEndian(record[(maskOffset + 8)..]);
      for (var bit = 0; bit < 64; ++bit)
        if ((mask & (1ul << bit)) != 0)
          members.Add((group * 64) + bit);
    }

    return members;
  }

  #endregion

  /// <summary>
  /// How many logical processors one core covers, counted from its group affinity masks.
  /// </summary>
  /// <remarks>
  /// A core with SMT reports two bits; a machine with more than 64 processors reports several
  /// groups. Counting records rather than bits would report half the thread count on anything
  /// hyper-threaded.
  /// </remarks>
  private static int CountLogical(ReadOnlySpan<byte> record) {
    // PROCESSOR_RELATIONSHIP: flags, efficiency class, 20 reserved, group count, then the masks.
    const int groupCountOffset = 8 + 1 + 1 + 20;
    const int firstMaskOffset = groupCountOffset + 2;
    const int groupAffinitySize = 16;

    if (record.Length < firstMaskOffset)
      return 0;

    var groups = BinaryPrimitives.ReadUInt16LittleEndian(record[groupCountOffset..]);
    var total = 0;
    for (var i = 0; i < groups; ++i) {
      var maskOffset = firstMaskOffset + (i * groupAffinitySize);
      if (maskOffset + 8 > record.Length)
        break;

      total += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(record[maskOffset..]));
    }

    return total;
  }

  private static Counter Count(int value)
    => value > 0 ? Counter.Of((ulong)value) : Counter.NotSupported;

  private static Counter Bytes(ulong value)
    => value > 0 ? Counter.Of(value) : Counter.NotSupported;

}
