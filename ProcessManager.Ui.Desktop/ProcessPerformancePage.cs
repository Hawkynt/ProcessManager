using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One process's history, drawn (PRD §28).
/// </summary>
/// <remarks>
/// <para>
/// Rings of its own rather than the table's. <see cref="ProcessHistory"/> keeps sixty-four samples
/// for whichever rows are on screen, which is what a forty-pixel sparkline needs and an hour of
/// history is not; this window is pinned to one process, so a full ring costs one process's worth of
/// numbers and buys the longer windows §28 asks for.
/// </para>
/// <para>
/// The retained limit is on the page in words. An hour of axis over ten minutes of history would
/// otherwise draw as a graph that is mostly empty, and nothing on it would say whether that means
/// "idle" or "this program has not been running that long" (PRD §72.3).
/// </para>
/// </remarks>
internal sealed class ProcessPerformancePage {

  /// <summary>An hour at a one-second interval, which is the longest window §28 names.</summary>
  private const int _Capacity = 3600;

  private const int _Gap = 8;
  private const int _StripHeight = 32;
  private const int _FooterHeight = 38;

  private readonly Panel _panel = new();
  private readonly Panel _strip = new();
  private readonly Label _footer = new();
  private readonly List<Button> _spans = [];
  private readonly List<HistoryPlot> _plots = [];

  private readonly HistoryRing<Rate> _cpu = new(_Capacity);
  private readonly HistoryRing<Rate> _private = new(_Capacity);
  private readonly HistoryRing<Rate> _workingSet = new(_Capacity);
  private readonly HistoryRing<Rate> _read = new(_Capacity);
  private readonly HistoryRing<Rate> _write = new(_Capacity);
  private readonly HistoryRing<Rate> _gpu = new(_Capacity);
  private readonly HistoryRing<Rate> _handles = new(_Capacity);
  private readonly HistoryRing<Rate> _threads = new(_Capacity);

  private readonly HistoryPlot _cpuPlot = new() { Caption = "CPU", Unit = PerformanceUnit.Percent, Maximum = 100 };
  private readonly HistoryPlot _memoryPlot = new() { Caption = "Memory", Unit = PerformanceUnit.Bytes, Filled = false };
  private readonly HistoryPlot _ioPlot = new() { Caption = "Disk", Unit = PerformanceUnit.BytesPerSecond, Filled = false };
  // Fixed at the whole adapter, and the label says so. A GPU percentage is already a share of the
  // whole device, so unlike the CPU's it has nowhere above a hundred to go — and a graph whose
  // ceiling is unlabelled is a filled area at two thirds of the height meaning two thirds of nothing.
  private readonly HistoryPlot _gpuPlot = new() { Caption = "GPU", Unit = PerformanceUnit.Percent, Maximum = 100, ScaleLabel = "100 %" };
  private readonly HistoryPlot _handlePlot = new() { Caption = "Descriptors", Unit = PerformanceUnit.Count };
  private readonly HistoryPlot _threadPlot = new() { Caption = "Threads", Unit = PerformanceUnit.Count };

  // A hundred per cent floor: one core held is the reading everybody is looking for, and an axis
  // that scaled to a process using two per cent would draw that as a full graph.
  private Scale _cpuScale = Scale.OfCount(100);
  private Scale _memoryScale = Scale.OfBytes(16 * 1024 * 1024);
  private Scale _ioScale = Scale.OfBytes(64 * 1024);
  private Scale _handleScale = Scale.OfCount(16);
  private Scale _threadScale = Scale.OfCount(8);
  private double _secondsPerSample = 1;

  /// <summary>
  /// The size the plots were last tiled for.
  /// </summary>
  /// <remarks>
  /// A control outside the toolkit's own assembly cannot observe its own resize —
  /// <c>Control.OnBoundsChanged</c> is <c>private protected</c> and there is no public event — so the
  /// layout is re-run from the owner's tick and this is what keeps that from being arithmetic every
  /// second for a page whose size has not changed.
  /// </remarks>
  private System.Drawing.Size _laidOutFor = new(-1, -1);

