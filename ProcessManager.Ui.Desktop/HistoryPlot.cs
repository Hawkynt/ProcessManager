using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// A scrolling time plot of one or more series, drawn as an instrument rather than as a chart.
/// </summary>
/// <remarks>
/// NativeForms has no plotting control, so this is ours (PRD §7.2). What matters:
/// <list type="bullet">
/// <item>It reads a <see cref="HistoryRing{T}"/> of <see cref="Rate"/> directly, so nothing is copied
/// or projected per frame.</item>
/// <item>A <see cref="Rate"/> without a value breaks the line rather than being drawn as zero. A gap
/// in sampling is not a quiet second, and a plot that draws it as one is lying (PRD §3.3).</item>
/// <item>The horizontal axis is time and not sample count. When older history is compressed, the
/// cursor, grid, statistics and end label all use the same non-linear mapping as the pixels.</item>
/// </list>
/// </remarks>
public sealed class HistoryPlot : OwnerDrawnControl {

  private const int _SegmentHeight = 2;
  private const int _SegmentGap = 1;
  private const int _MeterGap = 4;
  private const int _MeterTextHeight = 16;

  private readonly List<Series> _series = [];

  /// <param name="Filled">
  /// Whether this series is drawn as an area or as a line, where it differs from the plot's own
  /// setting. Per series and not per plot, because the pair a two-line graph draws is most legible
  /// as one of each: the area is the direction that dominates and the line over it is the other,
  /// which two lines in two shades of one accent are not (PRD §48, §49).
  /// </param>
  private sealed record Series(HistoryRing<Rate> Values, Color Color, string Label, bool? Filled = null);

  public HistoryPlot() =>
    // Not OnDoubleClick: Control.DoubleClick is raised by PerformClick and never by the pointer, so
    // a handler on it is a gesture that quietly does nothing.
    this.MouseDoubleClick += (_, _) => this.Expanded?.Invoke(this, EventArgs.Empty);

  /// <summary>
  /// Focusable, unlike most owner-drawn controls, so that Tab reaches every graph and the arrow keys
  /// can walk its cursor — §45.9's requirement that keyboard navigation reach every graph control.
  /// </summary>
  protected override bool Focusable => true;

  /// <summary>Where the pointer is, in pixels from the left, or -1 for nowhere.</summary>
  private int _hoverX = -1;

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

  /// <summary>
  /// What one sample is measured in, for the readings under the cursor.
  /// </summary>
  /// <remarks>
  /// Told rather than inferred from the scale. A plot whose ceiling is not a hundred could be
  /// holding bytes, bytes a second, degrees or watts, and rendering a disk's 1.2 MB/s as "1.2M" is
  /// a small lie of exactly the kind this program exists not to tell (PRD §76).
  /// </remarks>
  public PerformanceUnit Unit { get; set; } = PerformanceUnit.Percent;

  /// <summary>
  /// How the top of the scale reads — <c>100%</c>, <c>16 GB</c> (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// In the corner because a filled area at two thirds of the height means nothing at all until the
  /// reader knows two thirds of what.
  /// </remarks>
  public string ScaleLabel { get; set; } = string.Empty;

  /// <summary>Drawn in the top-left corner, so a wall of plots is readable without a legend.</summary>
  /// <remarks>
  /// It is painted rather than written as text, so nothing announces it: a plot has no <c>Text</c>
  /// for the toolkit to fall back on, and a wall of them reads as "graph, graph, graph". Setting the
  /// caption therefore names the control too, unless a caller has already named it something better
  /// (PRD §74).
  /// </remarks>
  public string Caption {
    get;
    set {
      field = value;
      if (string.IsNullOrEmpty(this.AccessibleName))
        this.AccessibleName = value;
    }
  } = string.Empty;

  /// <summary>
  /// Whether this plot carries the segmented utilisation/capacity meter used by the system pages.
  /// </summary>
  /// <remarks>
  /// Processor plots and the bounded physical-memory/swap plots are the meters the reference UI
  /// actually pairs with histories. A disk, GPU or sensor does not acquire one merely because its
  /// scale happens to be a percentage: that would turn a visual convention into a unit heuristic.
  /// </remarks>
  public bool UsesSegmentedMeter => this.IsProcessorMeter || this.IsMemoryMeter;

