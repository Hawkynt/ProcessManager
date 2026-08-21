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
public sealed class DetailPane : IDisposable {

  private readonly ISystemProbe _probe;
  private readonly TabControl _tabs = new();
  private readonly Label _overview = new();
  private readonly TreeListView _threads = new();
  private readonly TreeListView _modules = new();
  private readonly TreeListView _handles = new();
  private readonly Label _handleSummary = new();
  private readonly TreeListView _environment = new();
  private readonly TreeListView _network = new();
  private readonly Label _hint = new();

  /// <summary>
  /// One socket's byte totals against the last reading of the same socket, which is the only way to
  /// a rate: the kernel publishes totals and never a rate (PRD §40).
  /// </summary>
  private readonly ConnectionRates _rates = new();

  /// <summary>
  /// Addresses to names, off until somebody asks for it in the tab's own menu.
  /// </summary>
  /// <remarks>
  /// Constructed either way and disabled, so that turning it on is a property assignment rather than
  /// a thread start in the middle of a redraw. Nothing here ever waits for it (PRD §40).
  /// </remarks>
  private readonly HostnameCache _hostnames = new();

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
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;
    this._threads.ContextMenuStrip = this.BuildThreadMenu();
    this._modules.ContextMenuStrip = this.BuildModuleMenu();

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
    AddList(
      "Modules",
      this._modules,
      ("Path", 380),
      ("Base", 130),
      ("End", 130),
      ("Size", 80),
      ("Resident", 80),
      ("Perm", 56),
      ("Type", 100),
      ("Arch", 80),
      ("SONAME", 160),
      ("Offset", 90),
      ("Device", 62),
      ("Inode", 90),
      ("File size", 80),
      ("Modified", 130),
      ("Maps", 50),
      ("Interpreter", 220)
    );
    AddList(
      "Handles",
      this._handles,
      ("Type", 110),
      ("FD", 60),
      ("Access", 60),
      ("Position", 90),
      ("Inode", 100),
      ("Endpoint", 200),
      ("Flags", 220),
      ("Name", 520)
    );

