using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>Which page of a process's detail is showing.</summary>
public enum DetailTab : byte { Overview, Threads, Modules, Handles, Environment, Network }

/// <summary>
/// The terminal's answer to a process-properties window: one process, in as much detail as the
/// platform will give (PRD §6.2, §11).
/// </summary>
/// <remarks>
/// Collected when the view is opened or its page is changed, and not on the sampling tick — the same
/// rule the desktop pane follows, and for the same reason: enumerating one process's handles is
/// expensive and enumerating every process's would be absurd (PRD §3.5).
/// </remarks>
public sealed class DetailView(ISystemProbe probe) {

  private static readonly DetailTab[] _tabs = Enum.GetValues<DetailTab>();

  private readonly List<string[]> _rows = [];
  private string[] _headers = [];
  private int[] _widths = [];
  private ProcessKey _key;
  private bool _stale = true;

  public DetailTab Tab { get; private set; }

  public int Scroll { get; private set; }

  public int RowCount => this._rows.Count;

  public void Open(ProcessKey key) {
    this._key = key;
    this._stale = true;
    this.Scroll = 0;
  }

  public void NextTab() {
    this.Tab = _tabs[(Array.IndexOf(_tabs, this.Tab) + 1) % _tabs.Length];
    this._stale = true;
    this.Scroll = 0;
  }

  public void PreviousTab() {
    this.Tab = _tabs[(Array.IndexOf(_tabs, this.Tab) - 1 + _tabs.Length) % _tabs.Length];
    this._stale = true;
    this.Scroll = 0;
  }

  public void ScrollBy(int delta, int pageHeight) {
    var maximum = Math.Max(0, this._rows.Count - pageHeight);
    this.Scroll = Math.Clamp(this.Scroll + delta, 0, maximum);
  }

  /// <summary>Re-collects the current page if it has been invalidated.</summary>
  public void Collect(in ProcessRecord process) {
    if (!this._stale)
      return;

    this._stale = false;
    this._rows.Clear();

    switch (this.Tab) {
      case DetailTab.Overview:
        this._headers = ["Field", "Value"];
        this._widths = [18, 100];
        this.AddOverview(in process);
        break;

      case DetailTab.Threads: {
        // The same fields as the window's thread tab and in the same order, because a field that
        // exists in one front-end and not the other is the thing §58 forbids.
        this._headers = [
          "TID", "Name", "S", "Started", "CPU time", "User", "Kernel", "Ctx", "Vol / invol",
          "CPU#", "Pri", "Base", "Policy", "Affinity", "Waiting on",
        ];
        this._widths = [8, 16, 6, 20, 10, 9, 9, 9, 14, 5, 4, 5, 14, 12, 28];
        foreach (var thread in probe.GetThreads(this._key))
          this._rows.Add([
            thread.Tid.ToString(CultureInfo.InvariantCulture),
            thread.Name ?? "—",
            Humanize.State(thread.State),
            Humanize.Timestamp(thread.StartTimeUtcTicks),
            Humanize.Duration(thread.CpuTimeNs),
            Humanize.Duration(thread.UserTimeNs),
            Humanize.Duration(thread.KernelTimeNs),
            Humanize.Count(thread.ContextSwitches),
            Humanize.Pair(thread.VoluntaryContextSwitches, thread.InvoluntaryContextSwitches),
            thread.LastCpu >= 0 ? thread.LastCpu.ToString(CultureInfo.InvariantCulture) : "—",
            thread.Priority.ToString(CultureInfo.InvariantCulture),
            thread.BasePriority?.ToString(CultureInfo.InvariantCulture) ?? "—",
            Humanize.SchedulingPolicy(thread.Policy),
            thread.Affinity ?? "—",
            // The wait reason last and widest: it is what somebody opened this page to find out.
            thread.WaitReason
              ?? (thread.StartAddress == 0 ? "—" : "0x" + thread.StartAddress.ToString("x", CultureInfo.InvariantCulture)),
          ]);

        break;
      }

      case DetailTab.Modules: {
        this._headers = ["Base", "Size", "Perm", "Path"];
        this._widths = [18, 9, 6, 90];
        foreach (var module in probe.GetModules(this._key))
          this._rows.Add([
            "0x" + module.BaseAddress.ToString("x", CultureInfo.InvariantCulture),
            Humanize.Bytes(module.Size),
            module.Permissions.Length > 0 ? module.Permissions : "—",
            module.Path,
          ]);

        break;
      }

      case DetailTab.Handles: {
        this._headers = ["Type", "Handle", "Name"];
        this._widths = [12, 8, 100];
        foreach (var handle in probe.GetHandles(this._key))
          this._rows.Add([
            handle.Kind.ToString(),
            handle.Handle.ToString(CultureInfo.InvariantCulture),
            handle.Name ?? "<not named>",
          ]);

        break;
      }

      case DetailTab.Environment: {
        this._headers = ["Variable", "Value"];
        this._widths = [28, 90];
        foreach (var (name, value) in probe.GetEnvironment(this._key))
          this._rows.Add([name, value]);

        break;
      }

      case DetailTab.Network: {
        this._headers = ["Proto", "Type", "Local", "Remote", "State", "User", "If", "Send-Q", "Recv-Q", "Retx"];
        this._widths = [6, 9, 24, 24, 12, 9, 8, 7, 7, 5];
        foreach (var connection in probe.GetConnections(this._key))
          this._rows.Add([
            connection.Protocol.ToString(),
            Humanize.SocketKindName(connection.Kind),
            Humanize.LocalEndpoint(connection),
            Humanize.RemoteEndpoint(connection),
            connection.State,
            Humanize.SocketUser(connection),
            connection.Interface ?? "—",
            Humanize.Bytes(connection.SendQueueBytes),
            Humanize.Bytes(connection.ReceiveQueueBytes),
            Humanize.Count(connection.Retransmits),
          ]);

        break;
      }
    }
  }