  private bool IsProcessorMeter
    => this.Unit == PerformanceUnit.Percent
      && Math.Abs(this.Maximum - 100) < 0.0001
      && (string.Equals(this.Caption, "Processor", StringComparison.Ordinal)
        || this.Caption.StartsWith("Core ", StringComparison.Ordinal));

  private bool IsMemoryMeter
    => this.Maximum > 0
      && (
        this.Unit == PerformanceUnit.Bytes
          && this.Caption is "Physical memory" or "Swap"
        || this.Unit == PerformanceUnit.Percent
          && Math.Abs(this.Maximum - 100) < 0.0001
          && string.Equals(this.Caption, "Memory", StringComparison.Ordinal)
      );

  /// <summary>
  /// The recent, uncompressed span. Older retained samples may extend the visible horizon beyond it.
  /// </summary>
  public int SpanSeconds { get; set; } = 60;

  /// <summary>How far apart the samples are, which is what turns a span in seconds into a count.</summary>
  public double SecondsPerSample { get; set; } = 1;

  /// <summary>
  /// How much older history the graph asks to fit beside the recent span.
  /// </summary>
  /// <remarks>
  /// Fifteen is the useful long-history default from the reference UI. It is a request, not invented
  /// storage: <see cref="HistoryAxis"/> caps it to the backing ring's actual retained capacity.
  /// Set to one for the traditional linear axis.
  /// </remarks>
  public double HistoryMultiplier { get; set; } = HistoryAxis.DefaultMultiplier;

  /// <summary>The multiplier the current ring can actually support.</summary>
  public double EffectiveHistoryMultiplier => this.TimeAxis.Multiplier;

  /// <summary>The real left edge of the current time axis, in seconds.</summary>
  public double VisibleSpanSeconds => this.TimeAxis.OldestSampleAge * Math.Max(0.05, this.SecondsPerSample);

  /// <summary>
  /// How many of the newest samples to leave undrawn — what "paused" is made of.
  /// </summary>
  /// <remarks>
  /// Pause freezes the drawing without clearing the history or stopping collection (PRD §45.4), so
  /// it is counted in samples rather than remembered as an index: a ring that wraps while somebody
  /// is reading a paused plot would leave an index pointing at a different second than the one they
  /// paused on, and the plot would drift without ever looking wrong.
  /// </remarks>
  public int SkipNewest { get; set; }

  /// <summary>Whether to say so, which a frozen plot must, or it is simply a broken one.</summary>
  public bool Paused { get; set; }

  /// <summary>Raised on a double-click, which §45.4 makes the gesture for the inspection view.</summary>
  public event EventHandler? Expanded;

  /// <summary>
  /// Drops every series.
  /// </summary>
  /// <remarks>
  /// For a plot whose subject changes — the performance page swaps one series for another as the
  /// selection moves, rather than building a plot per resource.
  /// </remarks>
  public void ClearSeries() => this._series.Clear();

  /// <summary>
  /// The colour of each series, in the order they were added.
  /// </summary>
  /// <remarks>
  /// So a caller — and a test — can ask what a plot is actually drawing. §45.5 requires a resource's
  /// sparkline and its graph to be the same colour, and the two are worked out in different places;
  /// nothing but reading the plot back can check that they agree.
  /// </remarks>
  public IReadOnlyList<Color> SeriesColours {
    get {
      var colours = new Color[this._series.Count];
      for (var i = 0; i < colours.Length; ++i)
        colours[i] = this._series[i].Color;

      return colours;
    }
  }

  /// <param name="filled">
  /// Null follows <see cref="Filled"/>, which is what nearly every series wants; false strokes this
  /// one over whatever is under it.
  /// </param>
  public void AddSeries(HistoryRing<Rate> values, Color color, string label = "", bool? filled = null) {
    ArgumentNullException.ThrowIfNull(values);
    this._series.Add(new(values, color, label, filled));
  }

  /// <summary>How many samples the ordinary recent span is wide.</summary>
  private int Samples => Math.Max(1, (int)Math.Round(this.SpanSeconds / Math.Max(0.05, this.SecondsPerSample)));

  /// <summary>
  /// The segmented meter needs a useful bar and the history still needs enough width to be a graph.
  /// </summary>
  private bool DrawsSegmentedMeter
    => this.UsesSegmentedMeter && this._series.Count > 0 && this.Width >= 80 && this.Height >= 40;