  public ProcessPerformancePage() {
    this._panel.Dock = DockStyle.Fill;

    this._cpuPlot.AddSeries(this._cpu, RowPalette.Cpu, "CPU");
    this._memoryPlot.AddSeries(this._private, RowPalette.Memory, "private");
    // Two series on one plot rather than two plots: the pair is the story — a resident set far below
    // the commit charge is a process that has been paged out, and that is only visible side by side.
    this._memoryPlot.AddSeries(this._workingSet, RowPalette.Cpu, "working set");
    this._ioPlot.AddSeries(this._read, RowPalette.Io, "read");
    this._ioPlot.AddSeries(this._write, RowPalette.CpuKernel, "write");
    this._gpuPlot.AddSeries(this._gpu, RowPalette.Memory, "GPU");
    this._handlePlot.AddSeries(this._handles, RowPalette.Io, "descriptors");
    this._threadPlot.AddSeries(this._threads, RowPalette.Cpu, "threads");

    this._plots.AddRange([this._cpuPlot, this._memoryPlot, this._ioPlot, this._gpuPlot, this._handlePlot, this._threadPlot]);
    foreach (var plot in this._plots) {
      // Pointing at one plot writes its readings into the footer as well as onto the plot itself, so
      // that the gesture is discoverable: a cursor that only draws on the graph it is over is easy to
      // miss on a page of six. CursorMoved rather than MouseMove, because the arrow keys move the
      // same cursor and the footer has to follow them too (PRD §45.9).
      plot.CursorMoved += (sender, _) => this.Report(sender as HistoryPlot);
      this._panel.Controls.Add(plot);
    }

    this._strip.Dock = DockStyle.Top;
    this._strip.Height = _StripHeight;
    foreach (var (label, seconds) in _Windows) {
      var span = seconds;
      var button = new Button { Text = label };
      button.Click += (_, _) => this.SetSpan(span);
      this._spans.Add(button);
      this._strip.Controls.Add(button);
    }

    this._footer.Dock = DockStyle.Bottom;
    this._footer.Height = _FooterHeight;
    this._panel.Controls.Add(this._strip);
    this._panel.Controls.Add(this._footer);

    this.SetSpan(60);
  }

  /// <summary>The windows §28 names. Seconds, because that is what the plots are told.</summary>
  private static readonly (string Label, int Seconds)[] _Windows = [
    ("60 s", 60),
    ("5 min", 300),
    ("15 min", 900),
    ("1 h", 3600),
  ];

  public Control Control => this._panel;

  /// <summary>
  /// Which fields this page keeps an hour of, in the order <see cref="Append"/> stores them (PRD §28).
  /// </summary>
  /// <remarks>
  /// Named as fields of the catalogue rather than only as captions, so that "an hour of this is
  /// kept" is a statement the catalogue can be checked against: every one of these declares
  /// <see cref="FieldHistory.Process"/>, and nothing else does (PRD §5.1).
  /// </remarks>
  public static readonly ProcessField[] Plotted = [
    ProcessField.CpuPercent,
    ProcessField.PrivateBytes,
    ProcessField.WorkingSetBytes,
    ProcessField.ReadBytesPerSecond,
    ProcessField.WriteBytesPerSecond,
    ProcessField.GpuEnginePercent,
    ProcessField.HandleCount,
    ProcessField.ThreadCount,
  ];

  /// <summary>The graphs, in the order they are tiled — so a test can point at one (PRD §28).</summary>
  public IReadOnlyList<HistoryPlot> Plots => this._plots;

  /// <summary>What the strip under the graphs currently says: the axis, or the readings under the cursor.</summary>
  public string Footer => this._footer.Text;

  /// <summary>How wide the axis is, in seconds.</summary>
  public int SpanSeconds { get; private set; } = 60;

  /// <summary>How far apart the samples are, which is what turns the span into a sample count.</summary>
  public double SecondsPerSample {
    get => this._secondsPerSample;
    set {
      this._secondsPerSample = value;
      foreach (var plot in this._plots)
        plot.SecondsPerSample = value;

      this.DescribeFooter();
    }
  }

  /// <summary>How many samples the rings hold, which is what the axis can be asked for.</summary>
  public int Retained => Math.Min(_Capacity, this._cpu.Count);

  /// <summary>
  /// What the page is drawing, in text — where the "graph at nought by nought" defects are visible
  /// without a picture (PRD §9.6).
  /// </summary>
  public string Description {
    get {
      var text = new System.Text.StringBuilder();
      text.Append("span:         ").Append(this.SpanSeconds).Append(" s over ")
        .Append(this.Retained).AppendLine(" retained samples");

      foreach (var plot in this._plots)
        text.Append("  ").Append(plot.Caption.PadRight(12)).Append(plot.Bounds).Append("  ")
          .AppendLine(plot.Value.Length > 0 ? plot.Value : "—");

      return text.ToString();
    }
  }

