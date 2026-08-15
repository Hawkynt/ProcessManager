using System.Drawing;
using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The Process-Explorer-shaped window: system plots on top, the process tree below, a detail pane
/// under it, and a status bar that admits what the sample cost (PRD §7.1).
/// </summary>
public sealed class MainWindow : Form {

  private readonly Sampler _sampler;
  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private readonly ProcessView _view = new() { TreeMode = true, SortColumn = ProcessColumn.CpuPercent, SortDescending = true };
  private readonly ProcessTreeBinder _binder;
  private readonly TreeListView _tree = new();
  private readonly HistoryPlot _cpuPlot = new();
  private readonly HistoryPlot _memoryPlot = new();
  private readonly CoreMeterStrip _cores = new();
  private readonly Label _details = new();
  private readonly Label _status = new();
  private readonly NativeForms.Timer _timer = new();
  private readonly HistoryRing<Rate> _cpuHistory = new(600);
  private readonly HistoryRing<Rate> _memoryHistory = new(600);

  public MainWindow(Sampler sampler, ISystemProbe probe, IProcessActions? actions) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);

    this._sampler = sampler;
    this._probe = probe;
    this._actions = actions;
    this._binder = new(this._tree);

    this.Text = "Process Manager";
    this.Bounds = new(0, 0, 1180, 760);

    this.BuildPlots();
    this.BuildTree();
    this.BuildDetails();
    this.BuildStatus();
    this.BuildMenu();

    this._timer.Interval = 1000;
    this._timer.Tick += (_, _) => this.Refresh();
  }

  /// <summary>The refresh interval in milliseconds.</summary>
  public int Interval {
    get => this._timer.Interval;
    set => this._timer.Interval = Math.Clamp(value, 250, 60_000);
  }

  public void Start() {
    this.Refresh();
    this._timer.Start();
  }

  #region layout

  private void BuildPlots() {
    this._cpuPlot.Caption = "CPU";
    this._cpuPlot.Bounds = new(8, 8, 380, 90);
    this._cpuPlot.AddSeries(this._cpuHistory, Color.FromArgb(0x2E, 0x8B, 0x57), "CPU");

    this._memoryPlot.Caption = "Memory";
    this._memoryPlot.Bounds = new(396, 8, 380, 90);
    this._memoryPlot.AddSeries(this._memoryHistory, Color.FromArgb(0x46, 0x82, 0xB4), "Memory");

    this._cores.Bounds = new(784, 8, 380, 90);

    this.Controls.Add(this._cpuPlot);
    this.Controls.Add(this._memoryPlot);
    this.Controls.Add(this._cores);
  }

  private void BuildTree() {
    this._tree.Bounds = new(8, 106, 1156, 430);
    this._tree.ShowColumnHeaders = true;

    // The columns Process Explorer opens with, in its order. Everything is a selector over the
    // pre-formatted row, so painting never formats a number (PRD §7.2).
    this._tree.Columns.Add(new("Process", 300, static node => ((ProcessRow)node.Tag!).Label));
    this._tree.Columns.Add(new("User", 130, static node => ((ProcessRow)node.Tag!).User));
    this._tree.Columns.Add(new("State", 70, static node => ((ProcessRow)node.Tag!).State));
    this._tree.Columns.Add(new("CPU %", 70, static node => ((ProcessRow)node.Tag!).Cpu));
    this._tree.Columns.Add(new("Private", 85, static node => ((ProcessRow)node.Tag!).Private));
    this._tree.Columns.Add(new("Working set", 95, static node => ((ProcessRow)node.Tag!).WorkingSet));
    this._tree.Columns.Add(new("Read/s", 80, static node => ((ProcessRow)node.Tag!).Read));
    this._tree.Columns.Add(new("Write/s", 80, static node => ((ProcessRow)node.Tag!).Write));
    this._tree.Columns.Add(new("Threads", 65, static node => ((ProcessRow)node.Tag!).Threads));
    this._tree.Columns.Add(new("Handles", 70, static node => ((ProcessRow)node.Tag!).Handles));
    this._tree.Columns.Add(new("Started", 150, static node => ((ProcessRow)node.Tag!).Started));

    this._tree.AfterSelect += (_, _) => this.UpdateDetails();
    this._tree.ContextMenuStrip = this.BuildContextMenu();
    this.Controls.Add(this._tree);
  }

  private ContextMenuStrip BuildContextMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Properties", () => this.UpdateDetails()));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("End process", () => this.Act("end", key => this._actions!.Terminate(key))));
    menu.Items.Add(Item("Suspend", () => this.Act("suspend", key => this._actions!.Suspend(key))));
    menu.Items.Add(Item("Resume", () => this.Act("resume", key => this._actions!.Resume(key))));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("Read handle count", this.FillHandleCounts));
    return menu;
  }

  private static ToolStripMenuItem Item(string text, Action action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => action();
    return item;
  }

  private void BuildDetails() {
    this._details.Bounds = new(8, 544, 1156, 150);
    this._details.Text = "Select a process.";
    this.Controls.Add(this._details);
  }

  private void BuildStatus() {
    this._status.Bounds = new(8, 700, 1156, 22);
    this.Controls.Add(this._status);
  }

  private void BuildMenu() {
    var menu = new MenuStrip();

    var view = new ToolStripMenuItem("View");
    view.DropDownItems.Add(Item("Tree", () => {
      this._view.TreeMode = !this._view.TreeMode;
      this.Refresh();
    }));

    view.DropDownItems.Add(Item("Show all users", () => {
      this._view.UserIdFilter = null;
      this.Refresh();
    }));

    view.DropDownItems.Add(Item("CPU % convention", () => {
      this._sampler.CpuPercentMode = this._sampler.CpuPercentMode == CpuPercentMode.Normalized
        ? CpuPercentMode.PerCore
        : CpuPercentMode.Normalized;

      this.Refresh();
    }));

    menu.Items.Add(view);
    this.Controls.Add(menu);
  }

  #endregion

  #region refresh

  private new void Refresh() {
    this._sampler.Sample();
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;

    this._cpuHistory.Add(delta.SystemCpuPercent);
    this._memoryHistory.Add(MemoryPercent(snapshot.System));

    this._view.Rebuild(snapshot, delta);
    this._binder.Sync(snapshot, delta, this._view);
    this._cores.Bind(delta);
    this._cpuPlot.Invalidate();
    this._memoryPlot.Invalidate();

    this.UpdateStatus(snapshot, delta);
    this.UpdateDetails();
  }

  private static Rate MemoryPercent(in SystemCounters system) {
    if (!system.TotalMemoryBytes.HasValue || system.TotalMemoryBytes.Value == 0 || !system.AvailableMemoryBytes.HasValue)
      return Rate.Gap;

    var total = system.TotalMemoryBytes.Value;
    var used = total - Math.Min(total, system.AvailableMemoryBytes.Value);
    return Rate.Of(used * 100d / total);
  }

  private void UpdateStatus(SystemSnapshot snapshot, SnapshotDelta delta) {
    var mode = this._sampler.CpuPercentMode == CpuPercentMode.PerCore ? "per core" : "normalized";
    this._status.Text =
      $"{this._view.RowCount} of {snapshot.ProcessCount} processes  ·  "
      + $"CPU {Humanize.Percent(delta.SystemCpuPercent)}% ({mode})  ·  "
      + $"memory {Humanize.Bytes(snapshot.System.TotalMemoryBytes)} total, {Humanize.Bytes(snapshot.System.AvailableMemoryBytes)} free  ·  "
      + $"sample {this._sampler.LastSampleDuration.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms";
  }

  private void UpdateDetails() {
    var row = this._binder.SelectedRow;
    if (row is null) {
      this._details.Text = "Select a process.";
      return;
    }

    if (!this._sampler.Current.TryGetProcess(row.Key, out var process)) {
      this._details.Text = $"{row.Label} — this process has ended.";
      return;
    }

    this._details.Text =
      $"{process.Name} ({process.Pid})   parent {process.ParentPid}   user {row.User}   session {process.SessionId}\n"
      + $"CPU {row.Cpu} %   private {row.Private}   working set {row.WorkingSet}   virtual {Humanize.Bytes(process.VirtualBytes)}   swap {Humanize.Bytes(process.SwapBytes)}\n"
      + $"threads {row.Threads}   handles {row.Handles}   priority {process.Priority}   nice {process.Nice}   started {row.Started}\n"
      + $"image {process.ImagePath ?? "—"}\n"
      + $"cgroup {process.ContainerPath ?? "—"}\n"
      + $"command line {(row.CommandLine.Length > 0 ? row.CommandLine : "—")}";
  }

  private void FillHandleCounts() {
    // Only the selected row: on Linux this makes the kernel walk the process's whole descriptor
    // table, which is why it is not in the sample (PRD §3.5).
    if (this._binder.SelectedRow is not { } row)
      return;

    this._binder.HandleCounts[row.Key] = this._probe.GetHandleCount(row.Key);
    this.Refresh();
  }

  #endregion

  private void Act(string what, Func<ProcessKey, ActionResult> action) {
    if (this._binder.SelectedRow is not { } row)
      return;

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    // Confirmed before it happens, and the target named unambiguously — a pid on its own is not a
    // name, and the row under the pointer may have moved (PRD §6.4).
    var answer = MessageBox.Show(
      $"{char.ToUpper(what[0], CultureInfo.CurrentCulture)}{what[1..]} {row.Name} ({row.Pid})?",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (answer != DialogResult.Yes)
      return;

    var result = action(row.Key);
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");

    this.Refresh();
  }

}