  /// <summary>
  /// Narrow per-core cells get a narrow meter; the whole-resource plot can afford the wider one.
  /// </summary>
  private int MeterWidth => Math.Clamp(this.Width / 4, 34, 44);

  /// <summary>The rectangle in which time means horizontal distance.</summary>
  private Rectangle PlotBounds {
    get {
      if (!this.DrawsSegmentedMeter)
        return new(0, 0, this.Width, this.Height);

      var left = this.MeterWidth + _MeterGap;
      return new(left, 0, Math.Max(1, this.Width - left), this.Height);
    }
  }

  /// <summary>One mapping for painting, labels, cursor movement and statistics.</summary>
  private HistoryAxis TimeAxis {
    get {
      var retained = this._series.Count > 0 ? this._series[0].Values.Capacity : this.Samples;
      return new(Math.Max(1, this.PlotBounds.Width), this.Samples, retained, this.HistoryMultiplier);
    }
  }

  #region what the pointer is over (PRD §45.4)

  /// <summary>
  /// The timestamp and readings under the pointer, or empty when it is over nothing.
  /// </summary>
  /// <remarks>
  /// Relative rather than a wall clock: "12 s ago" is what somebody watching a scrolling graph
  /// actually wants, and a plot that has been paused for a minute would otherwise label its newest
  /// sample with a time that has since passed.
  /// </remarks>
  public string HoverText { get; private set; } = string.Empty;

  /// <summary>
  /// Raised whenever the cursor lands somewhere new, however it got there (PRD §28).
  /// </summary>
  /// <remarks>
  /// One event for the pointer and the arrow keys alike, because the page that echoes the readings
  /// into its footer must follow both. It hung off <c>MouseMove</c> at first, which left the
  /// keyboard drawing its cursor on the plot while the footer went on reporting wherever the mouse
  /// had last been — a reading beside the wrong moment, which is worse than no reading (PRD §45.9).
  /// </remarks>
  public event EventHandler? CursorMoved;

  /// <summary>
  /// Puts the cursor at a pixel from the left — what a mouse move does, without a mouse.
  /// </summary>
  /// <remarks>
  /// Public so the gesture is testable at all: <c>OnMouseMove</c> is raised by a realized canvas and
  /// nothing else, so a window-less test can otherwise say nothing about what a reader sees when
  /// they point at a graph (PRD §9.6).
  /// </remarks>
  public void PointAt(int x) {
    var was = this._hoverX;
    this._hoverX = x;
    this.UpdateHoverText();
    this.CursorMoved?.Invoke(this, EventArgs.Empty);
    if (was != this._hoverX)
      this.Invalidate();
  }

  /// <summary>
  /// Walks the cursor along the axis — what an arrow key does. Starts at the newest sample.
  /// </summary>
  public void MoveCursor(int step) {
    var plot = this.PlotBounds;
    if (plot.Width < 2)
      return;

    this.PointAt(this._hoverX < plot.Left ? plot.Right - 1 : Math.Clamp(this._hoverX + step, plot.Left, plot.Right - 1));
  }

  /// <summary>Takes the cursor off the plot, which is what leaving it does.</summary>
  public void ClearCursor() {
    if (this._hoverX < 0)
      return;

    this._hoverX = -1;
    this.HoverText = string.Empty;
    this.CursorMoved?.Invoke(this, EventArgs.Empty);
    this.Invalidate();
  }

  protected override void OnMouseMove(MouseEventArgs e) => this.PointAt(e.X);

  protected override void OnMouseLeave(EventArgs e) => this.ClearCursor();

  /// <summary>
  /// Arrow keys walk the cursor along the axis, so the tooltip is reachable without a mouse
  /// (PRD §45.9).
  /// </summary>
  protected override void OnKeyDown(KeyEventArgs e) {
    var step = e.KeyCode switch {
      Keys.Left => -1,
      Keys.Right => 1,
      _ => 0,
    };

    if (step == 0)
      return;

    this.MoveCursor(step);
    e.Handled = true;
  }

