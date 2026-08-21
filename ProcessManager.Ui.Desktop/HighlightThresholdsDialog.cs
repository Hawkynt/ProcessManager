using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// When a cell is busy enough to be marked (PRD §23).
/// </summary>
/// <remarks>
/// <para>
/// The thresholds have been settable from the settings file since the bands existed, which is only
/// half of "configurable": somebody who thinks a hundred megabytes a second is nothing on their NVMe
/// has to find the file, learn six key names and restart the program to say so. This is the other
/// half, and it writes the same six — now eight — numbers the file holds.
/// </para>
/// <para>
/// Byte rates are entered in megabytes a second rather than in bytes. The stored unit is bytes,
/// because that is what a reading is measured in, but a spinner counting to a hundred million is not
/// a control anybody can use.
/// </para>
/// </remarks>
public sealed class HighlightThresholdsDialog : Form {

  private const int _Margin = 14;
  private const int _RowHeight = 30;
  private const int _LabelWidth = 250;
  private const int _FieldWidth = 96;
  private const int _ButtonHeight = 28;
  private const double _Megabyte = 1024 * 1024;

  private readonly (Label Caption, NumericUpDown Warm, NumericUpDown Hot)[] _rows;
  private readonly Label _heading = new() { Text = "Warm" };
  private readonly Label _hotHeading = new() { Text = "Hot" };
  private readonly Label _note = new();
  private readonly Button _defaults = new() { Text = "Restore defaults" };
  private readonly Button _ok = new() { Text = "OK" };
  private readonly Button _cancel = new() { Text = "Cancel" };

  public HighlightThresholdsDialog(UsageThresholds thresholds) {
    this.Text = "Highlighting thresholds";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;

    this._rows = [
      // Per core, and the caption says so — a spinner reading 50 with no unit beside it is the exact
      // ambiguity §23 spends three paragraphs on.
      (Caption("CPU, % of one core"), Spinner(thresholds.WarmCpuPercent, 0, 6400), Spinner(thresholds.HotCpuPercent, 0, 6400)),
      (Caption("Memory, % of the machine"), Spinner(thresholds.WarmMemoryPercent, 0, 100), Spinner(thresholds.HotMemoryPercent, 0, 100)),
      (Caption("Disk, MB/s"), Spinner(thresholds.WarmBytesPerSecond / _Megabyte, 0, 100_000), Spinner(thresholds.HotBytesPerSecond / _Megabyte, 0, 100_000)),
      (Caption("GPU, % of the adapter"), Spinner(thresholds.WarmGpuPercent, 0, 100), Spinner(thresholds.HotGpuPercent, 0, 100)),
    ];

    this._note.Text =
      "A threshold of nought turns its band off rather than marking everything.\n"
      + "A reading that does not exist is never marked, in either direction: a counter\n"
      + "that came back \"not permitted\" is not a measurement of zero.\n"
      + "\n"
      + "The mark goes on the cell, not the row — the row's colour already answers a\n"
      + "different question, and colouring for both would mean one of the two winning.";

    this._defaults.Click += (_, _) => this.Fill(UsageThresholds.Default);
    this._ok.Click += (_, _) => {
      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();

    this.Controls.Add(this._heading);
    this.Controls.Add(this._hotHeading);
    foreach (var (caption, warm, hot) in this._rows) {
      this.Controls.Add(caption);
      this.Controls.Add(warm);
      this.Controls.Add(hot);
    }

    this.Controls.Add(this._note);
    this.Controls.Add(this._defaults);
    this.Controls.Add(this._ok);
    this.Controls.Add(this._cancel);

    // Sized to what it holds rather than to a round number somebody liked: a box with eighty pixels
    // of nothing in the middle is what a fixed height produces the moment a row is added.
    var height = _Margin + _RowHeight + (this._rows.Length * _RowHeight) + 118 + _ButtonHeight + _Margin;
    this.Bounds = new(0, 0, 520, height);
    this.MinimumSize = new(420, height);
    // Laid out by arithmetic rather than by anchoring: a child anchored inside a docked container
    // here grows without bound, which is what MainWindow's own layout note records.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>True when the dialog was closed with OK.</summary>
  public bool Accepted { get; private set; }

  /// <summary>What the spinners currently say, in the units the engine stores.</summary>
  public UsageThresholds Thresholds => new(
    (double)this._rows[0].Warm.Value,
    (double)this._rows[0].Hot.Value,
    (double)this._rows[1].Warm.Value,
    (double)this._rows[1].Hot.Value,
    (double)this._rows[2].Warm.Value * _Megabyte,
    (double)this._rows[2].Hot.Value * _Megabyte
  ) {
    WarmGpuPercent = (double)this._rows[3].Warm.Value,
    HotGpuPercent = (double)this._rows[3].Hot.Value,
  };

  /// <summary>What the box says, for a test with no display to read it off.</summary>
  public string Description {
    get {
      var lines = new List<string>();
      foreach (var (caption, warm, hot) in this._rows)
        lines.Add($"{caption.Text}: warm {warm.Value}, hot {hot.Value}");

      return string.Join('\n', lines);
    }
  }

  private void Fill(UsageThresholds thresholds) {
    this._rows[0].Warm.Value = (decimal)thresholds.WarmCpuPercent;
    this._rows[0].Hot.Value = (decimal)thresholds.HotCpuPercent;
    this._rows[1].Warm.Value = (decimal)thresholds.WarmMemoryPercent;
    this._rows[1].Hot.Value = (decimal)thresholds.HotMemoryPercent;
    this._rows[2].Warm.Value = (decimal)(thresholds.WarmBytesPerSecond / _Megabyte);
    this._rows[2].Hot.Value = (decimal)(thresholds.HotBytesPerSecond / _Megabyte);
    this._rows[3].Warm.Value = (decimal)thresholds.WarmGpuPercent;
    this._rows[3].Hot.Value = (decimal)thresholds.HotGpuPercent;
  }

  public void ApplyLayout() {
    var warmLeft = _Margin + _LabelWidth;
    var hotLeft = warmLeft + _FieldWidth + 12;
    var y = _Margin;

    this._heading.Bounds = new(warmLeft, y, _FieldWidth, 20);
    this._hotHeading.Bounds = new(hotLeft, y, _FieldWidth, 20);
    y += _RowHeight;

    foreach (var (caption, warm, hot) in this._rows) {
      caption.Bounds = new(_Margin, y + 4, _LabelWidth - 8, 20);
      warm.Bounds = new(warmLeft, y, _FieldWidth, 24);
      hot.Bounds = new(hotLeft, y, _FieldWidth, 24);
      y += _RowHeight;
    }

    this._note.Bounds = new(_Margin, y + 6, Math.Max(200, this.Width - (2 * _Margin)), 112);

    var buttons = Math.Max(y + 40, this.Height - _Margin - _ButtonHeight);
    this._defaults.Bounds = new(_Margin, buttons, 140, _ButtonHeight);
    this._cancel.Bounds = new(this.Width - _Margin - 84, buttons, 84, _ButtonHeight);
    this._ok.Bounds = new(this._cancel.Bounds.X - 94, buttons, 84, _ButtonHeight);
  }

  private static Label Caption(string text) => new() { Text = text };

  private static NumericUpDown Spinner(double value, decimal minimum, decimal maximum) {
    var spinner = new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = 0 };
    spinner.Value = Math.Clamp((decimal)Math.Round(value), minimum, maximum);
    return spinner;
  }

}