    // The per-kind tally of §20 sits above the descriptors it was counted from rather than becoming
    // a process-table column: the count is free, the enumeration behind it is 85 µs a process, and
    // making it a column would put that back in the sample loop (PRD §5.4, §71.2).
    //
    // Added after the list, because docked children claim their edge in reverse order and a Fill
    // child always takes what is left — so the label gets its band and the list keeps the rest.
    this._handleSummary.Dock = DockStyle.Top;
    this._handleSummary.Height = 20;
    this._tabs.TabPages[^1].Controls.Add(this._handleSummary);
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
      ("Retrans", 70),
      // From here on it is the socket diagnostics rather than the tables — bytes, segments, latency
      // and the lifetime retransmission count, none of which /proc/net/tcp has a column for.
      ("Sent", 80),
      ("Received", 80),
      ("Send rate", 90),
      ("Recv rate", 90),
      ("Pkts out", 80),
      ("Pkts in", 80),
      ("RTT", 80),
      ("Retrans total", 96),
      ("Service", 180),
      ("Cgroup", 220)
    );

    this._network.ContextMenuStrip = this.BuildNetworkMenu();

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

  /// <summary>
  /// Stops the name-lookup thread.
  /// </summary>
  /// <remarks>
  /// It is a background thread and would not hold the program open on its own, so this is tidiness
  /// rather than a leak — but a pane that owns something disposable and cannot be disposed is the
  /// kind of thing that stops being true the moment somebody makes a second one.
  /// </remarks>
  public void Dispose() => this._hostnames.Dispose();

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

  /// <summary>
  /// What can be done with a loaded image (PRD §25.6).
  /// </summary>
  /// <remarks>
  /// All of it is about the file on disk rather than the mapping, and none of it changes anything.
  /// Unloading a module is deliberately absent: on Linux there is no supported way to make another
  /// process drop a shared object, and an item that could only ever refuse is a lie dressed as a
  /// feature (PRD §32).
  /// </remarks>
  private ContextMenuStrip BuildModuleMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(ModuleItem("Copy path", path => Clipboard.SetText(path)));
    menu.Items.Add(ModuleItem("Open folder", this.RevealModule));
    menu.Items.Add(ModuleItem("File properties…", path => new FilePropertiesDialog(path, this.ModuleFacts(path), this.Actions).ShowDialog()));
    return menu;
  }

  private ToolStripMenuItem ModuleItem(string text, Action<string> action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => {
      if (this.SelectedModulePath is { } path)
        action(path);
    };

    return item;
  }

  /// <summary>
  /// The path in the selected module row, or null when nothing is selected.
  /// </summary>
  /// <remarks>
  /// The suffix the modules view adds to a deleted image is stripped here rather than being carried
  /// into a file name. It is a warning printed beside the path — the image is still mapped and still
  /// running while the file on disk is gone — and it was never part of the path itself (PRD §31).
  /// </remarks>
  private string? SelectedModulePath {
    get {
      if (this._modules.SelectedNode?.Text is not { Length: > 0 } text)
        return null;

      const string DeletedSuffix = "  (deleted)";
      return text.EndsWith(DeletedSuffix, StringComparison.Ordinal) ? text[..^DeletedSuffix.Length] : text;
    }
  }

  /// <summary>What the modules view already read about this image, so the dialog need not read it again.</summary>
  private List<KeyValuePair<string, string>> ModuleFacts(string path) {
    var extra = new List<KeyValuePair<string, string>>();
    foreach (var module in this._probe.GetModules(this._key)) {
      if (!string.Equals(module.Path, path, StringComparison.Ordinal))
        continue;

      extra.Add(new("image type", Humanize.ImageType(module.Type)));
      extra.Add(new("architecture", module.Architecture ?? "—"));
      extra.Add(new("soname", module.Soname ?? "—"));
      extra.Add(new("mapped at", Humanize.Address(module.BaseAddress)));
      extra.Add(new("mappings", module.MappingCount.ToString(CultureInfo.InvariantCulture)));
      break;
    }

    return extra;
  }

  private void RevealModule(string path) {
    if (this.Actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (Abstractions.DesktopOpen.Reveal(path) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the folder to.", "Process Manager");
      return;
    }

    var result = this.Actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
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
      // A deleted image is still mapped and still running; saying so on the path is the only warning
      // that the file on disk is no longer the code in memory (PRD §31).
      modules[i].IsDeleted ? modules[i].Path + "  (deleted)" : modules[i].Path,
      Humanize.Address(modules[i].BaseAddress),
      Humanize.Address(modules[i].EndAddress),
      Humanize.Bytes(modules[i].Size),
      Humanize.Bytes(modules[i].ResidentBytes),
      // The kernel's own four characters: read, write, execute, and shared-or-private. Three of §31's
      // flags in one column, in the notation anybody who has read a maps file already knows.
      modules[i].Permissions.Length > 0 ? modules[i].Permissions : "—",
      Humanize.ImageType(modules[i].Type),
      modules[i].Architecture ?? "—",
      modules[i].Soname ?? "—",
      Humanize.Address(modules[i].FileOffset),
      modules[i].Device ?? "—",
      Humanize.Count(modules[i].Inode),
      Humanize.Bytes(modules[i].FileSizeBytes),
      Humanize.Timestamp(modules[i].FileModifiedUtcTicks),
      modules[i].MappingCount.ToString(CultureInfo.InvariantCulture),
      modules[i].Interpreter ?? "—",
    ]);
  }

  private void FillHandles() {
    var handles = this._probe.GetHandles(this._key);

    // The socket join, done once for the list rather than once per row: the five network tables are
    // read whole either way, and a per-row lookup would read them once per socket the process holds.
    var endpoints = new Dictionary<ulong, string>();
    foreach (var connection in this._probe.GetConnections(this._key))
      endpoints[connection.Inode] = connection.RemotePort == 0
        ? $"{Humanize.LocalEndpoint(connection)} {connection.State}"
        : $"{Humanize.LocalEndpoint(connection)} → {Humanize.RemoteEndpoint(connection)}";

    this._handleSummary.Text = HandleTally.From(handles).Describe();
    Fill(this._handles, handles.Count, i => [
      Humanize.ResourceKind(handles[i].Kind),
      handles[i].Handle.ToString(CultureInfo.InvariantCulture),
      handles[i].Access ?? "—",
      Humanize.Count(handles[i].Position),
      Humanize.Count(handles[i].Inode),
      handles[i].Inode.TryGetValue(out var inode) && endpoints.TryGetValue(inode, out var endpoint)
        ? endpoint
        // A socket with no row in the five tables closed between the two reads — and every other kind
        // of descriptor has no endpoint at all, which is the same dash for a different reason.
        : "—",
      DescriptorParser.DescribeFlags(handles[i].OpenFlags) ?? Humanize.Placeholder(handles[i].OpenFlags.Reason),
      // A handle the kernel would not name is a normal outcome on Windows, not a failure — see
      // HandleNameResolver. Saying so beats a blank cell nobody can interpret.
      handles[i].TargetPid.TryGetValue(out var target)
        ? $"{handles[i].Name} → pid {target}"
        : handles[i].Name ?? "<not named>",
    ]);
  }

  private void FillEnvironment() {
    var variables = this._probe.GetEnvironment(this._key);
    Fill(this._environment, variables.Count, i => [variables[i].Key, variables[i].Value]);
  }

  private void FillNetwork() {
    var connections = new List<ConnectionRecord>(this._probe.GetConnections(this._key));

    // The rates come from this reading against the last one of the same socket. The tab is filled on
    // demand rather than every tick, so the interval is however long the reader took to look again —
    // which is why it is measured rather than assumed (PRD §40).
    this._rates.Observe(connections, DateTime.UtcNow.Ticks);

    // Asked for every address on every fill, and answered only for the ones already known. Nothing
    // here waits: a name that is not back yet shows the address, and the next fill shows the name
    // (PRD §40).
    var hosts = this._hostnames;
    Fill(this._network, connections.Count, i => {
      var connection = connections[i];
      var statistics = connection.Statistics;
      return [
        connection.Protocol.ToString(),
        Humanize.SocketKindName(connection.Kind),
        Humanize.LocalEndpoint(connection, null, hosts),
        Humanize.RemoteEndpoint(connection, null, hosts),
        connection.State,
        Humanize.SocketUser(connection),
        connection.Interface ?? "—",
        Humanize.Bytes(connection.SendQueueBytes),
        Humanize.Bytes(connection.ReceiveQueueBytes),
        Humanize.Count(connection.Retransmits),
        Humanize.Bytes(statistics.BytesSent),
        Humanize.Bytes(statistics.BytesReceived),
        Humanize.BytesPerSecond(connection.SendRate),
        Humanize.BytesPerSecond(connection.ReceiveRate),
        Humanize.Count(statistics.PacketsSent),
        Humanize.Count(statistics.PacketsReceived),
        Humanize.RoundTrip(statistics.RoundTripTimeMicroseconds),
        Humanize.Count(statistics.TotalRetransmits),
        connection.OwningService ?? "—",
        connection.ContainerPath ?? "—",
      ];
    });
  }

  /// <summary>
  /// What can be done with a connection (PRD §40).
  /// </summary>
  /// <remarks>
  /// Deliberately short. "Go to process" and "process properties" are absent because this tab shows
  /// one process's own sockets and the owner is already the selected row of the tree; closing a
  /// connection is absent because Linux offers it only to a process holding <c>CAP_NET_ADMIN</c>,
  /// and an item that could only ever refuse is a lie dressed as a feature (PRD §32).
  /// </remarks>
  private ContextMenuStrip BuildNetworkMenu() {
    var menu = new ContextMenuStrip();
    var copy = new ToolStripMenuItem("Copy endpoint");
    copy.Click += (_, _) => {
      if (this.SelectedEndpoint is { } endpoint)
        Clipboard.SetText(endpoint);
    };

    menu.Items.Add(copy);

    // Checkable rather than a one-shot "resolve this row": the disclosure is the same either way —
    // asking a resolver tells whoever runs it which addresses this machine is talking to — so it is
    // one deliberate act with a visible state rather than a per-row habit (PRD §40).
    this._resolveNames.Click += (_, _) => {
      this._resolveNames.Checked = !this._resolveNames.Checked;
      this._hostnames.Enabled = this._resolveNames.Checked;
      this.Invalidate();
    };

    menu.Items.Add(this._resolveNames);
    var terminate = new ToolStripMenuItem("Terminate owner…");
    terminate.Click += (_, _) => this.TerminateOwner();
    menu.Items.Add(terminate);
    return menu;
  }

  private readonly ToolStripMenuItem _resolveNames = new("Resolve hostnames");

  /// <summary>
  /// The local and remote endpoint of the selected row, as one line worth pasting into a search.
  /// </summary>
  /// <remarks>
  /// Taken from the drawn cells rather than re-read from the kernel: what the reader asked to copy
  /// is what they are looking at, and re-reading would sometimes copy a different socket — the table
  /// is a moment old and a connection can close between a right-click and a menu choice.
  /// </remarks>
  private string? SelectedEndpoint {
    get {
      if (this._network.SelectedNode?.Tag is not string[] cells || cells.Length < 4)
        return null;

      var local = cells[2];
      var remote = cells[3];
      return remote is "—" or "" ? local : $"{local} → {remote}";
    }
  }

  /// <summary>
  /// Ends the process whose sockets these are, which is the selected process and no other.
  /// </summary>
  /// <remarks>
  /// It asks first, because ending a process loses whatever it had not written and there is no undo
  /// (PRD §5.5). The key is the pane's own rather than anything read off a row: every row here
  /// belongs to the same process by construction, and taking a pid out of a table would make the
  /// action depend on which cell happened to be selected.
  /// </remarks>
  private void TerminateOwner() {
    if (this.Actions is null || this._key.IsNone)
      return;

    var confirm = MessageBox.Show(
      $"End process {this._key.Pid}? Anything it has not saved is lost.",
      "Process Manager",
      MessageBoxButtons.YesNo
    );

    if (confirm != DialogResult.Yes)
      return;

    var result = this.Actions.Terminate(this._key);
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");
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
