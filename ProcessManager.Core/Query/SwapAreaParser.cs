using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/swaps</c>: where the machine puts what it cannot keep (PRD §48).
/// </summary>
/// <remarks>
/// One line per area, and the two kinds are not interchangeable. A partition names a block device
/// directly; a file names a path, and which disk that path is on is a question for the mount table
/// rather than for this file. The distinction is kept rather than resolved here, because resolving
/// it needs a file system and this parser deliberately touches none.
/// <para>
/// No platform attribute and no file access, so it runs on every CI leg against recorded text
/// (PRD §9.2).
/// </para>
/// </remarks>
public static class SwapAreaParser {

  /// <summary>What kind of thing a swap area is.</summary>
  public enum SwapKind : byte {

    /// <summary>Anything the file did not call a partition, which is how a stale entry reads.</summary>
    Unknown,

    /// <summary>A whole block device given over to swapping.</summary>
    Partition,

    /// <summary>A file on some file system, which is what a modern installer produces.</summary>
    File,

  }

  /// <summary>One swap area.</summary>
  /// <param name="Path">
  /// The device node or the file's path, exactly as the kernel writes it. A deleted area is written
  /// with a <c>\040(deleted)</c> suffix, which is kept: it is a fact about the machine and hiding it
  /// would make a swap area nobody can find look like an ordinary one.
  /// </param>
  public readonly record struct SwapArea(string Path, SwapKind Kind, ulong SizeKilobytes, ulong UsedKilobytes);

  /// <summary>Every area the file lists, in its order.</summary>
  public static IReadOnlyList<SwapArea> Parse(ReadOnlySpan<byte> content) {
    var areas = new List<SwapArea>();
    var scanner = new AsciiScanner(content);
    var line = 0;
    while (!scanner.IsEmpty) {
      var text = scanner.NextLine();
      // One header line — "Filename Type Size Used Priority" — and then the areas.
      if (++line == 1 || text.IsEmpty)
        continue;

      var fields = new AsciiScanner(text);
      var path = fields.NextField();
      if (path.IsEmpty)
        continue;

      var kind = fields.NextField();
      areas.Add(new(
        Encoding.UTF8.GetString(path),
        Matches(kind, "partition"u8) ? SwapKind.Partition
          : Matches(kind, "file"u8) ? SwapKind.File
          : SwapKind.Unknown,
        fields.NextUInt64(),
        fields.NextUInt64()
      ));
    }

    return areas;
  }

  private static bool Matches(ReadOnlySpan<byte> field, ReadOnlySpan<byte> word)
    => field.Length == word.Length && field.SequenceEqual(word);

}
