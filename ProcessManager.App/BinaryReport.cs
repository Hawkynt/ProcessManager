using System.Globalization;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--inspect</c>: what a binary on disk is, one page at a time (PRD §53, §59).
/// </summary>
/// <remarks>
/// <para>
/// The same pages the window's inspector shows, from the same builder, so the two cannot disagree
/// about what a file says — the argument that put the process fields in one registry (§5.1, §58).
/// Until this existed a binary inspector would have been the largest thing §102 forbids: visible in
/// a front-end and obtainable nowhere else.
/// </para>
/// <para>
/// Text only, and deliberately. Every cell has already been through a formatter — addresses in hex,
/// counts grouped, flags spelled out — and §76 requires that a machine format carry the raw
/// measurement rather than the rounded string a screen shows. Offering <c>--format csv</c> here
/// would be a promise this page cannot keep, so it is not offered.
/// </para>
/// <para>
/// <b>Read-only.</b> There is no switch here that writes to the file, on any page, and there is not
/// going to be one: §53's last line is that this is a viewer and not a patcher.
/// </para>
/// </remarks>
internal static class BinaryReport {

  public static int Run(CommandLineOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    using var inspector = BinaryInspector.Open(options.InspectPath);
    if (inspector.Reason is { } reason) {
      Console.Error.WriteLine($"procman: {options.InspectPath}: {reason}.");
      return 1;
    }

    var view = options.InspectPage == BinaryPage.Strings
      ? Strings(inspector, options)
      : inspector.View(options.InspectPage);

    Console.WriteLine($"{inspector.Path} — {view.Title.ToLowerInvariant()}");
    Console.WriteLine();
    if (view.Rows.Count > 0) {
      Console.WriteLine(view.Describe());
      if (view.Note is { Length: > 0 } footnote) {
        Console.WriteLine();
        Console.WriteLine(Wrap(footnote));
      }

      return 0;
    }

    // Nothing on stdout beyond the heading, and the reason on stderr: an empty page and a page this
    // user may not read look identical from here, and only one of the two is worth acting on
    // (PRD §5.3, §72.3).
    Console.Error.WriteLine(Wrap(view.Note ?? "there is nothing on this page"));
    return 2;
  }

  /// <summary>
  /// The strings page, which is the one that reads every byte of the file (PRD §35).
  /// </summary>
  /// <remarks>
  /// <b>The cost is said before it is paid.</b> A scan of a three-hundred-megabyte runtime image is
  /// seconds of disk, and a command that started one without a word would look like a command that
  /// had hung. It goes to stderr so that a redirected run still gets only the strings.
  /// </remarks>
  private static BinaryView Strings(BinaryInspector inspector, CommandLineOptions options) {
    var regions = options.TextCodeOnly ? inspector.ExecutableRegions : [];
    if (options.TextCodeOnly && regions.Count == 0)
      Console.Error.WriteLine(
        "procman: --code-only was asked for and this file names no executable region; scanning all of it."
      );

    var bytes = regions.Count > 0 ? Sum(regions) : inspector.ScanCost;
    Console.Error.WriteLine(
      $"procman: scanning {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes for text; "
      + "this reads the whole of what it scans."
    );

    var options0 = TextScanOptions.Default with {
      MinimumLength = options.MinimumTextLength,
      Pattern = options.TextPattern,
    };

    if (regions.Count == 0)
      return inspector.Strings(in options0);

    // One scan per code region rather than one over the file, because "executable image only" is a
    // statement about which bytes, and a scan of the whole file with the others filtered out
    // afterwards would have read them anyway.
    var rows = new List<string[]>();
    foreach (var (offset, length, name) in regions) {
      var view = inspector.Strings(options0 with { From = offset, Length = length });
      foreach (var row in view.Rows)
        rows.Add([row[0], row[1], row[2], row[3], name]);
    }

    return new(
      "Strings",
      ["Offset", "Encoding", "Length", "Text", "In"],
      rows,
      $"{rows.Count.ToString("N0", CultureInfo.InvariantCulture)} runs in "
      + $"{regions.Count.ToString(CultureInfo.InvariantCulture)} executable regions, "
      + $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes."
    );
  }

  private static long Sum(IReadOnlyList<(long Offset, long Length, string Name)> regions) {
    long total = 0;
    foreach (var region in regions)
      total += region.Length;

    return total;
  }

  /// <summary>
  /// A note wrapped to something a terminal can read, with the emphasis markers taken out.
  /// </summary>
  /// <remarks>
  /// The notes are written once and shown in three places, so they carry Markdown emphasis for the
  /// window that can render it. A terminal cannot, and asterisks around a phrase read as noise
  /// rather than as stress (PRD §58).
  /// </remarks>
  private static string Wrap(string note) {
    var text = note.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
    var width = Math.Clamp(SafeWidth(), 40, 100);
    var line = new System.Text.StringBuilder();
    var wrapped = new System.Text.StringBuilder();
    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
      if (line.Length > 0 && line.Length + 1 + word.Length > width) {
        wrapped.Append(line).Append('\n');
        line.Clear();
      }

      if (line.Length > 0)
        line.Append(' ');

      line.Append(word);
    }

    return wrapped.Append(line).ToString();
  }

  private static int SafeWidth() {
    try {
      return Console.WindowWidth;
    } catch (IOException) {
      // A redirected run has no window and no width. Eighty is what everything else assumes.
      return 80;
    } catch (PlatformNotSupportedException) {
      return 80;
    }
  }

}
