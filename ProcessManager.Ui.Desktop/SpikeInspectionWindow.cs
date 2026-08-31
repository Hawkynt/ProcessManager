using System.Globalization;
using System.Text;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The processes that caused one retained point in a system-history graph (PRD §45, §73).
/// </summary>
/// <remarks>
/// The row keeps the historical identity and says whether that exact process still exists now. A
/// process that has exited remains useful evidence and is never silently rebound to a later process
/// that inherited its PID. Double-clicking raises the stable <see cref="ProcessKey"/> for the owning
/// window to navigate when it still can.
/// </remarks>
internal sealed class SpikeInspectionWindow : Form {

  private const int _Margin = 12;

  private readonly Label _heading = new();
  private readonly TreeListView _rows = new();
  private readonly Button _close = new() { Text = "Close" };
  private readonly List<Entry> _entries = [];
  private readonly SpikeMetric _metric;

  private readonly record struct Entry(SpikeContributor Contributor, bool IsAlive);

  public SpikeInspectionWindow(
    SpikeMetric metric,
    long utcTicks,
    int sampleAge,
    ReadOnlySpan<SpikeContributor> contributors,
    Func<ProcessKey, bool> isAlive
  ) {
    ArgumentNullException.ThrowIfNull(isAlive);

    this._metric = metric;
    this.Text = $"{MetricName(metric)} — contributors";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 760, 390);
    this.MinimumSize = new(520, 280);

    this._heading.Text = Heading(metric, utcTicks, sampleAge, contributors.Length);
    this._heading.AccessibleName = "Historical sample";

    this._rows.ShowColumnHeaders = true;
    this._rows.ItemHeight = 18;
    this._rows.AccessibleName = "Processes contributing to the historical sample";
    this._rows.Columns.Add(new("#", 40, node => (this._entries.IndexOf(Row(node)) + 1).ToString(CultureInfo.InvariantCulture)));
    this._rows.Columns.Add(new("Process", 190, node => Row(node).Contributor.Name));
    this._rows.Columns.Add(new("PID", 72, node => Row(node).Contributor.Key.Pid.ToString(CultureInfo.InvariantCulture)));
    this._rows.Columns.Add(new("User", 130, node => Row(node).Contributor.UserName ?? "—"));
    this._rows.Columns.Add(new("Contribution", 150, node => Reading(metric, Row(node).Contributor.Value)));
    this._rows.Columns.Add(new("State now", 110, node => Row(node).IsAlive ? "running" : "exited"));
    this._rows.MouseDoubleClick += (_, _) => this.Choose();

    for (var i = 0; i < contributors.Length; ++i) {
      var entry = new Entry(contributors[i], isAlive(contributors[i].Key));
      this._entries.Add(entry);
      this._rows.Nodes.Add(new TreeNode(entry.Contributor.Name) { Tag = entry });
    }

    this._close.Click += (_, _) => this.Close();
    this.Controls.Add(this._heading);
    this.Controls.Add(this._rows);
    this.Controls.Add(this._close);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>Raised when somebody asks to go to a retained contributor.</summary>
  public event EventHandler<ProcessKey>? ProcessChosen;

  /// <summary>
  /// Text form of the evidence in the window, for tests, accessibility diagnostics and copying later.
  /// </summary>
  public string Describe() {
    var text = new StringBuilder();
    text.AppendLine(this._heading.Text);
    foreach (var entry in this._entries)
      text.Append(entry.Contributor.Name)
        .Append(" (PID ").Append(entry.Contributor.Key.Pid.ToString(CultureInfo.InvariantCulture)).Append(") — ")
        .Append(Reading(this._metric, entry.Contributor.Value)).Append(" — ")
        .AppendLine(entry.IsAlive ? "running" : "exited");

    return text.ToString();
  }

  private static Entry Row(TreeNode node) => (Entry)node.Tag!;

  private void Choose() {
    if (this._rows.SelectedNode?.Tag is not Entry entry)
      return;

    this.ProcessChosen?.Invoke(this, entry.Contributor.Key);
  }

  private static string Heading(SpikeMetric metric, long utcTicks, int sampleAge, int count) {
    var when = utcTicks > 0
      ? Humanize.Timestamp(utcTicks)
      : $"{sampleAge.ToString(CultureInfo.InvariantCulture)} samples ago";
    var found = count switch {
      0 => "No process contribution was measured.",
      1 => "1 contributing process retained.",
      _ => $"{count.ToString(CultureInfo.InvariantCulture)} contributing processes retained.",
    };

    return $"{MetricName(metric)} at {when}. {found} Exited processes remain historical evidence.";
  }

  private static string MetricName(SpikeMetric metric) => metric switch {
    SpikeMetric.Cpu => "Processor activity",
    SpikeMetric.Io => "Process I/O",
    _ => "Private memory growth",
  };

  private static string Reading(SpikeMetric metric, Rate value) => metric switch {
    SpikeMetric.Cpu => value.HasValue ? Humanize.Percent(value) + " %" : Humanize.Placeholder(value.Reason),
    _ => Humanize.BytesPerSecond(value),
  };

  private void ApplyLayout() {
    var width = Math.Max(360, this.Width - (2 * _Margin));
    this._heading.Bounds = new(_Margin, _Margin, width, 34);
    this._rows.Bounds = new(_Margin, 52, width, Math.Max(120, this.Height - 104));
    this._close.Bounds = new(this.Width - _Margin - 88, this.Height - 40, 88, 26);
  }

}
