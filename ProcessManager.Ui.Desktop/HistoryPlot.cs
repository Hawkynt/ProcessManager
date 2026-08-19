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

  /// <summary>
  /// Fill the area under each series instead of stroking its outline.
  /// </summary>
  /// <remarks>
  /// On by default because it is what the reference tools do and it is easier to read: at a hundred
  /// pixels tall a filled area shows its shape at a glance where a one-pixel line has to be traced.
  /// A stroked plot is still the better choice when several series overlap heavily, which is why
  /// this is a property and not a decision.
  /// </remarks>
  public bool Filled { get; set; } = true;

  /// <summary>The reading to print in the corner, over the plot. Empty for none.</summary>
  public string Value { get; set; } = string.Empty;

  /// <summary>The top of the scale. 100 for percentages; set higher for rates.</summary>
  public double Maximum { get; set; } = 100;

  /// <summary>Drawn in the top-left corner, so a wall of plots is readable without a legend.</summary>
  public string Caption { get; set; } = string.Empty;

  /// <summary>
  /// Drops every series.
  /// </summary>
  /// <remarks>
  /// For a plot whose subject changes — the performance page swaps one series for another as the
  /// selection moves, rather than building a plot per resource.
  /// </remarks>
  public void ClearSeries() => this._series.Clear();

  public void AddSeries(HistoryRing<Rate> values, Color color, string label = "") {
    ArgumentNullException.ThrowIfNull(values);
    this._series.Add(new(values, color, label));
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    var bounds = new Rectangle(0, 0, this.Width, this.Height);

    // Black ground and a green graticule, which is what a monitor's plot has looked like since
    // before any of these tools existed. It is deliberately *not* the theme's field colour: this is
    // an instrument, and an instrument that changes contrast with the desktop is harder to read at a
    // glance than one that always looks the same.
    g.FillRectangle(RowPalette.PlotBackground, bounds);

    // A graticule rather than four rules: the vertical lines give the eye something to measure
    // horizontal movement against, which is most of what a scrolling plot is for.
    const int Cell = 16;
    for (var y = Cell; y < this.Height; y += Cell)
      g.DrawLine(RowPalette.PlotGrid, 0, y, this.Width, y);

    for (var x = this.Width % Cell; x < this.Width; x += Cell)
      g.DrawLine(RowPalette.PlotGrid, x, 0, x, this.Height);

    foreach (var series in this._series)
      this.DrawSeries(g, series);

    g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));

    // The caption and the current reading sit inside the plot, top-left, the way the reference tools
    // place them — a label outside would cost a row of pixels the plot can use.
    var caption = this.Value.Length > 0 && this.Caption.Length > 0
      ? $"{this.Caption}: {this.Value}"
      : this.Caption + this.Value;

    if (caption.Length > 0)
      g.DrawText(caption, theme.DefaultFont, _CaptionColor, new(4, 2, this.Width - 8, 16), ContentAlignment.TopLeft);
  }

  private static readonly Color _CaptionColor = Color.FromArgb(0xFF, 0x9C, 0xE8, 0x9C);

  private void DrawSeries(IGraphics g, Series series) {
    var count = series.Values.Count;
    if (count < 1)
      return;

    // Newest on the right, one pixel column per sample, oldest scrolled off the left — which is what
    // makes a plot readable without an axis: "now" is always in the same place.
    var visible = Math.Min(count, this.Width);
    var first = count - visible;
    var previousX = 0;
    var previousY = 0;
    var havePrevious = false;
    var edge = Lighten(series.Color);

    for (var i = 0; i < visible; ++i) {
      var value = series.Values[first + i];
      var x = this.Width - visible + i;
      if (!value.HasValue) {
        // A gap breaks the fill as well as the line. Filling through it would draw a second of
        // activity that was never measured (PRD §3.3).
        havePrevious = false;
        continue;
      }

      var scaled = Math.Clamp(value.Value / this.Maximum, 0, 1);
      var y = this.Height - 1 - (int)(scaled * (this.Height - 2));

      if (this.Filled) {
        var height = this.Height - y;
        if (height > 0)
          g.FillRectangle(series.Color, new(x, y, 1, height));

        // A brighter line along the top, so two filled series stacked on each other still show where
        // one ends.
        g.FillRectangle(edge, new(x, y, 1, 1));
      } else if (havePrevious)
        g.DrawLine(series.Color, previousX, previousY, x, y);
      else
        // A single point after a gap still has to be visible, or a one-sample island disappears.
        g.DrawLine(series.Color, x, y, x, y);

      previousX = x;
      previousY = y;
      havePrevious = true;
    }
  }

  private static Color Lighten(Color color) => Color.FromArgb(
    color.A,
    Math.Min(255, color.R + 70),
    Math.Min(255, color.G + 70),
    Math.Min(255, color.B + 70)
  );

}