  private void UpdateHoverText() {
    var plot = this.PlotBounds;
    if (this._hoverX < plot.Left || this._hoverX >= plot.Right || this._series.Count == 0 || plot.Width < 2) {
      this.HoverText = string.Empty;
      return;
    }

    var axis = this.TimeAxis;
    var age = (int)Math.Round(axis.AgeAtDistance(plot.Right - 1 - this._hoverX));
    var text = new System.Text.StringBuilder();
    text.Append(age <= 0 ? "now" : $"{age * this.SecondsPerSample:0.#} s ago");

    foreach (var series in this._series) {
      var count = Math.Max(0, series.Values.Count - this.SkipNewest);
      var index = count - 1 - age;
      var label = series.Label.Length > 0 ? series.Label + " " : string.Empty;
      // Outside the history the plot has is not a reading of zero — it is a part of the axis this
      // machine has not been running long enough to fill.
      text.Append(index < 0 || index >= series.Values.Count
        ? $"  ·  {label}{Humanize.Placeholder(UnknownReason.NotSampledYet)}"
        : $"  ·  {label}{this.Reading(series.Values[index])}");
    }

    this.HoverText = text.ToString();
  }

  /// <summary>One sample, in the unit the series was declared in.</summary>
  private string Reading(Rate value) {
    if (!value.HasValue)
      return Humanize.Placeholder(value.Reason);

    return this.Unit switch {
      PerformanceUnit.Bytes => Humanize.Bytes(Counter.Of((ulong)Math.Max(0, value.Value))),
      PerformanceUnit.BytesPerSecond => Humanize.BytesPerSecond(value),
      PerformanceUnit.Celsius => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} °C", value.Value),
      PerformanceUnit.Watts => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} W", value.Value),
      // A count is a whole number of things and reads as one. Falling through to the percentage
      // branch below would have printed a thread count as "42 %".
      PerformanceUnit.Count => Humanize.Count(Counter.Of((ulong)Math.Max(0, value.Value))),
      _ => Humanize.Percent(value) + " %",
    };
  }

  #endregion

  /// <summary>Current, minimum, maximum and average over the drawn span (PRD §45.4).</summary>
  public string Statistics() {
    if (this._series.Count == 0)
      return string.Empty;

    var text = new System.Text.StringBuilder();
    var visibleSamples = this.TimeAxis.VisibleSamples;
    foreach (var series in this._series) {
      var count = Math.Max(0, series.Values.Count - this.SkipNewest);
      var take = Math.Min(count, visibleSamples);
      var lowest = double.PositiveInfinity;
      var highest = double.NegativeInfinity;
      var total = 0d;
      var seen = 0;
      for (var i = count - take; i < count; ++i) {
        var value = series.Values[i];
        if (!value.HasValue)
          continue;

        lowest = Math.Min(lowest, value.Value);
        highest = Math.Max(highest, value.Value);
        total += value.Value;
        ++seen;
      }

      if (text.Length > 0)
        text.Append('\n');

      text.Append(series.Label.Length > 0 ? series.Label : this.Caption).Append(": ");
      if (seen == 0) {
        text.Append(Humanize.Placeholder(UnknownReason.NotSampledYet));
        continue;
      }

      var current = series.Values[count - 1];
      text.Append("current ").Append(this.Reading(current))
        .Append(", minimum ").Append(this.Reading(Rate.Of(lowest)))
        .Append(", maximum ").Append(this.Reading(Rate.Of(highest)))
        .Append(", average ").Append(this.Reading(Rate.Of(total / seen)));
    }

    return text.ToString();
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    var theme = this.Theme;
    var bounds = new Rectangle(0, 0, this.Width, this.Height);
    var plot = this.PlotBounds;
    var axis = this.TimeAxis;

    // Black ground and a green graticule, which is what a monitor's plot has looked like since
    // before any of these tools existed. It is deliberately *not* the theme's field colour: this is
    // an instrument, and an instrument that changes contrast with the desktop is harder to read at a
    // glance than one that always looks the same.
    g.FillRectangle(RowPalette.PlotBackground, bounds);

    const int Cell = 16;
    for (var y = Cell; y < plot.Height; y += Cell)
      g.DrawLine(RowPalette.PlotGrid(theme), plot.Left, y, plot.Right, y);

    // Equal amounts of time, not equal amounts of pixels. On a compressed graph these rules crowd
    // toward the old edge, which is the visual cue that the horizontal scale is changing rather than
    // a row of evenly-spaced lines quietly pretending it is not.
    const int TimeDivisions = 12;
    for (var i = 1; i < TimeDivisions; ++i) {
      var age = axis.OldestSampleAge * i / TimeDivisions;
      var distance = axis.DistanceAtAge(age);
      var x = plot.Right - 1 - (int)Math.Round(distance);
      if (x > plot.Left && x < plot.Right)
        g.DrawLine(RowPalette.PlotGrid(theme), x, 0, x, plot.Height);
    }

    foreach (var series in this._series)
      SeriesPainter.Draw(
        g,
        plot,
        series.Values,
        this.Maximum,
        series.Color,
        this.Samples,
        this.SkipNewest,
        series.Filled ?? this.Filled,
        this.HistoryMultiplier
      );

    this.DrawSegmentedMeter(g, theme);
    this.DrawCursor(g);

    if (this.DrawsSegmentedMeter)
      g.DrawRectangle(theme.Border, new(plot.Left, 0, plot.Width - 1, plot.Height - 1));
    else
      g.DrawRectangle(theme.Border, new(0, 0, this.Width - 1, this.Height - 1));

    // The caption and the current reading sit inside the plot, top-left, the way the reference tools
    // place them — a label outside would cost a row of pixels the plot can use. A segmented meter
    // carries the current reading itself, so its adjacent history only needs to say what it is.
    var caption = this.DrawsSegmentedMeter
      ? this.Caption
      : this.Value.Length > 0 && this.Caption.Length > 0
        ? $"{this.Caption}: {this.Value}"
        : this.Caption + this.Value;

    var textBounds = new Rectangle(plot.Left + 4, 2, Math.Max(0, plot.Width - 8), 16);
    if (caption.Length > 0)
      g.DrawText(caption, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Caption), textBounds, ContentAlignment.TopLeft);

    if (this.ScaleLabel.Length > 0)
      Shadowed(g, this.ScaleLabel, theme, textBounds, ContentAlignment.TopRight);

    this.DrawAxis(g, theme);
  }

  /// <summary>
  /// Paints the compact segmented meter beside the bounded system-information histories.
  /// </summary>
  /// <remarks>
  /// Two-pixel bars and one-pixel gaps are a visual convention, not a data model. The values come
  /// from the same history rings as the graph, including <see cref="SkipNewest"/>, so pausing cannot
  /// leave the meter on "now" while its history is frozen in the past. On processor plots kernel
  /// time is a subset of total busy time and is therefore clamped to it before segment counts are
  /// calculated.
  /// </remarks>
  private void DrawSegmentedMeter(IGraphics g, ITheme theme) {
    if (!this.DrawsSegmentedMeter)
      return;

    var meterWidth = this.MeterWidth;
    var meter = new Rectangle(0, 0, meterWidth, this.Height);
    g.FillRectangle(RowPalette.PlotBackground, meter);

    var total = this.Latest(this._series[0]);
    var kernel = this.IsProcessorMeter && this._series.Count > 1 ? this.Latest(this._series[1]) : null;
    var ceiling = Math.Max(double.Epsilon, this.Maximum);
    var totalValue = Math.Clamp(total ?? 0, 0, ceiling);
    var kernelValue = Math.Clamp(kernel ?? 0, 0, totalValue);

    const int Padding = 3;
    var textHeight = this.Height >= 48 ? _MeterTextHeight : 0;
    var bar = new Rectangle(Padding, Padding, meterWidth - (Padding * 2), Math.Max(1, this.Height - textHeight - (Padding * 2)));
    var step = _SegmentHeight + _SegmentGap;
    var segmentCount = Math.Max(1, bar.Height / step);
    var lit = Math.Clamp((int)Math.Round(segmentCount * totalValue / ceiling), 0, segmentCount);
    var kernelLit = Math.Clamp((int)Math.Round(segmentCount * kernelValue / ceiling), 0, lit);
    var userColour = this._series[0].Color;
    var kernelColour = this._series.Count > 1 ? this._series[1].Color : RowPalette.CpuKernel;
    var unlitColour = RowPalette.PlotGrid(theme);

    for (var i = 0; i < segmentCount; ++i) {
      var y = bar.Bottom - ((i + 1) * step) + _SegmentGap;
      var colour = i < kernelLit ? kernelColour : i < lit ? userColour : unlitColour;
      g.FillRectangle(colour, new(bar.Left, y, bar.Width, _SegmentHeight));
    }

    if (textHeight > 0) {
      var reading = total.HasValue
        ? this.Reading(Rate.Of(totalValue))
        : Humanize.Placeholder(UnknownReason.NotSampledYet);
      g.DrawText(
        reading,
        theme.DefaultFont,
        RowPalette.PlotInk(theme, PlotInkKind.Caption),
        new(0, this.Height - textHeight, meterWidth, textHeight - 1),
        ContentAlignment.MiddleCenter
      );
    }

    g.DrawRectangle(theme.Border, new(0, 0, meterWidth - 1, this.Height - 1));
  }

  /// <summary>The newest sample the drawing is allowed to show.</summary>
  private double? Latest(Series series) {
    var index = series.Values.Count - 1 - Math.Max(0, this.SkipNewest);
    if ((uint)index >= (uint)series.Values.Count)
      return null;

    var reading = series.Values[index];
    return reading.HasValue ? reading.Value : null;
  }

  /// <summary>
  /// What the two ends of the axis are, spelled out (PRD §45.4).
  /// </summary>
  /// <remarks>
  /// Only where there is room for them. On the sixteen plots of a per-core grid the labels would be
  /// most of the picture, and the grid's axis is the same as the big plot's above it.
  /// </remarks>
  private void DrawAxis(IGraphics g, ITheme theme) {
    var plot = this.PlotBounds;
    if (plot.Height < 56 || plot.Width < 220)
      return;

    var axis = this.TimeAxis;
    var strip = new Rectangle(plot.Left + 4, this.Height - 18, plot.Width - 8, 16);
    var oldest = Ago((int)Math.Round(this.VisibleSpanSeconds));
    if (axis.IsCompressed)
      oldest += " · compressed";

    Shadowed(g, oldest, theme, strip, ContentAlignment.TopLeft);
    Shadowed(g, this.Paused ? "Paused" : "Now", theme, strip, ContentAlignment.TopRight);
  }

  /// <summary>"60 seconds ago", "15 minutes ago" — the far end of the axis, in the units it is set in.</summary>
  private static string Ago(int seconds) {
    if (seconds < 120)
      return $"{seconds} seconds ago";

    var minutes = seconds / 60;
    var remainder = seconds % 60;
    return remainder == 0 ? $"{minutes} minutes ago" : $"{minutes} min {remainder} s ago";
  }

  /// <summary>
  /// A rule down the sample the pointer is on, and its readings beside it.
  /// </summary>
  /// <remarks>
  /// The readings are drawn on the plot rather than in a floating tip: this is one control among
  /// several stacked graphs, and a popup that follows the pointer covers the neighbour a reader is
  /// comparing against.
  /// </remarks>
  private void DrawCursor(IGraphics g) {
    var plot = this.PlotBounds;
    if (this._hoverX < plot.Left || this._hoverX >= plot.Right || this.HoverText.Length == 0)
      return;

    var theme = this.Theme;
    g.DrawLine(RowPalette.PlotInk(theme, PlotInkKind.Cursor), this._hoverX, 0, this._hoverX, this.Height);
    var half = plot.Width / 2;
    var wide = this._hoverX > plot.Left + half;
    var box = new Rectangle(wide ? plot.Left + 4 : plot.Left + half, 18, Math.Max(0, half - 8), 14);
    g.DrawText(this.HoverText, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Caption), box, wide ? ContentAlignment.TopLeft : ContentAlignment.TopRight);
  }

  /// <summary>
  /// Axis text with a dark letter behind it.
  /// </summary>
  /// <remarks>
  /// The corners are exactly where a filled series ends up when a resource is busy, and a pale green
  /// "Now" on a pale purple fill is a label nobody can read at the moment they most want to — which
  /// is when the graph is full (PRD §45.9).
  /// </remarks>
  private static void Shadowed(IGraphics g, string text, ITheme theme, Rectangle bounds, ContentAlignment alignment) {
    // No shadow under a high-contrast scheme: a second colour one pixel from the first is the one
    // thing that scheme exists to stop, and a white label needs no help (PRD §45.9).
    if (RowPalette.PlotInkShadow(theme) is { } shadow)
      g.DrawText(text, theme.DefaultFont, shadow, bounds with { X = bounds.X + 1, Y = bounds.Y + 1 }, alignment);

    g.DrawText(text, theme.DefaultFont, RowPalette.PlotInk(theme, PlotInkKind.Axis), bounds, alignment);
  }

}
