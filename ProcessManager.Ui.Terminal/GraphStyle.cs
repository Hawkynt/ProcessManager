using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// A history plot drawn with the braille patterns (PRD §57.4).
/// </summary>
/// <remarks>
/// A braille cell is a 2×4 grid of dots, so it trades half the vertical resolution of the eighth
/// blocks for twice the horizontal: a twelve-character column holds twenty-four samples instead of
/// twelve, at four levels instead of eight. That is the right trade for a column whose question is
/// "when did this start" rather than "exactly how high did it get" — and the wrong one for a terminal
/// whose font has no braille, which is why it is a choice and not the default.
/// </remarks>
public static class BrailleSparkline {

  /// <summary>Bottom-up dot masks for the left and right halves of a cell.</summary>
  private static ReadOnlySpan<byte> LeftDots => [0x40, 0x04, 0x02, 0x01];

  private static ReadOnlySpan<byte> RightDots => [0x80, 0x20, 0x10, 0x08];

  private const char _Base = '⠀';

  /// <summary>Renders the newest samples into <paramref name="destination"/>, newest at the right.</summary>
  public static void Render(Span<char> destination, HistoryRing<Rate>? history, double scale) {
    destination.Fill(_Base);
    if (history is null || history.Count == 0 || scale <= 0 || destination.IsEmpty)
      return;

    var slots = destination.Length * 2;
    var visible = Math.Min(history.Count, slots);
    var first = history.Count - visible;
    var offset = slots - visible;

    for (var i = 0; i < visible; ++i) {
      var value = history[first + i];
      if (!value.HasValue)
        // A gap stays an empty cell: a dot at the bottom would read as "idle", which is a
        // measurement, and this is the absence of one (PRD §72.3).
        continue;

      var slot = offset + i;
      var fraction = Math.Clamp(value.Value / scale, 0, 1);
      var level = fraction <= 0 ? 0 : Math.Max(1, (int)Math.Round(fraction * 4));
      if (level == 0)
        continue;

      var dots = (slot & 1) == 0 ? LeftDots : RightDots;
      var mask = 0;
      for (var d = 0; d < level && d < dots.Length; ++d)
        mask |= dots[d];

      var cell = slot / 2;
      destination[cell] = (char)(destination[cell] | mask);
    }
  }

  public static string Render(int width, HistoryRing<Rate>? history, double scale) {
    if (width <= 0)
      return string.Empty;

    Span<char> buffer = width <= 128 ? stackalloc char[width] : new char[width];
    Render(buffer, history, scale);
    return new(buffer);
  }

}

/// <summary>
/// What a history plot would have shown, as figures (PRD §57.4).
/// </summary>
/// <remarks>
/// Required rather than decorative: a plot is the one thing in this program that a screen reader,
/// a monochrome terminal and a copied cell all lose completely, so the same series has to be
/// readable as four numbers. They are also what the lower pane shows for the selected process, where
/// there is room to write them out.
/// </remarks>
public static class HistorySummary {

  /// <summary>Minimum, mean, maximum and newest of a series, skipping the samples that are gaps.</summary>
  public static bool TryMeasure(HistoryRing<Rate>? history, out double minimum, out double average, out double maximum, out double current) {
    minimum = average = maximum = current = 0;
    if (history is null || history.Count == 0)
      return false;

    var seen = 0;
    var total = 0d;
    var hasCurrent = false;
    for (var i = 0; i < history.Count; ++i) {
      var value = history[i];
      if (!value.HasValue)
        continue;

      if (seen == 0)
        minimum = maximum = value.Value;
      else {
        minimum = Math.Min(minimum, value.Value);
        maximum = Math.Max(maximum, value.Value);
      }

      total += value.Value;
      ++seen;
      current = value.Value;
      hasCurrent = true;
    }

    if (seen == 0 || !hasCurrent)
      return false;

    average = total / seen;
    return true;
  }

  /// <summary>The four figures written out, for a pane that has room for a sentence.</summary>
  public static string Describe(HistoryRing<Rate>? history, HistorySeries series) {
    if (!TryMeasure(history, out var minimum, out var average, out var maximum, out var current))
      return "no samples yet";

    return $"min {Format(minimum, series)}  avg {Format(average, series)}  max {Format(maximum, series)}  now {Format(current, series)}";
  }

  /// <summary>
  /// The same figures squeezed into a column: current, mean and peak, in that order of usefulness.
  /// </summary>
  /// <remarks>
  /// Current first because it is what the eye goes to, and the peak last because it is the one that
  /// survives being clipped in a narrow column — a column too small for all three shows the current
  /// value alone rather than three unreadable fragments.
  /// </remarks>
  public static string Compact(HistoryRing<Rate>? history, HistorySeries series, int width) {
    if (width <= 0)
      return string.Empty;

    if (!TryMeasure(history, out _, out var average, out var maximum, out var current))
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    var now = Format(current, series);
    if (width < now.Length + 6)
      return now;

    var full = $"{now} ~{Format(average, series)} ^{Format(maximum, series)}";
    return full.Length <= width ? full : $"{now} ^{Format(maximum, series)}";
  }

  private static string Format(double value, HistorySeries series) => series switch {
    // The memory and I/O series are byte figures; the CPU one is a percentage of the machine.
    HistorySeries.Cpu => value.ToString(value >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + "%",
    _ => Humanize.Bytes(Counter.Of((ulong)Math.Max(0, value))),
  };

}
