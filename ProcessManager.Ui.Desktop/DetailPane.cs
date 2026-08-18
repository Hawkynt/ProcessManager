using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The tabs under the process tree: what one process is doing, in the detail Process Explorer shows
/// (PRD §6.2).
/// </summary>
/// <remarks>
/// Every list here is filled <em>on demand</em> — when the selection changes, when the tab changes,
/// or when the user asks — and never on the sampling tick. Enumerating one process's handles means
/// duplicating each one into this process and asking the kernel to name it; doing that for every
/// process every second is how a monitor becomes the thing worth monitoring (PRD §3.5, §5.2).
/// </remarks>
public sealed class DetailPane {

  private readonly ISystemProbe _probe;
  private readonly TabControl _tabs = new();
  private readonly Label _overview = new();
  private readonly TreeListView _threads = new();
  private readonly TreeListView _modules = new();
  private readonly TreeListView _handles = new();
  private readonly TreeListView _environment = new();
  private readonly TreeListView _network = new();
  private readonly Label _hint = new();

  private ProcessKey _key;
  private bool _dirty = true;

  public DetailPane(ISystemProbe probe) {
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;

    this._tabs.Dock = DockStyle.Fill;
    this._overview.Dock = DockStyle.Fill;
    this._hint.Dock = DockStyle.Bottom;

    AddPage("Overview", this._overview);
    AddList(
      "Threads",
      this._threads,
      ("TID", 80),
      ("Name", 150),
      ("State", 70),
      ("CPU time", 100),
      ("User", 90),
      ("Kernel", 90),
      ("Ctx switches", 100),
      ("CPU#", 60),
      ("Priority", 70),
      // Last, and widest: it is a kernel symbol or a wait reason, and it is the column that answers
      // "why is this hanging" (PRD §2, §29).
      ("Waiting on", 200)
    );
    AddList("Modules", this._modules, ("Path", 520), ("Base", 140), ("Size", 100), ("Permissions", 100));
    AddList("Handles", this._handles, ("Type", 110), ("Handle", 90), ("Name", 640));
    AddList("Environment", this._environment, ("Variable", 220), ("Value", 700));
    AddList("Network", this._network, ("Protocol", 80), ("Local", 200), ("Remote", 200), ("State", 120));

    // Switching to a tab is the request to fill it; nothing is collected for a tab nobody looked at.
    this._tabs.SelectedIndexChanged += (_, _) => {
      this._dirty = true;
      this.Refresh();
    };

    void AddPage(string title, Control content) {
      var page = new TabPage(title);
      page.Controls.Add(content);
      this._tabs.TabPages.Add(page);
    }

    void AddList(string title, TreeListView list, params (string Header, int Width)[] columns) {
      list.Dock = DockStyle.Fill;
      list.ShowColumnHeaders = true;
      for (var i = 0; i < columns.Length; ++i) {
        var column = i;
        list.Columns.Add(new(columns[i].Header, columns[i].Width, node => ((string[])node.Tag!)[column]));
      }

      AddPage(title, list);
    }
  }

  /// <summary>The control to add to the form.</summary>
  public Control Control => this._tabs;

  /// <summary>Points the pane at a process. Cheap; the lists fill on the next <see cref="Refresh"/>.</summary>
  public void Select(ProcessKey key) {
    if (this._key == key)
      return;

    this._key = key;
    this._dirty = true;
  }

  /// <summary>
  /// Describes the selected process in the overview tab. Called every sample, because the numbers
  /// here come from the snapshot that was just taken and cost nothing.
  /// </summary>
  public void UpdateOverview(in ProcessRecord process, ProcessRow row) {
    this._overview.Text =
      $"{process.Name} ({process.Pid})    parent {process.ParentPid}    user {row.User}    session {process.SessionId}\n"
      + $"state {row.State}    priority {process.Priority}    nice {process.Nice}    started {row.Started}\n"
      + "\n"
      + $"CPU {row.Cpu} %    time {Humanize.Duration(process.CpuTimeNs)}    threads {row.Threads}    handles {row.Handles}\n"
      + $"private {row.Private}    working set {row.WorkingSet}    virtual {Humanize.Bytes(process.VirtualBytes)}    swap {Humanize.Bytes(process.SwapBytes)}\n"
      + $"read {row.Read}    write {row.Write}    context switches {Humanize.Count(process.ContextSwitches)}\n"
      + "\n"
      + $"image    {process.ImagePath ?? "—"}\n"
      + $"cgroup   {process.ContainerPath ?? "—"}\n"
      + $"command  {(row.CommandLine.Length > 0 ? row.CommandLine : "—")}";
  }

