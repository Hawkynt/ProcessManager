using System.Drawing;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A history plot small enough to live in a table cell (PRD §7.2).
/// </summary>
/// <remarks>
/// Filled rather than stroked, and on the dark ground the reference tools use: at forty pixels wide
/// and sixteen tall a line is one pixel of signal in a field of background, where an area reads as a
/// shape. A gap in the series breaks the fill rather than being drawn as zero, which is the same rule
/// the full-size plot follows (PRD §3.3).
/// </remarks>
internal static class Sparkline {

  public static void Draw(IGraphics g, Rectangle bounds, HistoryRing<Rate>? history, double scale, Color color) {
    var plot = new Rectangle(bounds.X + 2, bounds.Y + 2, Math.Max(4, bounds.Width - 4), Math.Max(4, bounds.Height - 4));
    g.FillRectangle(RowPalette.PlotBackground, plot);

    // One horizontal rule at the half, so a reader can tell a busy plot from a full one.
    g.DrawLine(RowPalette.PlotGrid, plot.Left, plot.Top + plot.Height / 2, plot.Right - 1, plot.Top + plot.Height / 2);

    if (history is null || history.Count == 0 || scale <= 0)
      return;

    var visible = Math.Min(history.Count, plot.Width);
    var first = history.Count - visible;
    for (var i = 0; i < visible; ++i) {
      var value = history[first + i];
      if (!value.HasValue)
        continue;

      var fraction = Math.Clamp(value.Value / scale, 0, 1);
      var height = (int)Math.Round(fraction * plot.Height);
      if (height <= 0)
        continue;

      // Newest at the right, one pixel column per sample — the same direction as every other plot in
      // the program, so "now" is always in the same place.
      var x = plot.Right - visible + i;
      g.FillRectangle(color, new(x, plot.Bottom - height, 1, height));
    }
  }

}
