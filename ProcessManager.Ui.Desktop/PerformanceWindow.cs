using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What the machine is and what it is doing (PRD §45, §46, §47).
/// </summary>
/// <remarks>
/// Opened by clicking any of the plots along the top of the main window, which is where somebody
/// looking at a total goes when they want the detail behind it, and from the View menu for people
/// who would rather not discover that by accident.
/// <para>
/// The figures come from <see cref="PerformanceReport"/>, the same source <c>--host</c> renders, so
/// the window and the terminal cannot disagree about the machine (PRD §58).
/// </para>
/// </remarks>
public sealed class PerformanceWindow : Form {

  private readonly ISystemProbe _probe;
  private readonly Sampler _sampler;
  private readonly List<Label> _values = [];
  private readonly HistoryPlot _cpu = new() { Caption = "CPU", Maximum = 100 };
  private readonly HistoryPlot _memory = new() { Caption = "Memory" };

  /// <param name="cpuHistory">The main window's own rings, shared rather than copied.</param>
  public PerformanceWindow(
    ISystemProbe probe,
    Sampler sampler,
    HistoryRing<Rate> cpuHistory,
    HistoryRing<Rate> memoryHistory
  ) {
    ArgumentNullException.ThrowIfNull(probe);
    ArgumentNullException.ThrowIfNull(sampler);

    this._probe = probe;
    this._sampler = sampler;
    this._cpu.AddSeries(cpuHistory, Color.FromArgb(0x2E, 0x8B, 0x57), "CPU");
    this._memory.AddSeries(memoryHistory, Color.FromArgb(0x46, 0x82, 0xB4), "Memory");

    this.Text = "Performance";
    this.Bounds = new(0, 0, 720, 620);

    this._cpu.Bounds = new(12, 12, 340, 120);
    this._memory.Bounds = new(364, 12, 340, 120);
    this.Controls.Add(this._cpu);
    this.Controls.Add(this._memory);

    this.BuildSections();
  }

  /// <summary>
  /// Lays the sections out in one column of headings and label/value pairs.
  /// </summary>
  /// <remarks>
  /// Built once and refreshed in place: the sections and their order never change, only the values,
  /// so rebuilding the controls every second would make the window flicker for nothing.
  /// </remarks>
  private void BuildSections() {
    var y = 148;
    foreach (var section in PerformanceReport.Build(this._probe.DescribeHost(), this._sampler.Current, this._sampler.Delta)) {
      // Upper case rather than bold: the toolkit's Label has no font of its own, and a heading that
      // reads differently is worth more than one that is merely heavier.
      this.Controls.Add(new Label {
        Text = section.Title.ToUpperInvariant(),
        Bounds = new(12, y, 300, 20),
      });

      y += 24;
      foreach (var row in section.Rows) {
        this.Controls.Add(new Label { Text = row.Label, Bounds = new(24, y, 170, 18) });
        var value = new Label { Text = row.Value, Bounds = new(200, y, 500, 18) };
        this._values.Add(value);
        this.Controls.Add(value);
        y += 20;
      }

      y += 10;
    }
  }

  /// <summary>Refreshes the values from the latest sample. The plots read their rings themselves.</summary>
  public void Update(double memoryScale) {
    var rows = new List<PerformanceRow>();
    foreach (var section in PerformanceReport.Build(this._probe.DescribeHost(), this._sampler.Current, this._sampler.Delta))
      rows.AddRange(section.Rows);

    // The shape is fixed, so the two lists line up. If they ever did not, showing the first n is
    // still better than throwing inside a timer tick.
    var count = Math.Min(rows.Count, this._values.Count);
    for (var i = 0; i < count; ++i)
      if (!string.Equals(this._values[i].Text, rows[i].Value, StringComparison.Ordinal))
        this._values[i].Text = rows[i].Value;

    this._memory.Maximum = memoryScale > 0 ? memoryScale : 100;
    this._cpu.Invalidate();
    this._memory.Invalidate();
  }

}