  /// <summary>Fills whichever tab is showing, if it needs it.</summary>
  public void Refresh() {
    if (!this._dirty || this._key.IsNone)
      return;

    this._dirty = false;
    var selected = this._tabs.SelectedTab?.Text;
    switch (selected) {
      case "Threads": this.FillThreads(); break;
      case "Modules": this.FillModules(); break;
      case "Handles": this.FillHandles(); break;
      case "Environment": this.FillEnvironment(); break;
      case "Network": this.FillNetwork(); break;
      default: break;
    }
  }

  /// <summary>Forces the visible tab to be collected again.</summary>
  public void Invalidate() {
    this._dirty = true;
    this.Refresh();
  }

  private void FillThreads() {
    var threads = this._probe.GetThreads(this._key);
    Fill(this._threads, threads.Count, i => [
      threads[i].Tid.ToString(CultureInfo.InvariantCulture),
      threads[i].Name ?? "—",
      Humanize.State(threads[i].State),
      Humanize.Duration(threads[i].CpuTimeNs),
      Humanize.Duration(threads[i].UserTimeNs),
      Humanize.Duration(threads[i].KernelTimeNs),
      Humanize.Count(threads[i].ContextSwitches),
      threads[i].LastCpu >= 0 ? threads[i].LastCpu.ToString(CultureInfo.InvariantCulture) : "—",
      threads[i].Priority.ToString(CultureInfo.InvariantCulture),
      threads[i].WaitReason
        ?? threads[i].StartSymbol
        ?? (threads[i].StartAddress == 0 ? "—" : "0x" + threads[i].StartAddress.ToString("x", CultureInfo.InvariantCulture)),
    ]);
  }

  private void FillModules() {
    var modules = this._probe.GetModules(this._key);
    Fill(this._modules, modules.Count, i => [
      modules[i].Path,
      "0x" + modules[i].BaseAddress.ToString("x", CultureInfo.InvariantCulture),
      Humanize.Bytes(modules[i].Size),
      modules[i].Permissions.Length > 0 ? modules[i].Permissions : "—",
    ]);
  }

  private void FillHandles() {
    var handles = this._probe.GetHandles(this._key);
    Fill(this._handles, handles.Count, i => [
      handles[i].Kind.ToString(),
      handles[i].Handle.ToString(CultureInfo.InvariantCulture),
      // A handle the kernel would not name is a normal outcome on Windows, not a failure — see
      // HandleNameResolver. Saying so beats a blank cell nobody can interpret.
      handles[i].Name ?? "<not named>",
    ]);
  }

  private void FillEnvironment() {
    var variables = this._probe.GetEnvironment(this._key);
    Fill(this._environment, variables.Count, i => [variables[i].Key, variables[i].Value]);
  }

  private void FillNetwork() {
    var connections = this._probe.GetConnections(this._key);
    Fill(this._network, connections.Count, i => [
      connections[i].Protocol.ToString(),
      $"{connections[i].LocalAddress}:{connections[i].LocalPort}",
      connections[i].RemotePort == 0 ? "—" : $"{connections[i].RemoteAddress}:{connections[i].RemotePort}",
      connections[i].State,
    ]);
  }

  private static void Fill(TreeListView list, int count, Func<int, string[]> row) {
    list.Nodes.Clear();
    if (count == 0) {
      // An empty list and a list we were not allowed to read look identical, so the empty case says
      // which it is rather than leaving the reader to guess (PRD §1.5).
      list.Nodes.Add(new TreeNode("nothing to show — the process may not permit this, or has none") {
        Tag = new[] { "nothing to show — the process may not permit this, or has none", "", "", "", "" },
      });

      return;
    }

    for (var i = 0; i < count; ++i) {
      var cells = row(i);
      list.Nodes.Add(new TreeNode(cells[0]) { Tag = cells });
    }
  }

}
