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

  /// <summary>
  /// What may be done to a thread, or null in a read-only front-end.
  /// </summary>
  /// <remarks>
  /// Optional for the same reason <see cref="Abstractions.IProcessActions"/> is a separate interface
  /// from the probe: a pane that only shows things must be constructible without the ability to
  /// change any of them.
  /// </remarks>
  public Abstractions.IProcessActions? Actions { get; set; }

  /// <summary>The process the thread rows belong to, for the identity check the actions make.</summary>
  private ProcessKey Key => this._key;

  public DetailPane(ISystemProbe probe) {
    this._threads.ContextMenuStrip = this.BuildThreadMenu();
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
      ("Started", 140),
      ("CPU time", 100),
      ("User", 90),
      ("Kernel", 90),
      ("Ctx switches", 100),
      // The split, next to the total it adds up to: voluntary switches are a thread waiting for
      // something and involuntary ones are a thread being pushed off a contended processor, and the
      // sum on its own cannot tell those apart (PRD §29).
      ("Vol / invol", 110),
      ("CPU#", 60),
      ("Priority", 70),
      ("Base", 60),
      ("Policy", 120),
      ("Affinity", 110),
      // Last, and widest: it is a kernel symbol or a wait reason, and it is the column that answers
      // "why is this hanging" (PRD §2, §29).
      ("Waiting on", 200)
    );
    AddList("Modules", this._modules, ("Path", 520), ("Base", 140), ("Size", 100), ("Permissions", 100));
    AddList("Handles", this._handles, ("Type", 110), ("Handle", 90), ("Name", 640));
    AddList("Environment", this._environment, ("Variable", 220), ("Value", 700));
    AddList(
      "Network",
      this._network,
      ("Protocol", 70),
      // Only a Unix socket needs this, and for a Unix socket it is the difference between two
      // endpoints on the same path (PRD §40).
      ("Type", 70),
      ("Local", 200),
      ("Remote", 200),
      ("State", 110),
      ("User", 90),
      ("Interface", 80),
      // Send and receive queue: what the peer has not acknowledged, and what this process has not
      // read. The pair says which end of a stalled connection is the slow one.
      ("Send-Q", 70),
      ("Recv-Q", 70),
      ("Retrans", 70)
    );

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

  /// <summary>
  /// Priority and affinity for one thread (PRD §26).
  /// </summary>
  /// <remarks>
  /// Built once and hung on the thread list. The tid comes from the selected row rather than being
  /// captured when the menu was made, because the list is refilled while the menu exists.
  /// </remarks>
  private ContextMenuStrip BuildThreadMenu() {
    var menu = new ContextMenuStrip();
    var priority = new ToolStripMenuItem("Thread priority");
    foreach (var (label, nice) in new[] { ("High (-10)", -10), ("Normal (0)", 0), ("Low (10)", 10), ("Idle (19)", 19) }) {
      var value = nice;
      priority.DropDownItems.Add(ThreadItem(label, tid => this.Actions!.SetThreadPriority(this.Key, tid, value)));
    }

    menu.Items.Add(priority);
    menu.Items.Add(ThreadItem("Set thread affinity…", this.ChooseThreadAffinity));
    return menu;
  }

  private ToolStripMenuItem ThreadItem(string text, Func<int, ActionResult> action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => {
      if (this.Actions is null || this.SelectedThread is not { } tid)
        return;

      var result = action(tid);
      if (!result.Succeeded)
        MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");
    };

    return item;
  }

  /// <summary>The tid of the selected thread row, or null when nothing is selected.</summary>
  private int? SelectedThread
    => this._threads.SelectedNode is { } node
      && int.TryParse(node.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid)
        ? tid
        : null;

  private ActionResult ChooseThreadAffinity(int tid) {
    var chooser = new AffinityChooser($"thread {tid}", this._key.Pid, Math.Max(1, Environment.ProcessorCount), CpuTopology.Empty);
    chooser.ShowDialog();
    return chooser.Accepted
      ? this.Actions!.SetThreadAffinity(this._key, tid, chooser.Mask)
      : ActionResult.Ok;
  }

  private void FillThreads() {
    var threads = this._probe.GetThreads(this._key);
    Fill(this._threads, threads.Count, i => [
      threads[i].Tid.ToString(CultureInfo.InvariantCulture),
      threads[i].Name ?? "—",
      Humanize.State(threads[i].State),
      Humanize.Timestamp(threads[i].StartTimeUtcTicks),
      Humanize.Duration(threads[i].CpuTimeNs),
      Humanize.Duration(threads[i].UserTimeNs),
      Humanize.Duration(threads[i].KernelTimeNs),
      Humanize.Count(threads[i].ContextSwitches),
      Humanize.Pair(threads[i].VoluntaryContextSwitches, threads[i].InvoluntaryContextSwitches),
      threads[i].LastCpu >= 0 ? threads[i].LastCpu.ToString(CultureInfo.InvariantCulture) : "—",
      threads[i].Priority.ToString(CultureInfo.InvariantCulture),
      threads[i].BasePriority?.ToString(CultureInfo.InvariantCulture) ?? "—",
      Humanize.SchedulingPolicy(threads[i].Policy),
      threads[i].Affinity ?? "—",
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
      Humanize.SocketKindName(connections[i].Kind),
      Humanize.LocalEndpoint(connections[i]),
      Humanize.RemoteEndpoint(connections[i]),
      connections[i].State,
      Humanize.SocketUser(connections[i]),
      connections[i].Interface ?? "—",
      Humanize.Bytes(connections[i].SendQueueBytes),
      Humanize.Bytes(connections[i].ReceiveQueueBytes),
      Humanize.Count(connections[i].Retransmits),
    ]);
  }

  private static void Fill(TreeListView list, int count, Func<int, string[]> row) {
    list.Nodes.Clear();
    if (count == 0) {
      // An empty list and a list we were not allowed to read look identical, so the empty case says
      // which it is rather than leaving the reader to guess (PRD §1.5).
      const string EMPTY = "nothing to show — the process may not permit this, or has none";
      // One cell per column, counted from the list rather than written out: every column accessor
      // indexes this array, so a fixed five cells threw the moment a list grew a sixth column.
      var cells = new string[Math.Max(1, list.Columns.Count)];
      Array.Fill(cells, string.Empty);
      cells[0] = EMPTY;
      list.Nodes.Add(new TreeNode(EMPTY) { Tag = cells });
      return;
    }

    for (var i = 0; i < count; ++i) {
      var cells = row(i);
      list.Nodes.Add(new TreeNode(cells[0]) { Tag = cells });
    }
  }

}