  /// <summary>Sets the width of the axis, in seconds (PRD §28).</summary>
  public void SetSpan(int seconds) {
    this.SpanSeconds = seconds;
    foreach (var plot in this._plots) {
      plot.SpanSeconds = seconds;
      plot.Invalidate();
    }

    this.DescribeFooter();
  }

  /// <summary>
  /// Appends one sample.
  /// </summary>
  /// <remarks>
  /// Every series is appended on every tick, including the ones with nothing to report: a ring that
  /// only grows when a reading exists would put the samples out of step with each other and with the
  /// axis, and the plot would draw a gap as if it had never happened (PRD §3.3).
  /// </remarks>
  public void Append(in ProcessRecord process, SnapshotDelta delta, int index, Counter handles) {
    ArgumentNullException.ThrowIfNull(delta);

    // One ring per field of <see cref="Plotted"/>, in that order. Which fields an hour is kept for
    // is the catalogue's declaration and not this page's opinion — a test asserts the two lists are
    // the same set, so a seventh plot added here without declaring the field historical fails a
    // build (PRD §5.1, §28).
    this._cpu.Add(delta.CpuPercent(index));
    this._private.Add(AsRate(process.PrivateBytes));
    this._workingSet.Add(AsRate(process.WorkingSetBytes));
    this._read.Add(delta.ReadBytesPerSecond(index));
    this._write.Add(delta.WriteBytesPerSecond(index));
    this._gpu.Add(delta.GpuEnginePercent(index));
    this._handles.Add(AsRate(handles));
    this._threads.Add(Rate.Of(process.ThreadCount));

    // A CPU percentage can exceed a hundred when the window is counting per core, and a plot whose
    // ceiling is fixed at a hundred draws four cores held and one core held as the same full graph.
    //
    // Decayed like the others, not a running maximum over the ring. A maximum would let one spike to
    // four hundred per cent hold the axis there for the whole hour behind it, and everything after it
    // would be drawn flat along the bottom.
    this._cpuScale = this._cpuScale.Observe(Latest(this._cpu));
    this._cpuPlot.Maximum = this._cpuScale.Top;
    this._memoryScale = this._memoryScale.Observe(Peak(this._private, this._workingSet));
    this._ioScale = this._ioScale.Observe(Peak(this._read, this._write));
    this._handleScale = this._handleScale.Observe(Latest(this._handles));
    this._threadScale = this._threadScale.Observe(Latest(this._threads));

    this._memoryPlot.Maximum = this._memoryScale.Top;
    this._ioPlot.Maximum = this._ioScale.Top;
    this._handlePlot.Maximum = this._handleScale.Top;
    this._threadPlot.Maximum = this._threadScale.Top;

    this._cpuPlot.ScaleLabel = Humanize.Percent(Rate.Of(this._cpuPlot.Maximum)) + " %";
    this._memoryPlot.ScaleLabel = Bytes(this._memoryScale.Top);
    this._ioPlot.ScaleLabel = Bytes(this._ioScale.Top) + "/s";
    this._handlePlot.ScaleLabel = this._handleScale.Top.ToString("0", CultureInfo.InvariantCulture);
    this._threadPlot.ScaleLabel = this._threadScale.Top.ToString("0", CultureInfo.InvariantCulture);

    this._cpuPlot.Value = Reading(delta.CpuPercent(index), PerformanceUnit.Percent);
    this._memoryPlot.Value = Reading(AsRate(process.PrivateBytes), PerformanceUnit.Bytes);
    this._ioPlot.Value = Reading(delta.IoTotalBytesPerSecond(index), PerformanceUnit.BytesPerSecond);
    this._gpuPlot.Value = Reading(delta.GpuEnginePercent(index), PerformanceUnit.Percent);
    this._handlePlot.Value = Humanize.Count(handles);
    this._threadPlot.Value = process.ThreadCount.ToString(CultureInfo.InvariantCulture);

    this.DescribeFooter();
    foreach (var plot in this._plots)
      plot.Invalidate();
  }

