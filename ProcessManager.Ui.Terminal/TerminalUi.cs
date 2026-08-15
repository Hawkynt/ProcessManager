using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// The full-screen terminal front-end: per-core meters and a process list, driven by the same
/// engine the window is (PRD §11).
/// </summary>
public sealed class TerminalUi {

  private readonly Sampler _sampler;
  private readonly IProcessActions? _actions;
  private readonly ISystemProbe _probe;
  private readonly ProcessView _view = new();
  private readonly TerminalScreen _screen;
  private readonly HistoryRing<Rate> _cpuHistory = new(240);
  private readonly Dictionary<ProcessKey, Counter> _handleCounts = [];

  private int _selectedRow;
  private ProcessKey _selectedKey;
  private int _scrollOffset;
  private string _message = string.Empty;
  private byte _messageAttribute = Attributes.Dim;
  private InputMode _mode;
  private string _input = string.Empty;
  private ProcessKey _confirmTarget;
  private bool _confirmTree;

  private enum InputMode : byte { Normal, Search, Filter, ConfirmKill }

  public TerminalUi(Sampler sampler, ISystemProbe probe, IProcessActions? actions, int width, int height, ColorDepth depth) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);
    this._sampler = sampler;
    this._probe = probe;
    this._actions = actions;
    this._screen = new(width, height, depth);
    this._view.TreeMode = false;
    this._view.SortColumn = ProcessColumn.CpuPercent;
    this._view.SortDescending = true;
  }

  public ProcessView View => this._view;

  public TerminalScreen Screen => this._screen;

  /// <summary>True once the user has asked to leave.</summary>
  public bool ShouldQuit { get; private set; }

  public void Resize(int width, int height) => this._screen.Resize(width, height);

  /// <summary>Takes a sample and composes the next frame. Does not write to any terminal.</summary>
  public void Update() {
    this._sampler.Sample();
    this._cpuHistory.Add(this._sampler.Delta.SystemCpuPercent);
    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    this.Compose();
  }

  /// <summary>Recomposes without sampling — for a keypress that only changes what is shown.</summary>
  public void Refresh() {
    this._view.Rebuild(this._sampler.Current, this._sampler.Delta);
    this.RestoreSelection();
    this.Compose();
  }

  public void Flush(TextWriter writer) => this._screen.Flush(writer);

  #region selection

  private void RestoreSelection() {
    // The selection follows the process, not the row number. Without this a re-sort between two
    // samples moves whatever is under the cursor, and the next keystroke acts on the wrong program
    // (PRD §7.3).
    if (!this._selectedKey.IsNone) {
      var row = this._view.FindRow(this._selectedKey);
      if (row >= 0) {
        this._selectedRow = row;
        this.ClampScroll();
        return;
      }
    }

    this._selectedRow = Math.Clamp(this._selectedRow, 0, Math.Max(0, this._view.RowCount - 1));
    this._selectedKey = this.KeyAt(this._selectedRow);
    this.ClampScroll();
  }

  private ProcessKey KeyAt(int row) {
    if ((uint)row >= (uint)this._view.RowCount)
      return ProcessKey.None;

    return this._sampler.Current.Processes[this._view.Rows[row].Index].Key;
  }

  private int ListHeight => Math.Max(1, this._screen.Height - this.HeaderHeight - 2);

  private int HeaderHeight {
    get {
      var cores = this._sampler.Current.System.CoreCount;
      // Two meters per line, plus the memory and swap lines and a blank one.
      return Math.Max(2, (cores + 1) / 2) + 3;
    }
  }

  private void ClampScroll() {
    var height = this.ListHeight;
    if (this._selectedRow < this._scrollOffset)
      this._scrollOffset = this._selectedRow;
    else if (this._selectedRow >= this._scrollOffset + height)
      this._scrollOffset = this._selectedRow - height + 1;

    this._scrollOffset = Math.Clamp(this._scrollOffset, 0, Math.Max(0, this._view.RowCount - height));
  }

  #endregion

  #region input

  /// <summary>Handles one key. Returns true when the frame needs recomposing.</summary>
  public bool HandleKey(ConsoleKeyInfo key) {
    switch (this._mode) {
      case InputMode.Search or InputMode.Filter:
        return this.HandleTextInput(key);
      case InputMode.ConfirmKill:
        return this.HandleConfirm(key);
      default:
        return this.HandleNormal(key);
    }
  }

  private bool HandleNormal(ConsoleKeyInfo key) {
    switch (key.Key) {
      case ConsoleKey.UpArrow: this.MoveSelection(-1); return true;
      case ConsoleKey.DownArrow: this.MoveSelection(1); return true;
      case ConsoleKey.PageUp: this.MoveSelection(-this.ListHeight); return true;
      case ConsoleKey.PageDown: this.MoveSelection(this.ListHeight); return true;
      case ConsoleKey.Home: this.MoveSelection(int.MinValue / 2); return true;
      case ConsoleKey.End: this.MoveSelection(int.MaxValue / 2); return true;
      case ConsoleKey.F5: this._view.TreeMode = !this._view.TreeMode; return true;
      case ConsoleKey.F6: this.NextSortColumn(); return true;
      case ConsoleKey.F9: this.BeginKill(tree: false); return true;
      case ConsoleKey.F10 or ConsoleKey.Escape: this.ShouldQuit = true; return false;
      case ConsoleKey.F3: this.BeginInput(InputMode.Search); return true;
    }

    switch (key.KeyChar) {
      case 'q': this.ShouldQuit = true; return false;
      case 't': this._view.TreeMode = !this._view.TreeMode; return true;
      case '/': this.BeginInput(InputMode.Search); return true;
      case '\\': this.BeginInput(InputMode.Filter); return true;
      case 'u': this.ToggleUserFilter(); return true;
      case 'k': this.BeginKill(tree: false); return true;
      case 'K': this.BeginKill(tree: true); return true;
      case 'I': this._view.SortDescending = !this._view.SortDescending; return true;
      case 'h': this.FillHandleCounts(); return true;
      case '<': this.PreviousSortColumn(); return true;
      case '>': this.NextSortColumn(); return true;
      case 'P': this._view.SortColumn = ProcessColumn.CpuPercent; this._view.SortDescending = true; return true;
      case 'M': this._view.SortColumn = ProcessColumn.PrivateBytes; this._view.SortDescending = true; return true;
      case 'T': this._view.SortColumn = ProcessColumn.StartTime; this._view.SortDescending = false; return true;
      case 'N': this._view.SortColumn = ProcessColumn.Pid; this._view.SortDescending = false; return true;
      case 'C':
        this._sampler.CpuPercentMode = this._sampler.CpuPercentMode == CpuPercentMode.Normalized
          ? CpuPercentMode.PerCore
          : CpuPercentMode.Normalized;
        this.Say($"CPU% is now {(this._sampler.CpuPercentMode == CpuPercentMode.PerCore ? "per core (100% = one core)" : "normalized (100% = whole machine)")}", Attributes.Accent);
        return true;
      default: return false;
    }
  }

  private bool HandleTextInput(ConsoleKeyInfo key) {
    switch (key.Key) {
      case ConsoleKey.Escape:
        this._input = string.Empty;
        this.ApplyFilter();
        this._mode = InputMode.Normal;
        return true;
      case ConsoleKey.Enter:
        this._mode = InputMode.Normal;
        return true;
      case ConsoleKey.Backspace:
        if (this._input.Length > 0)
          this._input = this._input[..^1];

        this.ApplyFilter();
        return true;
    }

    if (char.IsControl(key.KeyChar))
      return false;

    this._input += key.KeyChar;
    this.ApplyFilter();
    return true;
  }

  private void ApplyFilter()
    => this._view.TextFilter = string.IsNullOrEmpty(this._input) ? null : this._input;

  private bool HandleConfirm(ConsoleKeyInfo key) {
    this._mode = InputMode.Normal;
    if (key.KeyChar is not ('y' or 'Y')) {
      this.Say("cancelled", Attributes.Dim);
      return true;
    }

    if (this._actions is null) {
      this.Say("no actions are available in this build", Attributes.Bad);
      return true;
    }

    if (this._confirmTree)
      this.KillTree(this._confirmTarget);
    else {
      var result = this._actions.Terminate(this._confirmTarget);
      this.Say(
        result.Succeeded ? $"sent SIGTERM to {this._confirmTarget.Pid}" : result.Detail ?? "failed",
        result.Succeeded ? Attributes.Good : Attributes.Bad
      );
    }

    return true;
  }

  private void MoveSelection(int delta) {
    if (this._view.RowCount == 0)
      return;

    this._selectedRow = Math.Clamp(this._selectedRow + delta, 0, this._view.RowCount - 1);
    this._selectedKey = this.KeyAt(this._selectedRow);
    this.ClampScroll();
  }

  private void BeginInput(InputMode mode) {
    this._mode = mode;
    this._input = this._view.TextFilter ?? string.Empty;
  }

  private void ToggleUserFilter() {
    this._view.UserIdFilter = this._view.UserIdFilter is null ? CurrentUserId() : null;
    this.Say(
      this._view.UserIdFilter is null ? "showing every user" : "showing only your processes",
      Attributes.Accent
    );
  }

  private static int CurrentUserId() {
    // The uid of whatever this process is; the probe reports the same number for our own row.
    foreach (var name in (ReadOnlySpan<string>)["UID", "USER_ID"])
      if (int.TryParse(Environment.GetEnvironmentVariable(name), out var value))
        return value;

    return OperatingSystem.IsWindows() ? -1 : ReadOwnUid();
  }

  private static int ReadOwnUid() {
    try {
      foreach (var line in File.ReadLines("/proc/self/status")) {
        if (!line.StartsWith("Uid:", StringComparison.Ordinal))
          continue;

        var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 0 && int.TryParse(fields[0].Trim(), out var uid))
          return uid;
      }
    } catch (IOException) {
      // Falls through to the sentinel, which filters nothing.
    }

    return -1;
  }

  private void NextSortColumn() => this.SetSortColumn(1);

  private void PreviousSortColumn() => this.SetSortColumn(-1);

  private void SetSortColumn(int direction) {
    var columns = Layout.Columns;
    var index = Array.IndexOf(columns, this._view.SortColumn);
    index = ((index < 0 ? 0 : index) + direction + columns.Length) % columns.Length;
    this._view.SortColumn = columns[index];
    this._view.SortDescending = columns[index].PrefersDescending();
    this.Say($"sorted by {columns[index].ToHeader()}", Attributes.Accent);
  }

  private void BeginKill(bool tree) {
    if (this._selectedKey.IsNone) {
      this.Say("nothing selected", Attributes.Dim);
      return;
    }

    this._confirmTarget = this._selectedKey;
    this._confirmTree = tree;
    this._mode = InputMode.ConfirmKill;
  }

  private void KillTree(ProcessKey root) {
    // Children first: killing the parent first can reparent the children to init and lose them.
    var snapshot = this._sampler.Current;
    var order = new List<ProcessKey>();
    Collect(root.Pid);
    order.Reverse();

    var killed = 0;
    foreach (var key in order)
      if (this._actions!.Terminate(key).Succeeded)
        ++killed;

    this.Say($"sent SIGTERM to {killed} of {order.Count} processes", killed == order.Count ? Attributes.Good : Attributes.Warn);

    void Collect(int pid) {
      var processes = snapshot.Processes;
      for (var i = 0; i < processes.Length; ++i)
        if (processes[i].Pid == pid) {
          order.Add(processes[i].Key);
          break;
        }

      for (var i = 0; i < processes.Length; ++i)
        if (processes[i].ParentPid == pid && processes[i].Pid != pid)
          Collect(processes[i].Pid);
    }
  }

  /// <summary>
  /// Fills the handle column for the rows currently on screen. Not done automatically: on Linux the
  /// kernel builds one directory entry per descriptor, which is 85 µs per process (PRD §3.5).
  /// </summary>
  private void FillHandleCounts() {
    var processes = this._sampler.Current.Processes;
    var rows = this._view.Rows;
    var last = Math.Min(rows.Length, this._scrollOffset + this.ListHeight);
    for (var i = this._scrollOffset; i < last; ++i) {
      var key = processes[rows[i].Index].Key;
      this._handleCounts[key] = this._probe.GetHandleCount(key);
    }

    this.Say("handle counts read for the visible rows", Attributes.Accent);
  }

  private void Say(string message, byte attribute) {
    this._message = message;
    this._messageAttribute = attribute;
  }

  #endregion

  #region drawing

  private void Compose() {
    this._screen.BeginFrame();
    var y = this.DrawMeters();
    this.DrawColumnHeader(y);
    this.DrawRows(y + 1);
    this.DrawStatus();
  }

  private int DrawMeters() {
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;
    var width = this._screen.Width;
    var half = Math.Max(12, width / 2 - 1);
    var cores = delta.PerCoreCount;

    var y = 0;
    for (var core = 0; core < cores; core += 2) {
      this.DrawMeter(0, y, half, $"{core,3}", delta.PerCoreBusyPercent(core));
      if (core + 1 < cores)
        this.DrawMeter(half + 1, y, half, $"{core + 1,3}", delta.PerCoreBusyPercent(core + 1));

      ++y;
    }

    var system = snapshot.System;
    var used = system.TotalMemoryBytes.HasValue && system.AvailableMemoryBytes.HasValue
      ? system.TotalMemoryBytes.Value - Math.Min(system.TotalMemoryBytes.Value, system.AvailableMemoryBytes.Value)
      : 0;
    var memoryPercent = system.TotalMemoryBytes.HasValue && system.TotalMemoryBytes.Value > 0
      ? Rate.Of(used * 100d / system.TotalMemoryBytes.Value)
      : Rate.Gap;
    this.DrawMeter(0, y, half, "Mem", memoryPercent, $"{Humanize.Bytes(Counter.Of(used))}/{Humanize.Bytes(system.TotalMemoryBytes)}");

    var swapPercent = system.TotalSwapBytes.HasValue && system.TotalSwapBytes.Value > 0
      ? Rate.Of(system.UsedSwapBytes.GetValueOrDefault() * 100d / system.TotalSwapBytes.Value)
      : Rate.Gap;
    this.DrawMeter(half + 1, y, half, "Swp", swapPercent, $"{Humanize.Bytes(system.UsedSwapBytes)}/{Humanize.Bytes(system.TotalSwapBytes)}");
    ++y;

    var tasks = $"Tasks: {this._view.TotalCount}, {system.RunningProcesses} running";
    var load = $"Load average: {system.LoadAverage1:0.00} {system.LoadAverage5:0.00} {system.LoadAverage15:0.00}";
    var uptime = $"Uptime: {FormatUptime(system.UptimeSeconds)}";
    this._screen.Write(0, y, tasks, Attributes.Dim);
    this._screen.Write(Math.Min(width - 1, 32), y, load, Attributes.Dim);
    this._screen.Write(Math.Min(width - 1, 68), y, uptime, Attributes.Dim);
    return y + 2;
  }

  private void DrawMeter(int x, int y, int width, string label, Rate value, string? text = null) {
    this._screen.Write(x, y, label, Attributes.Accent);
    var barStart = x + label.Length + 1;
    var barWidth = Math.Max(4, width - label.Length - 3);
    this._screen.Write(barStart - 1, y, "[", Attributes.Dim);
    this._screen.Write(barStart + barWidth, y, "]", Attributes.Dim);

    if (!value.HasValue) {
      this._screen.Write(barStart, y, Humanize.Placeholder(value.Reason).PadRight(barWidth), Attributes.Dim);
      return;
    }

    var percent = Math.Clamp(value.Value, 0, 100);
    var filled = (int)Math.Round(percent * barWidth / 100);
    var attribute = percent >= 90 ? Attributes.Bad : percent >= 60 ? Attributes.Warn : Attributes.Good;
    this._screen.Fill(barStart, y, filled, '|', attribute);

    // The number sits inside the bar, right-aligned, the way htop does it: the bar answers "how
    // much" at a glance and the digits answer "exactly how much" when you look.
    var caption = text ?? Humanize.Percent(value) + "%";
    if (caption.Length < barWidth)
      this._screen.Write(barStart + barWidth - caption.Length, y, caption, Attributes.Dim);
  }

  private static string FormatUptime(double seconds) {
    var span = TimeSpan.FromSeconds(seconds);
    return span.TotalDays >= 1
      ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
      : $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
  }

  private void DrawColumnHeader(int y) {
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);
    var x = 0;
    foreach (var column in Layout.Columns) {
      var width = Layout.WidthOf(column);
      var header = column.ToHeader();
      if (column == this._view.SortColumn)
        header = this._view.SortDescending ? header + "▾" : header + "▴";

      // A header that does not fit loses its tail, not its head: "Working set" clipped to "ing set"
      // names nothing, where "Workin" is still recognisable. Values are the other way round, which
      // is why WriteRight cuts the front of those.
      if (header.Length > width)
        header = header[..width];

      if (Layout.IsRightAligned(column))
        this._screen.WriteRight(x, y, width, header, Attributes.Header);
      else
        this._screen.Write(x, y, header.Length > width ? header[..width] : header, Attributes.Header);

      x += width + 1;
      if (x >= this._screen.Width)
        break;
    }
  }

  private void DrawRows(int top) {
    var snapshot = this._sampler.Current;
    var delta = this._sampler.Delta;
    var rows = this._view.Rows;
    var height = this.ListHeight;

    for (var line = 0; line < height; ++line) {
      var rowIndex = this._scrollOffset + line;
      if (rowIndex >= rows.Length)
        break;

      var row = rows[rowIndex];
      ref readonly var process = ref snapshot.Processes[row.Index];
      var selected = rowIndex == this._selectedRow;
      var baseAttribute = selected
        ? Attributes.Selected
        : delta.IsNew(row.Index) ? Attributes.NewProcess : Attributes.Normal;

      if (selected)
        this._screen.Fill(0, top + line, this._screen.Width, ' ', Attributes.Selected);

      var x = 0;
      foreach (var column in Layout.Columns) {
        var width = Layout.WidthOf(column);
        var text = this.CellText(column, row, in process, delta);
        if (Layout.IsRightAligned(column))
          this._screen.WriteRight(x, top + line, width, text, baseAttribute);
        else
          this._screen.Write(x, top + line, text.Length > width ? text[..width] : text, baseAttribute);

        x += width + 1;
        if (x >= this._screen.Width)
          break;
      }
    }
  }

  private string CellText(ProcessColumn column, ViewRow row, in ProcessRecord process, SnapshotDelta delta)
    => column switch {
      ProcessColumn.Pid => process.Pid.ToString(CultureInfo.InvariantCulture),
      ProcessColumn.UserName => process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?"),
      ProcessColumn.PrivateBytes => Humanize.Bytes(process.PrivateBytes),
      ProcessColumn.WorkingSetBytes => Humanize.Bytes(process.WorkingSetBytes),
      ProcessColumn.State => Humanize.State(process.State),
      ProcessColumn.CpuPercent => Humanize.Percent(delta.CpuPercent(row.Index)),
      ProcessColumn.ReadBytesPerSecond => Humanize.BytesPerSecond(delta.ReadBytesPerSecond(row.Index)),
      ProcessColumn.WriteBytesPerSecond => Humanize.BytesPerSecond(delta.WriteBytesPerSecond(row.Index)),
      ProcessColumn.ThreadCount => process.ThreadCount.ToString(CultureInfo.InvariantCulture),
      ProcessColumn.HandleCount => Humanize.Count(this._handleCounts.TryGetValue(process.Key, out var handles) ? handles : process.HandleCount),
      ProcessColumn.Name => this._view.TreeMode
        ? new string(' ', Math.Min(row.Depth * 2, 32)) + (row.HasChildren ? "+ " : "  ") + process.Name
        : process.Name,
      ProcessColumn.CommandLine => process.CommandLine ?? string.Empty,
      _ => string.Empty,
    };

  private void DrawStatus() {
    var y = this._screen.Height - 1;
    this._screen.Fill(0, y, this._screen.Width, ' ', Attributes.Header);

    switch (this._mode) {
      case InputMode.Search or InputMode.Filter:
        this._screen.Write(0, y, $"Search: {this._input}_", Attributes.Header);
        return;
      case InputMode.ConfirmKill: {
        var name = this._sampler.Current.TryGetProcess(this._confirmTarget, out var record) ? record.Name : "?";
        var what = this._confirmTree ? "and every process under it" : "";
        this._screen.Write(0, y, $"Send SIGTERM to {name} ({this._confirmTarget.Pid}) {what}? y/N", Attributes.Header);
        return;
      }
    }

    var keys = "F5 tree  F6 sort  F9 kill  / search  u user  h handles  C cpu%  q quit";
    this._screen.Write(0, y, keys, Attributes.Header);

    if (this._message.Length > 0) {
      var cost = $"{this._sampler.LastSampleDuration.TotalMilliseconds:0.0} ms";
      this._screen.Write(0, y - 0, keys, Attributes.Header);
      this._screen.WriteRight(0, y, this._screen.Width - 1, $"{this._message}   {cost}", this._messageAttribute);
    } else
      this._screen.WriteRight(0, y, this._screen.Width - 1, $"{this._sampler.LastSampleDuration.TotalMilliseconds:0.0} ms", Attributes.Header);
  }

  #endregion

}
