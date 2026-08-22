namespace Hawkynt.ProcessManager.Query;

/// <summary>What a file turned out to be, from its own first bytes (PRD §53).</summary>
public enum BinaryFormat : byte {

  /// <summary>Nothing could be read at all — the file is gone, or not ours to look at.</summary>
  Unreadable,

  /// <summary>Readable, and none of the three formats. A font, an archive, a data blob.</summary>
  Unknown,

  Elf,

  /// <summary>A Windows binary or a managed assembly; which of the two is a fact about its headers.</summary>
  PortableExecutable,

  MachO,

  /// <summary>Several Mach-O images in one file, one per architecture.</summary>
  UniversalBinary,

  /// <summary>A shebang, which on Unix is as real a way to start a program as a header is.</summary>
  Script,

}

/// <summary>Which page of a binary's detail is being asked for (PRD §53, §35).</summary>
/// <remarks>
/// One page per question rather than one enormous dump, for the reason §5.2 gives: somebody opening
/// this has one question, and thirty screens of section table between them and the answer is the
/// same as not having it. The names are the ones the formats' own documentation uses, so that an
/// answer here can be held against <c>readelf</c>, <c>objdump</c> or <c>otool</c> without a glossary.
/// </remarks>
public enum BinaryPage : byte {
  Summary,
  Headers,
  Segments,
  Sections,
  Dynamic,
  Dependencies,
  Imports,
  Exports,
  Symbols,
  Relocations,
  Resources,
  Signature,

  /// <summary>
  /// The digests of the file, which are their own page because they are their own cost: every other
  /// page reads kilobytes of structure and this one reads every byte there is (PRD §5.4, §70).
  /// </summary>
  Hashes,
  Debug,
  Security,
  Strings,
}

/// <summary>
/// One page of a binary's detail, as a table of already-formatted cells (PRD §53).
/// </summary>
/// <param name="Title">What the page is called, for a caption.</param>
/// <param name="Headers">The column headings. Two of them — a name and a value — for the list pages.</param>
/// <param name="Rows">The cells, one array per row.</param>
/// <param name="Note">
/// The sentence that has to be read alongside the rows, or null when the rows speak for themselves.
/// This is where "an ELF has no resource section" and "this table was truncated" live: a page with no
/// rows and no note is indistinguishable from a page nobody could read, which is the exact confusion
/// §72.3 exists to stop.
/// </param>
public readonly record struct BinaryView(
  string Title,
  IReadOnlyList<string> Headers,
  IReadOnlyList<string[]> Rows,
  string? Note = null
) {

  /// <summary>A page with nothing on it but the reason there is nothing on it.</summary>
  public static BinaryView Empty(string title, string note) => new(title, ["Field", "Value"], [], note);

  /// <summary>A two-column list of facts, which is the shape most of these pages have.</summary>
  public static BinaryView Facts(string title, IReadOnlyList<string[]> rows, string? note = null)
    => new(title, ["Field", "Value"], rows, note);

  /// <summary>The page as text, one row per line, every column as wide as its widest cell.</summary>
  /// <remarks>
  /// Here rather than in each front-end because three of them would otherwise write it: the command
  /// line prints it, the window copies it to the clipboard, and a test reads it to find out what a
  /// page says without a display (PRD §58, §95).
  /// </remarks>
  public string Describe() {
    var columns = this.Headers.Count;
    var widths = new int[columns];
    for (var i = 0; i < columns; ++i)
      widths[i] = this.Headers[i].Length;

    foreach (var row in this.Rows)
      for (var i = 0; i < columns && i < row.Length; ++i)
        widths[i] = Math.Max(widths[i], row[i].Length);

    var text = new System.Text.StringBuilder();
    text.Append(Line(this.Headers, widths, columns));
    foreach (var row in this.Rows)
      text.Append('\n').Append(Line(row, widths, columns));

    return text.ToString();
  }

  private static string Line(IReadOnlyList<string> cells, int[] widths, int columns) {
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < columns; ++i) {
      if (i > 0)
        text.Append("  ");

      var cell = i < cells.Count ? cells[i] : string.Empty;
      // The last column is never padded: a trailing run of spaces on every line of a name column is
      // invisible on a screen and is not invisible to whatever the output is piped into.
      text.Append(i == columns - 1 ? cell : cell.PadRight(widths[i]));
    }

    return text.ToString().TrimEnd();
  }

}
