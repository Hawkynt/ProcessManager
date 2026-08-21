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

  /// <param name="samples">
  /// How many samples wide the axis is. The rail's rows are given the same span as the main graph,
  /// which is what §45.1 asks for: a row's sparkline is "over the same history the main graph uses",
  /// and a sparkline drawing a pixel per sample would show four minutes beside a graph showing one.
  /// </param>
  public static void Draw(
    IGraphics g,
    Rectangle bounds,
    HistoryRing<Rate>? history,
    double scale,
    Color color,
    int samples = 60,
    int skipNewest = 0
  ) {
    var plot = new Rectangle(bounds.X + 2, bounds.Y + 2, Math.Max(4, bounds.Width - 4), Math.Max(4, bounds.Height - 4));
    g.FillRectangle(RowPalette.PlotBackground, plot);

    // One horizontal rule at the half, so a reader can tell a busy plot from a full one.
    g.DrawLine(RowPalette.PlotGrid, plot.Left, plot.Top + plot.Height / 2, plot.Right - 1, plot.Top + plot.Height / 2);

    if (history is null || history.Count == 0 || scale <= 0)
      return;

    SeriesPainter.Draw(g, plot, history, scale, color, samples, skipNewest);
  }

}
