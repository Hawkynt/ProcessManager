using System.Globalization;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// A read-only view of one binary on disk (PRD §53).
/// </summary>
/// <remarks>
/// <para>
/// <b>A viewer, and nothing else.</b> §53's last line is that this is not a patcher, and it is
/// enforced rather than asserted: the file is opened for reading and there is no method here, on any
/// of the three format readers, that writes a byte anywhere. A test enumerates the surface so that
/// the day somebody adds one in good faith it fails.
/// </para>
/// <para>
/// The three formats are told apart by their own first bytes and never by their extension. A
/// <c>.dll</c> names a managed assembly and a Windows library both, a <c>.so</c> is occasionally a
/// linker script, and a Wine process has files of every kind mapped at once — so the name is exactly
/// the evidence that cannot settle it (PRD §5.3).
/// </para>
/// <para>
/// One page at a time, because a binary inspector that read everything on opening would walk a
/// symbol table nobody asked to see. The file stays open across pages so the window's tab strip
/// costs one open rather than sixteen (PRD §5.4).
/// </para>
/// </remarks>
public sealed class BinaryInspector : IDisposable {

  private readonly ImageBytes? _bytes;
  private readonly ElfInspector? _elf;
  private readonly PeInspector? _pe;
  private readonly MachOInspector? _macho;

  private BinaryInspector(
    ImageBytes? bytes,
    string path,
    BinaryFormat format,
    string? reason,
    ElfInspector? elf,
    PeInspector? pe,
    MachOInspector? macho,
    IReadOnlyList<(long Offset, long Size, string Architecture)> slices
  ) {
    this._bytes = bytes;
    this.Path = path;
    this.Format = format;
    this.Reason = reason;
    this._elf = elf;
    this._pe = pe;
    this._macho = macho;
    this.Slices = slices;
  }

  public string Path { get; }

  public BinaryFormat Format { get; }

  /// <summary>Why nothing could be read, or null when something was.</summary>
  public string? Reason { get; }

  public long Length => this._bytes?.Length ?? 0;

  /// <summary>The images inside a universal binary, or empty for everything else.</summary>
  public IReadOnlyList<(long Offset, long Size, string Architecture)> Slices { get; }

  /// <summary>
  /// Opens a file and works out what it is.
  /// </summary>
  /// <param name="slice">
  /// Which image of a universal binary to describe. Ignored for every other format, and out of range
  /// means the first — a caller asking for the fourth architecture of a file with two has asked a
  /// question about a file it has not looked at yet.
  /// </param>
  /// <returns>
  /// Never null. A file that cannot be read is an inspector whose every page says so, because a null
  /// here would make each of the sixteen call sites invent its own way of saying the same thing.
  /// </returns>
  public static BinaryInspector Open(string? path, int slice = 0) {
    var bytes = ImageBytes.Open(path, out var reason);
    if (bytes is null)
      return new(null, path ?? string.Empty, BinaryFormat.Unreadable, reason, null, null, null, []);

    var read = bytes.Reader;
    if (ElfInspector.TryOpen(read, bytes.Length) is { } elf)
      return new(bytes, bytes.Path, BinaryFormat.Elf, null, elf, null, null, []);

    if (PeInspector.TryOpen(read, bytes.Length) is { } pe)
      return new(bytes, bytes.Path, BinaryFormat.PortableExecutable, null, null, pe, null, []);

    var slices = MachOInspector.Slices(read);
    if (slices.Count > 0) {
      var chosen = slice >= 0 && slice < slices.Count ? slices[slice] : slices[0];
      return new(
        bytes,
        bytes.Path,
        BinaryFormat.UniversalBinary,
        null,
        null,
        null,
        MachOInspector.TryOpen(read, bytes.Length, chosen.Offset),
        slices
      );
    }

    if (MachOInspector.TryOpen(read, bytes.Length) is { } macho)
      return new(bytes, bytes.Path, BinaryFormat.MachO, null, null, null, macho, []);

    Span<byte> head = stackalloc byte[2];
    var format = bytes.Read(0, head) >= 2 && head[0] == (byte)'#' && head[1] == (byte)'!'
      ? BinaryFormat.Script
      : BinaryFormat.Unknown;

    return new(bytes, bytes.Path, format, null, null, null, null, []);
  }

  /// <summary>One page of the report.</summary>
  public BinaryView View(BinaryPage page) {
    if (this.Reason is { } reason)
      return BinaryView.Empty(Title(page), reason);

    // The two pages that are not about the file's structure. Hashing reads every byte and scanning
    // for text reads them again, so neither may be reached by asking a format reader for a page it
    // would otherwise build for free (PRD §5.4).
    if (page == BinaryPage.Hashes)
      return this.Hashes();

    if (page == BinaryPage.Strings)
      return this.Strings(TextScanOptions.Default);

    if (this._elf is { } elf)
      return this.WithSlices(elf.View(page), page);

    if (this._pe is { } pe)
      return this.WithSlices(pe.View(page), page);

    if (this._macho is { } macho)
      return this.WithSlices(macho.View(page), page);

    return page == BinaryPage.Summary ? this.ForeignSummary() : BinaryView.Empty(Title(page), this.NotAnImage());
  }

