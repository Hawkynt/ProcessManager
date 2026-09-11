using System.Globalization;
using System.Text;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Rewinds the continuously recorded system state without stopping live sampling.
/// </summary>
/// <remarks>
/// This is deliberately a separate investigation window: going back ten minutes must not make the
/// main process list look live while its rows are historical, nor should looking at history pause the
/// sampler that is collecting the next minute. The controls mirror the useful part of Spotlight's
/// playback model — live, rewind and skip — without coupling the recorder to a particular widget.
/// </remarks>
public sealed class PlaybackWindow : Form {

  private const int _Margin = 12;
  private const int _ButtonWidth = 78;
  private const int _ButtonGap = 6;

  private readonly SystemPlaybackHistory _history;
  private readonly Label _heading = new();
  private readonly Label _range = new();
  private readonly TreeListView _rows = new();
  private readonly Button _live = new() { Text = "Live" };
  private readonly Button _backHour = new() { Text = "-1 h" };
  private readonly Button _backTen = new() { Text = "-10 m" };
  private readonly Button _backFive = new() { Text = "-5 m" };
  private readonly Button _backMinute = new() { Text = "-1 m" };
  private readonly Button _forwardMinute = new() { Text = "+1 m" };
  private readonly Button _forwardFive = new() { Text = "+5 m" };
  private readonly Button _forwardTen = new() { Text = "+10 m" };
  private readonly Button _forwardHour = new() { Text = "+1 h" };
  private readonly NativeForms.Timer _follow = new() { Interval = 500 };
  private DateTime? _targetUtc;

  public PlaybackWindow(SystemPlaybackHistory history) {
    ArgumentNullException.ThrowIfNull(history);
    this._history = history;

    this.Text = "System playback";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 1120, 660);
    this.MinimumSize = new(760, 420);

    this._heading.AccessibleName = "Playback time and system summary";
    this._range.AccessibleName = "Available playback range";

    this._rows.ShowColumnHeaders = true;
    this._rows.ItemHeight = 18;
    this._rows.AccessibleName = "Processes at the selected historical time";
    this._rows.Columns.Add(new("Process", 190, node => Row(node).Name));
    this._rows.Columns.Add(new("PID", 68, node => Row(node).Key.Pid.ToString(CultureInfo.InvariantCulture)));
    this._rows.Columns.Add(new("Parent", 68, node => Row(node).ParentPid.ToString(CultureInfo.InvariantCulture)));
    this._rows.Columns.Add(new("User", 130, node => Row(node).UserName ?? "—"));
    this._rows.Columns.Add(new("State", 82, node => Row(node).State.ToString()));
    this._rows.Columns.Add(new("CPU", 72, node => Percent(Row(node).CpuPercent)));
    this._rows.Columns.Add(new("Private", 92, node => Humanize.Bytes(Row(node).PrivateBytes)));
    this._rows.Columns.Add(new("Working set", 102, node => Humanize.Bytes(Row(node).WorkingSetBytes)));
    this._rows.Columns.Add(new("I/O", 94, node => Humanize.BytesPerSecond(Row(node).IoBytesPerSecond)));
    this._rows.Columns.Add(new("GPU", 72, node => Percent(Row(node).GpuPercent)));
    this._rows.Columns.Add(new("Threads", 70, node => Row(node).ThreadCount.ToString(CultureInfo.InvariantCulture)));

    this._live.Click += (_, _) => this.GoLive();
    this._backHour.Click += (_, _) => this.Move(TimeSpan.FromHours(-1));
    this._backTen.Click += (_, _) => this.Move(TimeSpan.FromMinutes(-10));
    this._backFive.Click += (_, _) => this.Move(TimeSpan.FromMinutes(-5));
    this._backMinute.Click += (_, _) => this.Move(TimeSpan.FromMinutes(-1));
    this._forwardMinute.Click += (_, _) => this.Move(TimeSpan.FromMinutes(1));
    this._forwardFive.Click += (_, _) => this.Move(TimeSpan.FromMinutes(5));
    this._forwardTen.Click += (_, _) => this.Move(TimeSpan.FromMinutes(10));
    this._forwardHour.Click += (_, _) => this.Move(TimeSpan.FromHours(1));
    this._follow.Tick += (_, _) => this.FollowLive();
    this.FormClosed += (_, _) => this._follow.Dispose();

