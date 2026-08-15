using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A scrolling time plot of one or more series, drawn in the platform's own theme colours.
/// </summary>
/// <remarks>
/// NativeForms has no plotting control, so this is ours (PRD §7.2). Two properties matter:
/// <list type="bullet">
/// <item>It reads a <see cref="HistoryRing{T}"/> of <see cref="Rate"/> directly, so nothing is copied
/// or projected per frame.</item>
/// <item>A <see cref="Rate"/> without a value breaks the line rather than being drawn as zero. A gap
/// in sampling is not a quiet second, and a plot that draws it as one is lying (PRD §3.3).</item>
/// </list>
/// </remarks>
public sealed class HistoryPlot : OwnerDrawnControl {

  private readonly List<Series> _series = [];

  private sealed record Series(HistoryRing<Rate> Values, Color Color, string Label);

  /// <summary>The top of the scale. 100 for percentages; set higher for rates.</summary>
  public double Maximum { get; set; } = 100;

  /// <summary>Drawn in the top-left corner, so a wall of plots is readable without a legend.</summary>
  public string Caption { get; set; } = string.Empty;

  public void AddSeries(HistoryRing<Rate> values, Color color, string label = "") {
    ArgumentNullException.ThrowIfNull(values);
    this._series.Add(new(values, color, label));
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    var bounds = new Rectangle(0, 0, this.Width, this.Height);

    g.FillRectangle(theme.FieldBackground, bounds);

    // Gridlines at quarters. Four is enough to read a value off; more turns the plot into a grid
    // with a line in it.
    for (var i = 1; i < 4; ++i) {
      var y = this.Height * i / 4;
      g.DrawLine(theme.GridLine, 0, y, this.Width, y);
    }

    foreach (var series in this._series)
      this.DrawSeries(g, series);

    g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));

    if (this.Caption.Length > 0)
      g.DrawText(this.Caption, theme.DefaultFont, theme.DisabledText, new(3, 1, this.Width - 6, 14), ContentAlignment.TopLeft);
  }

  private void DrawSeries(IGraphics g, Series series) {
    var count = series.Values.Count;
    if (count < 2)
      return;

    // Newest on the right, one pixel column per sample, oldest scrolled off the left — which is what
    // makes a plot readable without an axis: "now" is always in the same place.
    var visible = Math.Min(count, this.Width);
    var first = count - visible;
    var previousX = 0;
    var previousY = 0;
    var havePrevious = false;

    for (var i = 0; i < visible; ++i) {
      var value = series.Values[first + i];
      var x = this.Width - visible + i;
      if (!value.HasValue) {
        havePrevious = false;
        continue;
      }

      var scaled = Math.Clamp(value.Value / this.Maximum, 0, 1);
      var y = this.Height - 1 - (int)(scaled * (this.Height - 2));
      if (havePrevious)
        g.DrawLine(series.Color, previousX, previousY, x, y);
      else
        // A single point after a gap still has to be visible, or a one-sample island disappears.
        g.DrawLine(series.Color, x, y, x, y);

      previousX = x;
      previousY = y;
      havePrevious = true;
    }
  }

}
