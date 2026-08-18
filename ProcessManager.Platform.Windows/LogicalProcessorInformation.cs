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
