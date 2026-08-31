using System.Drawing;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Draws one series across a rectangle, against a time axis rather than against a pixel count
/// (PRD §45.4).
/// </summary>
/// <remarks>
/// <para>
/// Shared by the large plots and by the rail's sparklines, because §45.1 requires a row's sparkline
/// to show "the same history the main graph uses" — and it cannot, if one of them draws a pixel per
/// sample and the other draws a minute. A 230-pixel rail row and an 880-pixel graph hold very
/// different numbers of pixels and exactly the same number of seconds.
/// </para>
/// <para>
/// The axis is the span, not the data. The newest part keeps the ordinary linear scale; older time
/// may be compressed by <see cref="HistoryAxis"/>, but only as far as the backing ring can retain.
/// That keeps recent motion readable without making a long horizon cost one pixel per raw sample.
/// </para>
/// </remarks>
internal static class SeriesPainter {

  /// <summary>
  /// Draws <paramref name="values"/> into <paramref name="plot"/>.
  /// </summary>
  /// <param name="samples">How many samples the uncompressed span contains.</param>
  /// <param name="skipNewest">
  /// How many of the newest samples to ignore, which is what a paused plot is: collection carries
  /// on, and the drawing stays where it was (PRD §45.4).
  /// </param>
  /// <param name="filled">Fill under the trace rather than stroking its outline.</param>
  /// <param name="historyMultiplier">
  /// Requested old-history horizon. The axis caps it to the number of samples the ring can retain.
  /// </param>
  public static void Draw(
    IGraphics g,
    Rectangle plot,
    HistoryRing<Rate> values,
    double maximum,
    Color color,
    int samples,
    int skipNewest = 0,
    bool filled = true,
    double historyMultiplier = HistoryAxis.DefaultMultiplier
  ) {
    if (plot.Width < 2 || plot.Height < 2 || maximum <= 0 || samples < 1)
      return;

    var count = Math.Max(0, values.Count - Math.Max(0, skipNewest));
    if (count == 0)
      return;

    var axis = new HistoryAxis(plot.Width, samples, values.Capacity, historyMultiplier);
    var edge = Lighten(color);
    var previousX = 0;
    var previousY = 0;
    var havePrevious = false;

    for (var x = plot.Right - 1; x >= plot.Left; --x) {
      var distance = plot.Right - 1 - x;
      var (youngest, oldest) = axis.AgesForPixel(distance);
      if (youngest >= count)
        break;

      // Once a pixel represents more than one sample, retain the peak rather than average it away.
      // A compressed history exists precisely so an old one-second spike remains findable.
      var reading = oldest - youngest <= 1d
        ? Interpolated(values, count, youngest)
        : Peak(values, count, youngest, oldest);

      if (!reading.HasValue) {
        // A gap breaks the fill as well as the line. Filling through it would draw activity that was
        // never measured (PRD §3.3).
        havePrevious = false;
        continue;
      }

      var scaled = Math.Clamp(reading.Value / maximum, 0, 1);
      var y = plot.Bottom - 1 - (int)(scaled * (plot.Height - 2));

      if (filled) {
        var height = plot.Bottom - y;
        if (height > 0)
          g.FillRectangle(color, new(x, y, 1, height));

        // A brighter line along the top, so two filled series stacked on each other still show
        // where one ends.
        g.FillRectangle(edge, new(x, y, 1, 1));
      } else if (havePrevious)
        g.DrawLine(color, previousX, previousY, x, y);
      else
        // A single point after a gap still has to be visible, or a one-sample island disappears.
        g.DrawLine(color, x, y, x, y);

      previousX = x;
      previousY = y;
      havePrevious = true;
    }
  }

  /// <summary>
  /// The reading a fractional age falls on, between the two samples either side of it.
  /// </summary>
  /// <remarks>
  /// For the newest part, where one pixel still represents no more than one sample. Drawing each
  /// sample as a block of its own would turn a thirty-second graph on an 880-pixel page into
  /// twenty-nine-pixel stairs, which reads as data that arrived in bursts.
  /// </remarks>
  private static Rate Interpolated(HistoryRing<Rate> values, int count, double age) {
    var whole = (int)age;
    var fraction = age - whole;
    var newer = values[count - 1 - whole];

    // Landing exactly on a sample is the sample, gap or not — a column that sits on a real reading
    // must draw it rather than being dropped for the company it keeps.
    if (fraction <= 0 || whole + 1 >= count)
      return newer;

    var older = values[count - 2 - whole];
    if (!newer.HasValue || !older.HasValue)
      // Either end missing makes the span between them unmeasured. Interpolating across a gap would
      // invent the very samples the gap says are absent.
      return Rate.Gap;

    return Rate.Of((newer.Value * (1 - fraction)) + (older.Value * fraction));
  }

  /// <summary>
  /// The largest reading in the range one pixel covers, for a span longer than the plot is wide.
  /// </summary>
  /// <remarks>
  /// Peaks rather than averages or nearest: a one-second spike is exactly what somebody scrolling
  /// back through compressed history is looking for. Averaging is what makes a long window flat and
  /// useless.
  /// </remarks>
  private static Rate Peak(HistoryRing<Rate> values, int count, double youngest, double oldest) {
    var first = Math.Max(0, count - 1 - (int)oldest);
    var last = Math.Min(count - 1, count - 1 - (int)youngest);
    var best = double.NegativeInfinity;
    var reason = UnknownReason.NotSampledYet;

    for (var i = first; i <= last; ++i) {
      var value = values[i];
      if (!value.HasValue) {
        reason = value.Reason;
        continue;
      }

      best = Math.Max(best, value.Value);
    }

    return double.IsNegativeInfinity(best) ? Rate.Unknown(reason) : Rate.Of(best);
  }

  /// <summary>
  /// The same colour, paler.
  /// </summary>
  /// <remarks>
  /// Used twice: for the bright line along the top of a filled series, so two stacked fills still
  /// show where one ends, and for the second line of a two-line plot — a disk's writes against its
  /// reads. Toward white rather than toward another hue, because both are the same resource and
  /// §45.5 gives a resource one colour.
  /// </remarks>
  public static Color Lighten(Color color, int amount = 70) => Color.FromArgb(
    color.A,
    Math.Min(255, color.R + amount),
    Math.Min(255, color.G + amount),
    Math.Min(255, color.B + amount)
  );

}
