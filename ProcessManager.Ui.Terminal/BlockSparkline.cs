using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// A history plot drawn in one row of text, using the eighth-block characters.
/// </summary>
/// <remarks>
/// <para>
/// U+2581 LOWER ONE EIGHTH BLOCK through U+2588 FULL BLOCK give a cell eight vertical steps, so a
/// twelve-character column is a twelve-sample plot with eight levels — coarse next to the window's
/// pixel-per-sample version, and enough to tell a process that is busy now from one that was busy a
/// moment ago, which is the whole question a sparkline answers.
/// </para>
/// <para>
/// They are not available everywhere. A terminal whose encoding is not UTF-8, or whose font has no
/// block glyphs, renders them as replacement boxes — which is worse than no plot at all — so
/// <see cref="Ascii"/> falls back to a coarser ramp that any terminal since 1970 can draw. Which one
/// is used is decided from the environment, not guessed (PRD §11).
/// </para>
/// </remarks>
public static class BlockSparkline {

  /// <summary>Index 0 is "nothing", 1..8 are the eighth-blocks.</summary>
  private static ReadOnlySpan<char> Blocks => [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

  /// <summary>The fallback ramp: five levels an ASCII-only terminal can definitely draw.</summary>
  private static ReadOnlySpan<char> Ascii => [' ', '.', ':', '-', '=', '+', '*', '#', '#'];

  /// <summary>Whether the terminal can be trusted with the block characters.</summary>
  /// <remarks>
  /// Both halves have to hold: the locale has to say UTF-8, and the terminal has to be something
  /// other than the one that promises nothing. Guessing wrong costs a column of replacement boxes on
  /// every row, so the test is deliberately conservative.
  /// </remarks>
  public static bool TerminalHasBlocks {
    get {
      var term = Environment.GetEnvironmentVariable("TERM");
      if (term is null or "dumb")
        return false;

      foreach (var name in (ReadOnlySpan<string>)["LC_ALL", "LC_CTYPE", "LANG"]) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
          continue;

        return value.Contains("UTF-8", StringComparison.OrdinalIgnoreCase)
            || value.Contains("UTF8", StringComparison.OrdinalIgnoreCase);
      }

      return false;
    }
  }

  /// <summary>
  /// Renders the newest <paramref name="width"/> samples into <paramref name="destination"/>.
  /// </summary>
  /// <param name="destination">Receives exactly <c>destination.Length</c> characters.</param>
  /// <param name="history">The series, or null when nothing is tracked for this row.</param>
  /// <param name="scale">The value that fills a cell. Percentages pass 100.</param>
  /// <param name="unicode">Whether to use the block characters or the ASCII ramp.</param>
  public static void Render(Span<char> destination, HistoryRing<Rate>? history, double scale, bool unicode) {
    var ramp = unicode ? Blocks : Ascii;
    destination.Fill(' ');
    if (history is null || history.Count == 0 || scale <= 0 || destination.IsEmpty)
      return;

    var visible = Math.Min(history.Count, destination.Length);
    var first = history.Count - visible;
    // Newest at the right, so "now" is in the same place as it is in every other plot here.
    var offset = destination.Length - visible;

    for (var i = 0; i < visible; ++i) {
      var value = history[first + i];
      if (!value.HasValue) {
        // A gap is a gap: a space, not a zero-height block that reads as "idle".
        destination[offset + i] = ' ';
        continue;
      }

      var fraction = Math.Clamp(value.Value / scale, 0, 1);
      // Anything above zero gets at least the smallest mark, or a process using half a percent looks
      // exactly like one using none.
      var level = fraction <= 0 ? 0 : Math.Max(1, (int)Math.Round(fraction * (ramp.Length - 1)));
      destination[offset + i] = ramp[level];
    }
  }

  /// <summary>Convenience for callers that want a string.</summary>
  public static string Render(int width, HistoryRing<Rate>? history, double scale, bool unicode) {
    if (width <= 0)
      return string.Empty;

    Span<char> buffer = width <= 128 ? stackalloc char[width] : new char[width];
    Render(buffer, history, scale, unicode);
    return new(buffer);
  }

}