    foreach (var control in (ReadOnlySpan<Control>)[
      this._heading,
      this._range,
      this._rows,
      this._live,
      this._backHour,
      this._backTen,
      this._backFive,
      this._backMinute,
      this._forwardMinute,
      this._forwardFive,
      this._forwardTen,
      this._forwardHour,
    ])
      this.Controls.Add(control);

    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
    this.GoLive();
    this._follow.Start();
  }

  public int RowCount => this._rows.Nodes.Count;
  public DateTime? SelectedUtc { get; private set; }

  /// <summary>Text form used by smoke tests and accessibility diagnostics.</summary>
  public string Describe() {
    var text = new StringBuilder();
    text.AppendLine(this._heading.Text);
    text.AppendLine(this._range.Text);
    text.Append(this.RowCount.ToString(CultureInfo.InvariantCulture)).AppendLine(" process rows");
    return text.ToString();
  }

  public void GoLive() {
    this._targetUtc = null;
    this.FollowLive(force: true);
  }

  public void Rewind(TimeSpan age) {
    if (age < TimeSpan.Zero)
      age = -age;
    var newest = this._history.NewestUtc;
    if (newest is null) {
      this.ShowEmpty();
      return;
    }

    this._targetUtc = newest.Value - age;
    this.RefreshTarget();
  }

  private void FollowLive(bool force = false) {
    if (this._targetUtc is not null)
      return;

    if (!this._history.TryNewest(out var frame)) {
      this.ShowEmpty();
      return;
    }

    if (!force && this.SelectedUtc == frame.TimestampUtc)
      return;

    this.ShowFrame(frame, live: true);
  }

  private void Move(TimeSpan delta) {
    var origin = this._targetUtc ?? this.SelectedUtc ?? this._history.NewestUtc;
    if (origin is null) {
      this.ShowEmpty();
      return;
    }

    var target = origin.Value + delta;
    if (this._history.NewestUtc is { } newest && target >= newest) {
      this.GoLive();
      return;
    }

    this._targetUtc = target;
    this.RefreshTarget();
  }

  private void RefreshTarget() {
    if (this._targetUtc is not { } target || !this._history.TryAtOrBefore(target, out var frame)) {
      this.ShowEmpty();
      return;
    }

    this._targetUtc = frame.TimestampUtc;
    this.ShowFrame(frame, live: false);
  }

  private void ShowFrame(SystemPlaybackFrame frame, bool live) {
    var processes = frame.Processes;
    this._rows.Nodes.Clear();
    for (var i = 0; i < processes.Length; ++i) {
      var sample = processes[i];
      this._rows.Nodes.Add(new TreeNode(sample.Name) { Tag = sample });
    }

    this.SelectedUtc = frame.TimestampUtc;
    var system = frame.System;
    var memory = Memory(system);
    var mode = live ? "LIVE" : "HISTORICAL";
    this._heading.Text = $"{mode}  {Humanize.Timestamp(frame.UtcTicks)}   CPU {Percent(frame.SystemCpuPercent)}   Memory {memory}   {processes.Length.ToString(CultureInfo.InvariantCulture)} processes";
    this.UpdateRange();
  }

  private void ShowEmpty() {
    this.SelectedUtc = null;
    this._rows.Nodes.Clear();
    this._heading.Text = "No playback samples have been recorded yet.";
    this.UpdateRange();
  }

  private void UpdateRange() {
    if (this._history.OldestUtc is not { } oldest || this._history.NewestUtc is not { } newest) {
      this._range.Text = "Playback begins with the first desktop sample.";
      return;
    }

    this._range.Text = $"Available: {Humanize.Timestamp(oldest.Ticks)} — {Humanize.Timestamp(newest.Ticks)}   {this._history.RetainedFrameCount.ToString(CultureInfo.InvariantCulture)} retained frames";
  }

  private static PlaybackProcessSample Row(TreeNode node) => (PlaybackProcessSample)node.Tag!;

  private static string Percent(Rate rate)
    => rate.HasValue ? Humanize.Percent(rate) + " %" : Humanize.Placeholder(rate.Reason);

  private static string Memory(SystemCounters system) {
    if (!system.TotalMemoryBytes.HasValue)
      return Humanize.Placeholder(system.TotalMemoryBytes.Reason);
    if (!system.AvailableMemoryBytes.HasValue)
      return Humanize.Bytes(system.TotalMemoryBytes);

    var used = system.TotalMemoryBytes.Value >= system.AvailableMemoryBytes.Value
      ? system.TotalMemoryBytes.Value - system.AvailableMemoryBytes.Value
      : 0;
    return $"{Humanize.Bytes(Counter.Of(used))} / {Humanize.Bytes(system.TotalMemoryBytes)}";
  }

  private void ApplyLayout() {
    var width = Math.Max(600, this.Width - (2 * _Margin));
    this._heading.Bounds = new(_Margin, _Margin, width, 24);
    this._range.Bounds = new(_Margin, 36, width, 20);

    var x = _Margin;
    foreach (var button in (ReadOnlySpan<Button>)[
      this._live,
      this._backHour,
      this._backTen,
      this._backFive,
      this._backMinute,
      this._forwardMinute,
      this._forwardFive,
      this._forwardTen,
      this._forwardHour,
    ]) {
      button.Bounds = new(x, 62, _ButtonWidth, 26);
      x += _ButtonWidth + _ButtonGap;
    }

    this._rows.Bounds = new(_Margin, 98, width, Math.Max(240, this.Height - 110));
  }

}