  /// <summary>
  /// The summary of a file that is readable and is not one of the three formats.
  /// </summary>
  /// <remarks>
  /// A real answer rather than a refusal. A font, a locale archive and a shell script are all things
  /// somebody points an inspector at, and reporting them as unreadable would be a lie about the
  /// permissions (PRD §72.3).
  /// </remarks>
  private BinaryView ForeignSummary() {
    var rows = new List<string[]>();
    rows.Add(["format", this.Format == BinaryFormat.Script ? "a script — it begins with a shebang" : "not an executable image"]);
    rows.Add(["path", this.Path]);
    rows.Add(["file size", this.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes"]);
    if (this._bytes is { } bytes) {
      Span<byte> head = stackalloc byte[16];
      var got = bytes.Read(0, head);
      if (got > 0)
        rows.Add(["first bytes", Convert.ToHexString(head[..got]).ToLowerInvariant()]);
    }

    return BinaryView.Facts("Summary", rows, this.NotAnImage());
  }

  private string NotAnImage()
    => this.Format == BinaryFormat.Script
      ? "This is a script rather than a compiled image, so it has no headers, no sections and no "
        + "symbols. The strings page still works on it, and on a script that is the whole of it."
      : "These bytes are not an ELF, a Portable Executable or a Mach-O. The strings page still works, "
        + "and the summary above says what the first bytes actually are.";

  /// <summary>Adds the architecture list to a universal binary's summary, which no slice knows about.</summary>
  private BinaryView WithSlices(BinaryView view, BinaryPage page) {
    if (page != BinaryPage.Summary || this.Slices.Count == 0)
      return view;

    var rows = new List<string[]>(view.Rows);
    foreach (var (offset, size, architecture) in this.Slices)
      rows.Add([
        "architecture",
        $"{architecture} — {size.ToString("N0", CultureInfo.InvariantCulture)} bytes at 0x{offset.ToString("x", CultureInfo.InvariantCulture)}",
      ]);

    return view with {
      Rows = rows,
      Note = $"A universal binary of {this.Slices.Count.ToString(CultureInfo.InvariantCulture)} images. "
        + "Every other page describes one of them, and which one is the caller's choice.",
    };
  }

  /// <summary>
  /// The runs of readable text in the file (PRD §35).
  /// </summary>
  /// <remarks>
  /// Its own method rather than a page of <see cref="View"/>, because it is the one reading here
  /// whose cost is the size of the file: every other page reads a few kilobytes of structure, and
  /// this one reads every byte there is. A front-end must therefore be able to say what it is about
  /// to cost before it starts, which is why <see cref="ScanCost"/> exists beside it (PRD §5.4, §35).
  /// </remarks>
  public BinaryView Strings(in TextScanOptions options) {
    if (this._bytes is not { } bytes)
      return BinaryView.Empty("Strings", this.Reason ?? "there is nothing to scan");

    var result = BinaryStrings.Scan(bytes.Reader, bytes.Length, in options);
    var rows = new List<string[]>(result.Runs.Count);
    foreach (var run in result.Runs)
      rows.Add([
        "0x" + run.Offset.ToString("x", CultureInfo.InvariantCulture),
        BinaryStrings.Name(run.Encoding),
        run.Text.Length.ToString(CultureInfo.InvariantCulture),
        run.Text,
      ]);

    var note = $"{result.Runs.Count.ToString("N0", CultureInfo.InvariantCulture)} runs of at least "
      + $"{Math.Max(1, options.MinimumLength).ToString(CultureInfo.InvariantCulture)} characters in "
      + $"{result.BytesScanned.ToString("N0", CultureInfo.InvariantCulture)} bytes.";

    if (result.Truncated)
      note += " The scan stopped at the limit on how many to keep; there are more in the file.";

    if (options.Pattern is { Length: > 0 } pattern)
      note += $" Filtered by {Describe(ResourceSearch.ModeOf(pattern))} \"{pattern}\".";

    return new("Strings", ["Offset", "Encoding", "Length", "Text"], rows, note);
  }

  private static string Describe(SearchMode mode) => mode switch {
    SearchMode.Regex => "the regular expression",
    SearchMode.Wildcard => "the wildcard",
    SearchMode.Exact => "the exact value",
    _ => "the substring",
  };

  /// <summary>
  /// How many bytes a strings scan of the whole file would read (PRD §35).
  /// </summary>
  /// <remarks>
  /// So a front-end can say what it is about to cost <em>before</em> it starts rather than after. A
  /// warning that arrives once the window has already been unresponsive for four seconds is not a
  /// warning, it is an apology.
  /// </remarks>
  public long ScanCost => this.Length;

  /// <summary>
  /// The parts of the file that hold code, so a scan can be restricted to them (PRD §35).
  /// </summary>
  /// <remarks>
  /// §35's "executable image only" filter, for a file. The memory filters beside it in that section
  /// — private, mapped, one region — are about a running process's address space and are refused for
  /// the reason §25.5 gives: reading it needs <c>PTRACE_MODE_ATTACH</c>, which Yama denies by default.
  /// </remarks>
  public IReadOnlyList<(long Offset, long Length, string Name)> ExecutableRegions
    => this._elf?.ExecutableRegions ?? this._pe?.ExecutableRegions ?? this._macho?.ExecutableRegions ?? [];

  /// <summary>The two digests of the file, on the one read of it they share (PRD §25.6, §70).</summary>
  /// <remarks>
  /// Asked for and never a side effect. <b>A hash is not a verdict</b>: it says what the bytes are
  /// and nothing about whether they are signed, trusted or known, and this program keeps those four
  /// apart everywhere (PRD §70).
  /// </remarks>
  public BinaryView Hashes() {
    var digest = FileDigest.Of(this.Path);
    var rows = new List<string[]>();
    rows.Add(["path", this.Path]);
    rows.Add(["size", this.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes"]);
    rows.Add(["sha-256", digest.Sha256 ?? digest.Reason ?? "not computed"]);
    rows.Add(["sha-1", digest.Sha1 ?? digest.Reason ?? "not computed"]);
    return BinaryView.Facts(
      "Hashes",
      rows,
      "**A hash is not a verdict.** It says what these bytes are and nothing about whether anybody "
      + "signed them, whether this machine trusts whoever did, or whether anybody has ever seen them "
      + "before. §70 keeps those four apart and so does this page."
    );
  }

  public void Dispose() => this._bytes?.Dispose();

  /// <summary>What a page is called before anything has been read, for an error view.</summary>
  public static string Title(BinaryPage page) => page switch {
    BinaryPage.Headers => "Headers",
    BinaryPage.Segments => "Segments",
    BinaryPage.Sections => "Sections",
    BinaryPage.Dynamic => "Dynamic",
    BinaryPage.Dependencies => "Dependencies",
    BinaryPage.Imports => "Imports",
    BinaryPage.Exports => "Exports",
    BinaryPage.Symbols => "Symbols",
    BinaryPage.Relocations => "Relocations",
    BinaryPage.Resources => "Resources",
    BinaryPage.Signature => "Signature",
    BinaryPage.Hashes => "Hashes",
    BinaryPage.Debug => "Debug information",
    BinaryPage.Security => "Security properties",
    BinaryPage.Strings => "Strings",
    _ => "Summary",
  };

  /// <summary>
  /// Reads a page name the way somebody would type it.
  /// </summary>
  /// <remarks>
  /// The plural and the singular both, and the names the three formats' own tools use — a reader who
  /// types <c>segments</c> for an ELF and <c>directories</c> for a PE means the same page, so both
  /// spellings reach it (PRD §5.3).
  /// </remarks>
  public static bool TryParsePage(string? name, out BinaryPage page) {
    page = BinaryPage.Summary;
    switch (name?.ToLowerInvariant()) {
      case null or "" or "summary" or "overview": return true;
      case "headers" or "header": page = BinaryPage.Headers; return true;
      case "segments" or "segment" or "program-headers" or "phdr": page = BinaryPage.Segments; return true;
      case "sections" or "section": page = BinaryPage.Sections; return true;
      case "dynamic" or "directories" or "commands": page = BinaryPage.Dynamic; return true;
      case "dependencies" or "deps" or "needed" or "libraries": page = BinaryPage.Dependencies; return true;
      case "imports" or "import": page = BinaryPage.Imports; return true;
      case "exports" or "export": page = BinaryPage.Exports; return true;
      case "symbols" or "symbol" or "syms": page = BinaryPage.Symbols; return true;
      case "relocations" or "relocs" or "reloc": page = BinaryPage.Relocations; return true;
      case "resources" or "resource": page = BinaryPage.Resources; return true;
      case "signature" or "signatures": page = BinaryPage.Signature; return true;
      case "hashes" or "hash" or "digest": page = BinaryPage.Hashes; return true;
      case "debug": page = BinaryPage.Debug; return true;
      case "security" or "mitigations": page = BinaryPage.Security; return true;
      case "strings" or "text": page = BinaryPage.Strings; return true;
      default: return false;
    }
  }

  /// <summary>Every page name, for a help line and for the message a mistyped one gets.</summary>
  public const string PageVocabulary =
    "summary, headers, segments, sections, dynamic, dependencies, imports, exports, symbols, "
    + "relocations, resources, signature, hashes, debug, security, strings";

  /// <summary>Every page, in the order a front-end should offer them.</summary>
  public static ReadOnlySpan<BinaryPage> Pages => [
    BinaryPage.Summary,
    BinaryPage.Headers,
    BinaryPage.Segments,
    BinaryPage.Sections,
    BinaryPage.Dynamic,
    BinaryPage.Dependencies,
    BinaryPage.Imports,
    BinaryPage.Exports,
    BinaryPage.Symbols,
    BinaryPage.Relocations,
    BinaryPage.Resources,
    BinaryPage.Signature,
    BinaryPage.Hashes,
    BinaryPage.Debug,
    BinaryPage.Security,
    BinaryPage.Strings,
  ];

}
