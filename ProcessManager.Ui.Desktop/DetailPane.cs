using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

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
  /// The per-thread rates, which need two readings of the same thread (PRD §29).
  /// </summary>
  /// <remarks>
  /// Owned by the pane rather than by the probe, because a probe reports counters and nothing else —
  /// there is one place in the program where a division happens, and this is a caller of it
  /// (PRD §2, §3.2).
  /// </remarks>
  private readonly ThreadDelta _threadRates = new();

  /// <summary>
  /// What the thread list is currently showing, for the actions that act on a row.
  /// </summary>
  /// <remarks>
  /// The rows themselves carry text. An action needs the reading behind it — the module a start
  /// address is in is a path, and the cell shows the file name — so the records are kept beside the
  /// list rather than parsed back out of it.
  /// </remarks>
  private IReadOnlyList<ThreadRecord> _threadRows = [];

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
    // Ordered by what a reader came for, not by what the kernel happens to write first, and pairs
    // that are always read together share a cell. Both are forced by the control: a TreeListView
    // scrolls up and down and not sideways, so a column past the right-hand edge is not merely
    // off-screen, it is unreachable. The twenty-one below come to about 1800 pixels, which fits a
    // maximised window on an ordinary screen; the ones a narrow window cuts off are the ones nobody
    // opens this tab to find (PRD §29).
    AddList(
      "Threads",
      this._threads,
      ("TID", 56),
      // Linux caps a thread's comm at fifteen characters, so this is as wide as a name can be. At
      // 120 the capture ran ".NET Tiered Com" into the state beside it.
      ("Name", 134),
      ("State", 54),
      // Which side of the user/kernel boundary the thread is on, and which system call it is in when
      // the machine will say. Two of §29's items in one cell, because "kernel" and "kernel, in call
      // 202" are the same answer at two levels of detail.
      // Wide enough for "kernel · 202" with a gap after it. At 82 the capture showed
      // "kernel · 2020.0", which is this cell and the next one with nothing between them.
      ("Mode", 100),
      ("CPU %", 54),
      ("Ctx/s", 52),
      // Seventh rather than last, which is where it used to sit and be cut off: it is a kernel symbol
      // naming what the thread is blocked in, and it answers "why is this hanging" — §2's first
      // question — without a stack walk. §29 says it earns its place above the rest; this is that
      // place.
      // As wide as "poll_schedule_timeout", which is the longest wait channel a desktop process is
      // routinely parked in and the one the first two captures cut in half.
      ("Waiting on", 176),
      ("CPU time", 72),
      // The user and kernel halves in one cell. Nobody reads one without the other, and a thread
      // whose time is all kernel is a different animal from one whose time is all its own.
      ("User / kernel", 100),
      ("Ctx switches", 94),
      // The split, next to the total it adds up to: voluntary switches are a thread waiting for
      // something and involuntary ones are a thread being pushed off a contended processor, and the
      // sum on its own cannot tell those apart (PRD §29).
      ("Vol / invol", 88),
      // How long this thread has been kept off a processor by other threads, ever. Not a wait
      // duration and not labelled as one — see §29.
      ("Queued", 74),
      ("CPU#", 44),
      // The effective priority and the one the thread was given. Only the pair says whether a busy
      // thread is being polite or was simply never asked to be.
      ("Prio / base", 82),
      // "SCHED_OTHER" is eleven characters and the kernel's own name for it (PRD §5.3), so the
      // column is as wide as the vocabulary and not as wide as the header.
      ("Policy", 112),
      ("Affinity", 86),
      ("Stack", 58),
      ("Instruction", 112),
      ("Start address", 104),
      ("Start module", 132),
      ("Started", 108)
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

  /// <summary>
  /// Fills whichever tab is showing, if it needs it.
  /// </summary>
  /// <remarks>
  /// The thread tab needs it every time. Its CPU and switch columns are differences between two
  /// readings, so a list filled once and left alone would show a percentage of an interval that
  /// ended when the process was selected — which is a number that gets less true the longer somebody
  /// looks at it. The other tabs stay on demand: enumerating a process's descriptors costs 85 µs and
  /// its modules a walk of the page table, and neither answer moves while it is being read
  /// (PRD §5.4, §29).
  /// </remarks>
  public void Refresh() {
    if (this._key.IsNone)
      return;

    var selected = this._tabs.SelectedTab?.Text;
    if (selected == "Threads") {
      this._dirty = false;
      this.FillThreads();
      return;
    }

    if (!this._dirty)
      return;

    this._dirty = false;
    switch (selected) {
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
  /// Brings one tab to the front and fills it.
  /// </summary>
  /// <returns>False when there is no tab by that name.</returns>
  /// <remarks>
  /// For the capture, which has to photograph a tab nobody clicked on: the pane opens on the overview
  /// and every list behind it is a layout no picture has ever shown (PRD §9.6).
  /// </remarks>
  public bool ShowTab(string tab) {
    for (var i = 0; i < this._tabs.TabPages.Count; ++i)
      if (this._tabs.TabPages[i].Text == tab) {
        this._tabs.SelectedIndex = i;
        this.Invalidate();
        return true;
      }

    return false;
  }

  /// <summary>What the visible tab holds, for a capture log with no display to read it off.</summary>
  public string DescribeForCapture() {
    var tab = this._tabs.SelectedTab?.Text ?? "none";
    var list = this._tabs.SelectedTab?.Controls.Count > 0
      ? this._tabs.SelectedTab.Controls[0] as TreeListView
      : null;

    return $"detail tab:   {tab}, {list?.Nodes.Count ?? 0} row(s), {list?.Columns.Count ?? 0} columns\n";
  }

  /// <summary>The thread rows the pane last drew, so the capture can open a stack on one of them.</summary>
  public IReadOnlyList<ThreadRecord> ThreadRows => this._threadRows;

  /// <summary>Opens the stack viewer on a thread, for the capture and for a test.</summary>
  public StackWindow OpenStack(int threadId, bool resolveSymbols) {
    var window = new StackWindow(this._probe, this._key, threadId, this.Thread(threadId)?.Name);
    window.Show();
    window.Reload(resolveSymbols);
    return window;
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

    // The rest need no privilege and no IProcessActions: they read, copy and show. That is why they
    // are added unconditionally, while the two above are inert in a pane holding no actions.
    menu.Items.Add(ReadOnlyThreadItem("View stack…", tid => this.ShowStack(tid, resolveSymbols: false)));
    menu.Items.Add(ReadOnlyThreadItem("View stack with symbols…", tid => this.ShowStack(tid, resolveSymbols: true)));
    menu.Items.Add(ReadOnlyThreadItem("Save stack…", this.SaveStack));
    menu.Items.Add(ReadOnlyThreadItem("Go to start module", this.GoToStartModule));
    menu.Items.Add(ReadOnlyThreadItem("Copy row", tid => Clipboard.SetText(this.DescribeThread(tid))));

    // Not a row action: this one is about the list, and works with nothing selected.
    var all = new ToolStripMenuItem("Copy all threads");
    all.Click += (_, _) => Clipboard.SetText(this.DescribeThreads());
    menu.Items.Add(all);
    return menu;
  }

  /// <summary>
  /// A thread action that changes nothing, and so needs no <see cref="IProcessActions"/>.
  /// </summary>
  /// <remarks>
  /// <see cref="ThreadItem"/> bails when the pane holds no actions, which is right for the two items
  /// that alter a thread and wrong for the five that look at one: a read-only front-end must still be
  /// able to show a stack (PRD §26).
  /// </remarks>
  private ToolStripMenuItem ReadOnlyThreadItem(string text, Action<int> action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => {
      if (this.SelectedThread is { } tid)
        action(tid);
    };

    return item;
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

  /// <summary>The row for a tid, or null when the list has moved on.</summary>
  private ThreadRecord? Thread(int tid) {
    for (var i = 0; i < this._threadRows.Count; ++i)
      if (this._threadRows[i].Tid == tid)
        return this._threadRows[i];

    return null;
  }

  /// <summary>
  /// Opens the stack viewer on one thread (PRD §30).
  /// </summary>
  /// <remarks>
  /// Shown rather than shown modally, because §26 is about being able to have several of these open
  /// at once — two threads of the same process side by side is most of what a stack viewer is for.
  /// </remarks>
  private void ShowStack(int tid, bool resolveSymbols) => this.OpenStack(tid, resolveSymbols);

  /// <summary>Writes one thread's stack to a file (PRD §29, §30).</summary>
  private void SaveStack(int tid) {
    var stack = this._probe.GetThreadStack(this._key, tid, resolveSymbols: true);
    var dialog = new SaveFileDialog {
      Title = $"Save the stack of thread {tid}",
      FileName = $"thread-{tid}.txt",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName is not { Length: > 0 } path)
      return;

    try {
      File.WriteAllText(path, StackWindow.Describe(in stack, this.Thread(tid)?.Name));
    } catch (IOException e) {
      MessageBox.Show(e.Message, "Process Manager");
    } catch (UnauthorizedAccessException e) {
      MessageBox.Show(e.Message, "Process Manager");
    }
  }

  /// <summary>
  /// Shows the modules tab with the image the thread started in selected (PRD §29).
  /// </summary>
  /// <remarks>
  /// Only the first thread of a process has a start address on Linux, so this refuses for the others
  /// rather than selecting nothing and leaving the reader to wonder whether the click registered.
  /// </remarks>
  private void GoToStartModule(int tid) {
    if (this.Thread(tid)?.StartModule is not { Length: > 0 } module) {
      MessageBox.Show(
        "This thread has no start module: Linux records an entry point for the first thread of a"
        + " process and for no other.",
        "Process Manager"
      );

      return;
    }

    for (var i = 0; i < this._tabs.TabPages.Count; ++i)
      if (this._tabs.TabPages[i].Text == "Modules") {
        this._tabs.SelectedIndex = i;
        break;
      }

    this._dirty = true;
    this.Refresh();
    foreach (TreeNode node in this._modules.Nodes)
      if (node.Text.StartsWith(module, StringComparison.Ordinal)) {
        this._modules.SelectedNode = node;
        return;
      }
  }

  /// <summary>One thread as text, using the visible columns and their headers (PRD §29, §95).</summary>
  private string DescribeThread(int tid) {
    foreach (TreeNode node in this._threads.Nodes)
      if (node.Text == tid.ToString(CultureInfo.InvariantCulture))
        return Headers(this._threads) + "\n" + Row(node);

    return string.Empty;
  }

  /// <summary>Every thread as text, in the order the list shows them.</summary>
  private string DescribeThreads() {
    var text = new System.Text.StringBuilder();
    text.Append(Headers(this._threads));
    foreach (TreeNode node in this._threads.Nodes)
      text.Append('\n').Append(Row(node));

    return text.ToString();
  }

  private static string Headers(TreeListView list) {
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < list.Columns.Count; ++i) {
      if (i > 0)
        text.Append('\t');

      text.Append(list.Columns[i].Text);
    }

    return text.ToString();
  }

  private static string Row(TreeNode node) => node.Tag is string[] cells ? string.Join('\t', cells) : node.Text;

  private ActionResult ChooseThreadAffinity(int tid) {
    var chooser = new AffinityChooser($"thread {tid}", this._key.Pid, Math.Max(1, Environment.ProcessorCount), CpuTopology.Empty);
    chooser.ShowDialog();
    return chooser.Accepted
      ? this.Actions!.SetThreadAffinity(this._key, tid, chooser.Mask)
      : ActionResult.Ok;
  }

  private void FillThreads() {
    var threads = this._probe.GetThreads(this._key);
    this._threadRows = threads;
    // A thread cannot use more than one processor, so the per-core convention is the one that reads
    // correctly here: 100 % means this thread had a core to itself for the whole interval. The
    // normalized figure would divide by the machine and report a saturated thread as 6 % on a
    // sixteen-way box, which is true and useless (PRD §3.2).
    this._threadRates.Update(this._key, threads, Math.Max(1, Environment.ProcessorCount));

    Fill(this._threads, threads.Count, i => [
      threads[i].Tid.ToString(CultureInfo.InvariantCulture),
      threads[i].Name ?? "—",
      Humanize.State(threads[i].State),
      DescribeMode(threads[i]),
      Humanize.Percent(this._threadRates.CpuPercentPerCore(i)),
      Humanize.Rate(this._threadRates.ContextSwitchesPerSecond(i)),
      // Null covers two cases the kernel writes the same way: a thread that is not blocked at all,
      // and a wchan the reader may not have. Neither is a symbol, and the state column beside this
      // one already says which of the two it is.
      Shorten(threads[i].WaitReason, 22) ?? "—",
      Humanize.Duration(threads[i].CpuTimeNs),
      Humanize.Duration(threads[i].UserTimeNs) + " / " + Humanize.Duration(threads[i].KernelTimeNs),
      Humanize.Count(threads[i].ContextSwitches),
      Humanize.Pair(threads[i].VoluntaryContextSwitches, threads[i].InvoluntaryContextSwitches),
      Queued(threads[i].QueuedNs),
      threads[i].LastCpu >= 0 ? threads[i].LastCpu.ToString(CultureInfo.InvariantCulture) : "—",
      threads[i].Priority.ToString(CultureInfo.InvariantCulture)
        + " / " + (threads[i].BasePriority?.ToString(CultureInfo.InvariantCulture) ?? "—"),
      Humanize.SchedulingPolicy(threads[i].Policy),
      threads[i].Affinity ?? "—",
      Humanize.Bytes(threads[i].StackBytes),
      // The address itself. Which image it is in, and which function, is what the stack viewer of §30
      // opens on this row — a cell this wide cannot hold a path and a symbol as well.
      Humanize.Address(threads[i].InstructionPointer),
      Humanize.Address(threads[i].StartAddress),
      Where(threads[i].StartAddress, threads[i].StartModule, threads[i].StartSymbol),
      Humanize.Timestamp(threads[i].StartTimeUtcTicks),
    ]);
  }

  /// <summary>
  /// Cuts a value to the width of its column, and says that it did.
  /// </summary>
  /// <remarks>
  /// The list clips a cell at the column boundary with nothing between it and the next one, so a
  /// value wider than its column runs straight into its neighbour's — "poll_schedule_timeout.c0:00"
  /// was a wait channel and a CPU time with no gap. An ellipsis is a shorter answer and an honest
  /// one: the whole of it is still what "Copy row" puts on the clipboard. GCC's <c>.constprop.0</c>
  /// and <c>.isra.0</c> clone suffixes are what make kernel symbols this long, and they are left on
  /// rather than stripped — the kernel named the function, not us (PRD §5.3).
  /// </remarks>
  private static string? Shorten(string? value, int limit)
    => value is null || value.Length <= limit ? value : value[..(limit - 1)] + "…";

  /// <summary>
  /// A run-queue delay, which is a duration of a different order from a CPU time (PRD §29).
  /// </summary>
  /// <remarks>
  /// The <c>h:mm:ss</c> the other duration columns use is right for a total that has been
  /// accumulating since boot and useless here: a thread that has been kept waiting for a hundred
  /// milliseconds in its whole life renders as <c>0:00</c>, which is the number this column exists
  /// to distinguish from nothing at all.
  /// </remarks>
  private static string Queued(Counter nanoseconds) {
    if (!nanoseconds.TryGetValue(out var value))
      return Humanize.Placeholder(nanoseconds.Reason);

    if (value >= 1_000_000_000)
      return Humanize.Duration(nanoseconds);

    return value >= 1_000_000
      ? (value / 1_000_000d).ToString("0.# ms", CultureInfo.InvariantCulture)
      : value >= 1_000
        ? (value / 1_000d).ToString("0 µs", CultureInfo.InvariantCulture)
        : value.ToString("0 ns", CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Whose code the thread is running, and which call it is in when the machine will say (PRD §29).
  /// </summary>
  /// <remarks>
  /// The reason lives in the system-call number, because the file that would have answered both is
  /// the same file — a refusal to say which call a thread is in is a refusal to say which side of the
  /// boundary it is on. Showing "user" for a thread nobody was allowed to ask about would be a guess
  /// with a confident face on it.
  /// </remarks>
  private static string DescribeMode(in ThreadRecord thread) => thread.Mode switch {
    ThreadMode.User => "user",
    ThreadMode.Kernel => thread.SyscallNumber.TryGetValue(out var call)
      ? "kernel · " + call.ToString(CultureInfo.InvariantCulture)
      : "kernel",
    _ => Humanize.Placeholder(thread.SyscallNumber.Reason),
  };

  /// <summary>
  /// An address rendered as the place it is, rather than as a number nobody can act on.
  /// </summary>
  /// <remarks>
  /// The file name and not the path: the path is two hundred characters of <c>/usr/lib</c> and the
  /// row has to fit on a screen. The full path is still on the record, which is what "go to start
  /// module" opens (PRD §29).
  /// </remarks>
  private static string Where(Counter address, string? module, string? symbol) {
    if (module is not { Length: > 0 })
      return address.HasValue ? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform) : Humanize.Placeholder(address.Reason);

    var name = System.IO.Path.GetFileName(module);
    return symbol is { Length: > 0 } ? $"{name}!{symbol}" : name;
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

  /// <summary>
  /// Replaces a list's contents, keeping whatever was selected selected.
  /// </summary>
  /// <remarks>
  /// The thread list is refilled every tick, and a refill that cleared the selection would take the
  /// row out from under whoever was about to right-click it — once a second, forever. The first cell
  /// is the identity: a thread id, a module path, a descriptor number. A row whose identity is gone
  /// leaves nothing selected, which is the truth about a thread that ended.
  /// </remarks>
  private static void Fill(TreeListView list, int count, Func<int, string[]> row) {
    var wasSelected = list.SelectedNode?.Text;
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
      var node = new TreeNode(cells[0]) { Tag = cells };
      list.Nodes.Add(node);
      if (wasSelected is not null && string.Equals(node.Text, wasSelected, StringComparison.Ordinal))
        list.SelectedNode = node;
    }
  }

}