  /// <summary>
  /// The plots, tiled two across.
  /// </summary>
  /// <remarks>
  /// By arithmetic rather than by anchoring: a child anchored inside a docked container makes the
  /// toolkit feed the resolved width back into the parent and the form grows without bound, which is
  /// what MainWindow's own layout note records. Run on resize as well as on the sample tick, because
  /// a window being dragged has to follow the pointer rather than the second hand.
  /// </remarks>
  public void ApplyLayout() {
    var width = this._panel.Width;
    var height = this._panel.Height - _StripHeight - _FooterHeight;
    if (width <= 0 || height <= 0)
      return;

    var x = _Gap;
    foreach (var button in this._spans) {
      // Wide enough for the longest label it carries. At seventy-four "15 min" photographed as
      // "15 ...", which reads as a button that does something else — the same defect the file box's
      // "Compute SHA…" button had.
      button.Bounds = new(x, 4, 86, 24);
      x += 92;
    }

    const int Columns = 2;
    var rows = (this._plots.Count + Columns - 1) / Columns;
    var cellWidth = (width - ((Columns + 1) * _Gap)) / Columns;
    var cellHeight = (height - ((rows + 1) * _Gap)) / rows;
    // A plot smaller than this is not a graph, it is a smudge — and drawing one is how a page ends
    // up looking like it failed rather than like it is too small.
    if (cellWidth < 80 || cellHeight < 48)
      return;

    for (var i = 0; i < this._plots.Count; ++i) {
      var column = i % Columns;
      var row = i / Columns;
      this._plots[i].Bounds = new(
        _Gap + (column * (cellWidth + _Gap)),
        _StripHeight + _Gap + (row * (cellHeight + _Gap)),
        cellWidth,
        cellHeight
      );
    }

    this._laidOutFor = new(width, this._panel.Height);
  }

  /// <summary>Tiles the plots again when the page has changed size since it last did.</summary>
  public void Refresh() {
    if (this._panel.Width != this._laidOutFor.Width || this._panel.Height != this._laidOutFor.Height)
      this.ApplyLayout();
  }

  private void Report(HistoryPlot? plot) {
    if (plot is null || plot.HoverText.Length == 0)
      return;

    this._footer.Text = $"{plot.Caption}   {plot.HoverText}";
  }

  private void DescribeFooter() {
    var window = this.SpanSeconds >= 120
      ? $"{this.SpanSeconds / 60} minutes"
      : $"{this.SpanSeconds} seconds";

    var held = this.Retained * this.SecondsPerSample;
    this._footer.Text =
      $"Axis {window}, sampled every {this.SecondsPerSample:0.##} s. "
      + $"Retained: {this.Retained} samples ({Duration(held)}), the ring's limit is {_Capacity}.\n"
      + "Hover a graph for its readings at that moment; Tab reaches every graph and ←/→ walk the cursor.";
  }

  private static string Duration(double seconds)
    => seconds >= 3600
      ? $"{seconds / 3600:0.#} h"
      : seconds >= 60 ? $"{seconds / 60:0.#} min" : $"{seconds:0} s";

  private static string Reading(Rate value, PerformanceUnit unit) {
    if (!value.HasValue)
      return Humanize.Placeholder(value.Reason);

    return unit switch {
      PerformanceUnit.Bytes => Bytes(value.Value),
      PerformanceUnit.BytesPerSecond => Bytes(value.Value) + "/s",
      PerformanceUnit.Count => Humanize.Count(Counter.Of((ulong)Math.Max(0, value.Value))),
      _ => Humanize.Percent(value) + " %",
    };
  }

  private static string Bytes(double value)
    => Humanize.Bytes(Counter.Of((ulong)Math.Max(0, value)));

  /// <summary>A counter as a plottable reading — and its absence as an absence, never as nought.</summary>
  private static Rate AsRate(Counter counter)
    => counter.TryGetValue(out var value) ? Rate.Of(value) : Rate.Unknown(counter.Reason);

  private static double Latest(HistoryRing<Rate> ring)
    => ring.Count > 0 && ring[ring.Count - 1].HasValue ? ring[ring.Count - 1].Value : 0;

  private static double Peak(HistoryRing<Rate> first, HistoryRing<Rate> second)
    => Math.Max(Latest(first), Latest(second));

  /// <summary>
  /// A scale that follows the readings up quickly and comes back down slowly.
  /// </summary>
  /// <remarks>
  /// Floored, because on an idle process the peak is noise and a graph scaled to noise reads as a
  /// process working hard. Decayed rather than reset, because a ceiling that snapped to each sample
  /// would make the whole plot jump every time one reading spiked.
  /// </remarks>
  private readonly record struct Scale(double Top, double Floor) {

    public static Scale OfBytes(double floor) => new(floor, floor);

    public static Scale OfCount(double floor) => new(floor, floor);

    public Scale Observe(double reading)
      => this with { Top = Math.Max(Math.Max(reading, this.Floor), this.Top * 0.92) };

  }

}