  private void AddOverview(in ProcessRecord process) {
    this._rows.Add(["name", process.Name]);
    this._rows.Add(["pid", process.Pid.ToString(CultureInfo.InvariantCulture)]);
    this._rows.Add(["parent", process.ParentPid.ToString(CultureInfo.InvariantCulture)]);
    this._rows.Add(["user", process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?")]);
    this._rows.Add(["state", Humanize.State(process.State)]);
    this._rows.Add(["session", process.SessionId.ToString(CultureInfo.InvariantCulture)]);
    this._rows.Add(["threads", process.ThreadCount.ToString(CultureInfo.InvariantCulture)]);
    this._rows.Add(["priority", $"{process.Priority} (nice {process.Nice})"]);
    this._rows.Add(["cpu time", Humanize.Duration(process.CpuTimeNs)]);
    this._rows.Add(["  user", Humanize.Duration(process.UserTimeNs)]);
    this._rows.Add(["  kernel", Humanize.Duration(process.KernelTimeNs)]);
    this._rows.Add(["private", Humanize.Bytes(process.PrivateBytes)]);
    this._rows.Add(["working set", Humanize.Bytes(process.WorkingSetBytes)]);
    this._rows.Add(["virtual", Humanize.Bytes(process.VirtualBytes)]);
    this._rows.Add(["swap", Humanize.Bytes(process.SwapBytes)]);
    this._rows.Add(["read", Humanize.Bytes(process.ReadBytes)]);
    this._rows.Add(["written", Humanize.Bytes(process.WriteBytes)]);
    this._rows.Add(["handles", Humanize.Count(process.HandleCount)]);
    this._rows.Add(["ctx switches", Humanize.Count(process.ContextSwitches)]);
    this._rows.Add(["started", process.StartTimeUtcTicks > 0
      ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
      : "—"]);
    this._rows.Add(["image", process.ImagePath ?? "—"]);
    this._rows.Add(["cgroup", process.ContainerPath ?? "—"]);
    this._rows.Add(["command", process.CommandLine ?? "—"]);
  }

  /// <summary>Draws the whole detail screen, tab strip included.</summary>
  public void Draw(TerminalScreen screen, in ProcessRecord process) {
    this.Collect(in process);

    var title = $" {process.Name} ({process.Pid}) ";
    screen.Fill(0, 0, screen.Width, ' ', Attributes.Header);
    screen.Write(0, 0, title, Attributes.Header);

    var x = title.Length + 2;
    foreach (var tab in _tabs) {
      var label = $" {tab} ";
      screen.Write(x, 0, label, tab == this.Tab ? Attributes.Selected : Attributes.Header);
      x += label.Length;
    }

    // Column headers.
    var y = 2;
    var columnX = 0;
    for (var i = 0; i < this._headers.Length; ++i) {
      screen.Write(columnX, y, this._headers[i], Attributes.Accent);
      columnX += this._widths[i] + 1;
    }

    var pageHeight = screen.Height - 4;
    if (this._rows.Count == 0) {
      // An empty page and a page we may not read look the same, so it says which (PRD §1.5).
      screen.Write(0, y + 2, "nothing to show — this process may not permit it, or has none", Attributes.Dim);
      return;
    }

    for (var line = 0; line < pageHeight; ++line) {
      var index = this.Scroll + line;
      if (index >= this._rows.Count)
        break;

      var row = this._rows[index];
      columnX = 0;
      for (var i = 0; i < row.Length && i < this._widths.Length; ++i) {
        var text = row[i];
        if (text.Length > this._widths[i])
          text = text[..this._widths[i]];

        screen.Write(columnX, y + 1 + line, text);
        columnX += this._widths[i] + 1;
      }
    }

    if (this._rows.Count > pageHeight)
      screen.WriteRight(0, y, screen.Width - 1, $"{this.Scroll + 1}–{Math.Min(this.Scroll + pageHeight, this._rows.Count)} of {this._rows.Count}", Attributes.Dim);
  }

}
