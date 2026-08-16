using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Turns a captured terminal frame into an SVG picture of itself.
/// </summary>
/// <remarks>
/// <para>
/// SVG rather than PNG, and deliberately: it needs no font data, no image library and no tool on the
/// machine — the terminal frame is already a grid of characters, and that is exactly what SVG text
/// is good at. It also diffs: a screenshot that changes shows *which line* changed in the pull
/// request rather than "binary files differ", which is the whole reason to check a screenshot in.
/// </para>
/// <para>
/// The colours are the terminal's own sixteen, resolved from the attribute the renderer assigned, so
/// the picture shows what the program actually paints rather than a monochrome transcript.
/// </para>
/// </remarks>
internal static class FrameSvg {

  private const int _CellWidth = 8;
  private const int _CellHeight = 17;
  private const int _Margin = 10;

  public static string Render(string frame, string title, byte[]? attributes = null, int width = 0) {
    ArgumentNullException.ThrowIfNull(frame);

    var lines = frame.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    var columns = 0;
    foreach (var line in lines)
      columns = Math.Max(columns, line.Length);

    var screenWidth = width > 0 ? width : columns;
    var svgWidth = columns * _CellWidth + _Margin * 2;
    var height = lines.Length * _CellHeight + _Margin * 2 + 4;

    var svg = new StringBuilder(frame.Length * 3);
    svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{svgWidth}\" height=\"{height}\" viewBox=\"0 0 {svgWidth} {height}\" role=\"img\" aria-label=\"{Escape(title)}\">\n");
    svg.Append(CultureInfo.InvariantCulture, $"  <title>{Escape(title)}</title>\n");
    svg.Append(CultureInfo.InvariantCulture, $"  <rect width=\"{svgWidth}\" height=\"{height}\" rx=\"6\" fill=\"#0c0c0c\"/>\n");

    // One <text> per line rather than per character: a per-character element would be a hundred
    // thousand nodes for a full screen, and xml:space plus a monospace family keeps the columns
    // lined up without them.
    svg.Append("  <g font-family=\"ui-monospace,SFMono-Regular,Menlo,DejaVu Sans Mono,Consolas,monospace\" font-size=\"13\" xml:space=\"preserve\">\n");

    for (var i = 0; i < lines.Length; ++i) {
      var y = _Margin + (i + 1) * _CellHeight - 4;
      var line = lines[i];
      if (line.Trim().Length == 0)
        continue;

      // The real attributes when the caller captured them, so the picture shows what the program
      // paints rather than a guess about which rows are emphasised.
      AppendLine(svg, line, i, y, width, attributes, screenWidth);
    }

    svg.Append("  </g>\n</svg>\n");
    return svg.ToString();
  }

  /// <summary>
  /// One line, split into runs of equal attribute so each gets its own colour.
  /// </summary>
  private static void AppendLine(
    StringBuilder svg,
    string line,
    int row,
    int y,
    int svgWidth,
    byte[]? attributes,
    int screenWidth
  ) {
    if (attributes is null || screenWidth <= 0) {
      svg.Append(CultureInfo.InvariantCulture, $"    <text x=\"{_Margin}\" y=\"{y}\" fill=\"#d8d8d8\">{Escape(line)}</text>\n");
      return;
    }

    var start = 0;
    while (start < line.Length) {
      var attribute = AttributeAt(attributes, screenWidth, row, start);
      var end = start + 1;
      while (end < line.Length && AttributeAt(attributes, screenWidth, row, end) == attribute)
        ++end;

      var run = line[start..end];
      var (fore, back) = Palette(attribute);
      if (back is not null)
        svg.Append(CultureInfo.InvariantCulture,
          $"    <rect x=\"{_Margin + start * _CellWidth}\" y=\"{y - _CellHeight + 4}\" width=\"{run.Length * _CellWidth}\" height=\"{_CellHeight}\" fill=\"{back}\"/>\n");

      svg.Append(CultureInfo.InvariantCulture,
        $"    <text x=\"{_Margin + start * _CellWidth}\" y=\"{y}\" fill=\"{fore}\">{Escape(run)}</text>\n");

      start = end;
    }
  }

  private static byte AttributeAt(byte[] attributes, int width, int row, int column) {
    var index = row * width + column;
    return (uint)index < (uint)attributes.Length ? attributes[index] : (byte)0;
  }

  /// <summary>
  /// The renderer's attribute byte as CSS colours. Mirrors <c>Attributes.ToAnsi</c> — the same seven
  /// meanings, in the same order, so a change to one is visible as a difference against the other.
  /// </summary>
  private static (string Fore, string? Back) Palette(byte attribute) {
    if ((attribute & 8) != 0)
      return ("#3fbf3f", null);                            // a process that just started
    if ((attribute & 16) != 0)
      return ("#d04040", null);                            // one that just ended

    return (attribute & 0x0F) switch {
      1 => ("#8a8a8a", null),                              // dim
      2 => ("#2aa198", null),                              // accent
      3 => ("#3fbf3f", null),                              // good
      4 => ("#c8a02c", null),                              // warn
      5 => ("#d04040", null),                              // bad
      6 => ("#0c2b2b", "#2aa198"),                         // header band
      7 => ("#0c0c0c", "#d8d8d8"),                         // selected row
      _ => ("#d8d8d8", null),
    };
  }

  private static string Escape(string text) {
    var builder = new StringBuilder(text.Length + 16);
    foreach (var c in text)
      switch (c) {
        case '&': builder.Append("&amp;"); break;
        case '<': builder.Append("&lt;"); break;
        case '>': builder.Append("&gt;"); break;
        case '"': builder.Append("&quot;"); break;
        default: builder.Append(c); break;
      }

    return builder.ToString();
  }

}
