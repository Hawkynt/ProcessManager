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
  /// The four modes §10 asks the lower pane for that are pages rather than lists (PRD §10, §26).
  /// </summary>
  /// <remarks>
  /// They live here rather than in the properties window that used to own them, and that is not a
  /// tidy-up: §26 asks for one row of tabs and not two, so a window that hosts this pane and adds its
  /// own Security page would have put two tabs called Security on one strip. Owning them here gives
  /// the main window's lower pane the same four modes for free, which is what §10 was asking for.
  /// </remarks>
  private readonly ProcessMemoryMapPage _map;

  private readonly ProcessWindowsPage _windowList;

  /// <summary>
  /// What confines the process (PRD §36).
  /// </summary>
  /// <remarks>
  /// Every field here is already in the sample — the uids and gids come off one line of
  /// <c>status</c> each, and the five capability sets off five more — so the page costs nothing to
  /// draw. The two that are not in the sample, the LSM label and the group list, cost a read apiece
  /// and are only asked for while this is the tab showing (PRD §5.4).
  /// <para>
  /// The four user ids are all here rather than only the effective one, because the gap between them
  /// is the interesting part: a process whose real and effective uids differ is running as somebody
  /// it was not started by, which is what a setuid binary looks like from outside.
  /// </para>
  /// </remarks>
  private readonly ProcessFactsPage _security = new(
    ProcessField.UserName,
    ProcessField.UserId,
    ProcessField.EffectiveUserName,
    ProcessField.EffectiveUserId,
    ProcessField.SavedUserId,
    ProcessField.FilesystemUserId,
    ProcessField.PrivilegeChanged,
    ProcessField.Elevated,
    ProcessField.GroupId,
    ProcessField.EffectiveGroupId,
    ProcessField.SavedGroupId,
    ProcessField.FilesystemGroupId,
    ProcessField.NoNewPrivileges,
    ProcessField.Seccomp,
    ProcessField.SeccompFilters,
    ProcessField.Capabilities,
    ProcessField.PermittedCapabilities,
    ProcessField.InheritableCapabilities,
    ProcessField.BoundingCapabilities,
    ProcessField.AmbientCapabilities,
    ProcessField.CapabilitiesHex
  );

  /// <summary>
  /// The unit this process belongs to, and what its unit file says (PRD §41).
  /// </summary>
  /// <remarks>
  /// No fields, because none of this is a column of the process table: a restart policy and a unit
  /// file path are facts about the <em>service</em>, and several processes share one. What belongs to
  /// the process is which unit it is in.
  /// </remarks>
  private readonly ProcessFactsPage _serviceFacts = new();

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
  /// What the module and descriptor lists are showing, for the actions that act on a row.
  /// </summary>
  /// <remarks>
  /// Kept for the same reason the thread rows are: a cell holds text, and an action needs the
  /// reading behind it. A descriptor's inode is what a search for its other holders is keyed on, and
  /// the cell shows it humanised; parsing it back out of the table would be reading our own
  /// formatting (PRD §32).
  /// </remarks>
  private IReadOnlyList<HandleRecord> _handleRows = [];

  private IReadOnlyList<ModuleRecord> _moduleRows = [];

  /// <summary>
  /// The socket join of the descriptor list: inode to endpoint, from the process's own connections.
  /// </summary>
  /// <remarks>
  /// Filled once per fill of the list rather than once per row — the five network tables are read
  /// whole either way — and kept afterwards so that the properties box of one descriptor can show
  /// the endpoint without reading them again (PRD §40).
  /// </remarks>
  private readonly Dictionary<ulong, string> _endpoints = [];

  /// <summary>The same join, for the reference count the same tables print (PRD §32).</summary>
  private readonly Dictionary<ulong, Counter> _socketReferences = [];

  /// <summary>
  /// A snapshot for the actions that need to know about processes other than this one.
  /// </summary>
  /// <remarks>
  /// Reused rather than made per request, because a snapshot owns its arrays and the two actions
  /// that want one — following a pidfd to its process, and finding the other holders of a resource —
  /// are both things somebody clicks rather than things that happen on a tick (PRD §5.4).
  /// </remarks>
  private SystemSnapshot? _machine;

  /// <summary>
  /// What may be done to a thread, or null in a read-only front-end.
  /// </summary>
  /// <remarks>
  /// Optional for the same reason <see cref="Abstractions.IProcessActions"/> is a separate interface
  /// from the probe: a pane that only shows things must be constructible without the ability to
  /// change any of them.
  /// <para>
  /// Passed on to the two pages that have actions of their own. They are built in the constructor and
  /// this is assigned afterwards, so a page that took the value once would have taken null — which is
  /// how the window list came to have a row menu that was drawn and inert.
  /// </para>
  /// </remarks>
  public Abstractions.IProcessActions? Actions {
    get;
    set {
      field = value;
      this._map.Actions = value;
      this._windowList.Actions = value;
    }
  }

  /// <summary>
  /// What the process is called, for the pages whose sentences name it.
  /// </summary>
  /// <remarks>
  /// Set by whoever owns the pane. The main window sets it as the selection moves; a properties
  /// window sets it once and it never changes, which is the point of a properties window.
  /// </remarks>
  public string ProcessName {
    get;
    set {
      field = value;
      this._windowList.Name = value;
    }
  } = string.Empty;

  /// <summary>
  /// What becomes of a tab this machine cannot fill — see <see cref="UnavailableTabs"/> (PRD §26).
  /// </summary>
  /// <remarks>
  /// Settled once per tab and never toggled: a page removed and added back as a reading came and went
  /// would move every tab to its right while somebody was reading one.
  /// </remarks>
  public UnavailableTabs Unavailable { get; set; } = UnavailableTabs.Disabled;

  /// <summary>The process the thread rows belong to, for the identity check the actions make.</summary>
  private ProcessKey Key => this._key;

  public DetailPane(ISystemProbe probe) {
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;
    this._map = new(probe, actions: null);
    this._windowList = new(probe, actions: null);
    this._threads.ContextMenuStrip = this.BuildThreadMenu();
    this._modules.ContextMenuStrip = this.BuildModuleMenu();
    this._handles.ContextMenuStrip = this.BuildHandleMenu();

    this._tabs.AccessibleName = "Process detail";
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
    // Twenty-one columns, which is more than fits: the list scrolls sideways now, so the ones past the
    // edge are reachable rather than lost. The order is what that changes — everything a reader
    // opens this tab for is in the first screenful, and the addresses, timestamps and identities
    // they go looking for on purpose are behind a scroll (PRD §31).
    AddList(
      "Modules",
      this._modules,
      ("Path", 340),
      ("Base", 124),
      ("Size", 76),
      ("Resident", 78),
      ("Perm", 52),
      // Wide enough for "shared object", measured off a capture: at 96 the longest of the six type
      // names filled the cell exactly and ran into the column beside it with no gap between them.
      ("Type", 118),
      // Which engine reads the file, which is not what its format is called: a .NET process maps
      // every assembly it loads and every one of them is a file that is not an ELF (PRD §31).
      ("Runtime", 76),
      // Why the image is here: the program, its loader, something it links against, something that
      // links against that, or nothing anybody named. Derived from the dependency graph, because
      // Linux publishes no load reason at all (PRD §31).
      ("Load", 84),
      // How many separate loads of this file are in the process. One for nearly every row, which is
      // what makes a two worth the width: two copies of a library is two sets of its globals.
      ("Loads", 52),
      // What the file asks the kernel for, in checksec's vocabulary. As wide as
      // "PIE NX RELRO+NOW CET", which is what a current distribution's binaries say — and measured
      // off a capture rather than guessed, because at 168 that exact string ran into the next
      // column on every row of a real machine.
      ("Mitigations", 198),
      ("Arch", 74),
      ("SONAME", 150),
      ("End", 124),
      ("Offset", 88),
      ("Device", 60),
      ("Inode", 84),
      ("File size", 78),
      ("Modified", 128),
      ("Maps", 46),
      // The build identity, which is what a distribution's debug packages and crash reports are
      // keyed by. Forty hex characters at this width shows the first eight or so, which is as much
      // as anybody compares by eye; the whole of it is what "Copy row" puts on the clipboard.
      ("Build ID", 150),
      ("Interpreter", 200)
    );
    AddList(
      "Handles",
      this._handles,
      ("Type", 100),
      ("FD", 46),
      // What the kernel's own st_mode says the target is, which is the answer wherever the name
      // above was only a good guess — and "no type" for the anonymous inodes, which have none
      // (PRD §32).
      //
      // Wide enough for "character 226:129", which is a graphics card's render node and is on every
      // desktop process: at 96 it photographed as "character 226", which is a different device, and
      // at 124 it lost the last digit of the minor number, which is a different device again.
      ("File type", 140),
      ("Access", 58),
      ("Position", 84),
      // How many references the kernel holds to the socket behind this descriptor, from the network
      // table's own column. Sockets only: nothing else a process holds has a count anybody may read.
      ("Refs", 48),
      // Which device the descriptor's inode is on and what kind of file system that is, from the
      // mount its fdinfo names. Empty for a socket or a pipe, which are on file systems the kernel
      // mounts nowhere (PRD §32).
      ("Device", 62),
      // Its own header is the longest thing in it, and at 76 the header itself lost a letter.
      ("Filesystem", 88),
      ("Inode", 92),
      ("Endpoint", 190),
      ("Flags", 200),
      ("Name", 420),
      // Whatever fdinfo said that belongs to this kind of descriptor and no other: an eventfd's
      // count, the descriptors an epoll set is watching, an inotify watch. One line here and the
      // whole of it in the properties box.
      ("Detail", 300)
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

    // The four modes §10 asks for that are pages rather than lists. They go on the end because the
    // toolkit's page collection has Add and Remove and no Insert, and because everything before them
    // is what somebody opens this pane for most often (PRD §10).
    //
    // Timeline is the fifth and is not here: it needs the event history of §63, and nothing in this
    // program records one yet. A tab named for a feature nobody wrote is worse than a missing one.
    this._mapPage = AddPage(_MemoryMapTab, this._map.Control);
    this._windowsPage = AddPage(_WindowsTab, this._windowList.Control);
    this._servicesPage = AddPage(_ServicesTab, this._serviceFacts.Control);
    AddPage(_SecurityTab, this._security.Control);

    // Switching to a tab is the request to fill it; nothing is collected for a tab nobody looked at.
    this._tabs.SelectedIndexChanged += (_, _) => {
      this._dirty = true;
      this.Refresh();
    };

    TabPage AddPage(string title, Control content) {
      var page = new TabPage(title);
      content.Dock = DockStyle.Fill;
      // The tab carries the title and the control inside it carries nothing, so a reader who moves
      // off the strip into the page is told only that it is a table. Named from the tab it is under,
      // unless the page named itself something better (PRD §74).
      content.AccessibleName ??= title;
      page.Controls.Add(content);
      this._tabs.TabPages.Add(page);
      return page;
    }

    void AddList(string title, TreeListView list, params (string Header, int Width)[] columns) {
      // The tab carries the title; the list inside it has no text of its own, so a reader who moves
      // from the tab strip into the table would otherwise be told only that it is a table (PRD §74).
      list.AccessibleName = title;
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
    // The four pages that are not lists follow the key too. Each of them throws away what it read for
    // the last process, so a pane that moves with the selection cannot show one process's mappings
    // under another's name (PRD §72.2).
    this._map.Key = key;
    this._windowList.Key = key;
    this._image = null;
    this._imageRead = false;
    this._servicesRead = false;
  }

  #region the four pages that are not lists (PRD §10, §26)

  private const string _MemoryMapTab = "Memory map";
  private const string _SecurityTab = "Security";
  private const string _ServicesTab = "Services";
  private const string _WindowsTab = "Windows";

  private TabPage? _mapPage;
  private TabPage? _windowsPage;
  private TabPage? _servicesPage;
  private bool _mapSettled;
  private bool _windowsSettled;
  private bool _servicesRead;

  /// <summary>The last sample's row, for the pages that are filled only while they are showing.</summary>
  private ProcessRow? _row;

  /// <summary>
  /// The cgroup path from the last sample, which is what the unit is looked up by.
  /// </summary>
  /// <remarks>
  /// Kept rather than read off the row's Container cell: that cell is formatted for a table, and
  /// looking a unit up by our own formatting is how the two quietly stop agreeing.
  /// </remarks>
  private string? _containerPath;

  private ImageInfo? _image;
  private bool _imageRead;

  /// <summary>What the Security page says, for a test with no display to read it off (PRD §36).</summary>
  public string SecurityText => this._security.Description;

  /// <summary>What the Services page says (PRD §41).</summary>
  public string ServicesText => this._serviceFacts.Description;

  /// <summary>The sentence above the memory map, which is the half that explains an empty one.</summary>
  public string MemoryMapHeading => this._map.Heading;

  /// <summary>How many mappings the memory map is showing (PRD §34).</summary>
  public int MemoryMapRows => this._map.RowCount;

  /// <summary>The sentence above the window list — the half that explains an empty one (PRD §39).</summary>
  public string WindowsHeading => this._windowList.Heading;

  /// <summary>How many windows this process has on screen, as the page last read them (PRD §39).</summary>
  public int WindowRows => this._windowList.RowCount;

  /// <summary>
  /// Lays out the four pages the toolkit cannot lay out for us.
  /// </summary>
  /// <remarks>
  /// A control outside the toolkit's own assembly cannot observe its own resize, so whoever owns the
  /// pane runs this from its layout pass. Every part of it is a no-op once the size has settled.
  /// </remarks>
  public void ApplyLayout() {
    this._map.Stretch();
    this._windowList.Stretch();
    this._security.Stretch();
    this._serviceFacts.Stretch();
  }

  /// <summary>
  /// What confines the process: the identity from the sample, and the two things that cost a read.
  /// </summary>
  /// <remarks>
  /// The two extras are read on every tick while this is the page showing rather than once, because
  /// unlike the image they can change under a running process: a program may drop groups, and a label
  /// changes at an <c>exec</c>. Two small files for one process is not a cost worth caching wrongly.
  /// </remarks>
  private void UpdateSecurity(ProcessRow row) {
    var extras = new List<KeyValuePair<string, string>>();
    var security = this._probe.DescribeSecurity(this._key);

    extras.Add(new("Security module", Label(security)));
    extras.Add(new("Supplementary groups", Groups(security)));

    // The namespaces, from the image description, read once per process and kept. They are where a
    // container actually is: two processes sharing an inode share that namespace, which is a harder
    // fact than a cgroup path anybody may write (PRD §14, §36).
    if (!this._imageRead) {
      this._imageRead = true;
      this._image = this._probe.DescribeImage(this._key);
    }

    if (this._image is { Namespaces.Count: > 0 } image)
      foreach (var (kind, inode) in image.Namespaces)
        extras.Add(new($"Namespace, {kind}", inode));
    else
      // Said rather than left off. Every process on Linux is in a namespace of every kind, so a page
      // with no namespace rows on it would be stating something that cannot be true — the honest
      // reading of an empty list here is that the links under /proc/[pid]/ns could not be followed
      // (PRD §72.3).
      extras.Add(new("Namespaces", "not readable — the links under /proc/[pid]/ns need the same permission as attaching a debugger"));

    this._security.Update(row, extras);
  }

  /// <summary>
  /// The LSM label, or which of the two reasons there is none.
  /// </summary>
  /// <remarks>
  /// A kernel with no security module fails the read outright rather than producing an empty file, so
  /// "nothing is confining this" and "we were not allowed to look" arrive the same way and must not be
  /// reported the same way. Neither is a blank, which would read as a clean bill of health (PRD §70).
  /// </remarks>
  private static string Label(ProcessSecurity? security) => security switch {
    null => "the process has ended",
    { Label: { Length: > 0 } label } => label,
    { LabelReason: UnknownReason.NotPermitted } => "not readable as this user",
    _ => "none — this kernel has no SELinux or AppArmor loaded",
  };

  private static string Groups(ProcessSecurity? security) {
    if (security is null)
      return "the process has ended";

    if (security.GroupsReason != UnknownReason.None)
      return Humanize.Explain(security.GroupsReason);

    if (security.SupplementaryGroups.Count == 0)
      // A real answer rather than a hole. Every kernel thread is in none, and so is anything started
      // by a service manager that cleared them.
      return "none";

    var names = new List<string>(security.SupplementaryGroups.Count);
    foreach (var group in security.SupplementaryGroups)
      // The number always, the name when this machine's own file has one. A group that comes from a
      // directory service is in no file here and stays a number, which is the honest answer rather
      // than a blank (PRD §5.3).
      names.Add(group.Name is { Length: > 0 } name
        ? $"{name} ({group.Id.ToString(CultureInfo.InvariantCulture)})"
        : group.Id.ToString(CultureInfo.InvariantCulture));

    return string.Join(", ", names);
  }

  /// <summary>
  /// Which service this process belongs to, and what that service's unit file says (PRD §41).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read once per process, when the page is first asked for. The reading is a walk of every unit file
  /// on the machine — 372 of them here — which is far too much to spend on a tick, and it does not
  /// need spending twice: a process cannot move between units while it runs, and a unit that stops
  /// takes its processes with it. What can change underneath it is somebody running
  /// <c>systemctl disable</c> in another window, and that is a fair price for not walking a thousand
  /// files a second (PRD §5.4).
  /// </para>
  /// <para>
  /// The unit comes from the cgroup, because a systemd unit <em>is</em> a cgroup — the same join
  /// §40's owning-service column makes, through the same code, so the two cannot disagree. The
  /// innermost one wins: a desktop application sits inside its own session manager, which is itself a
  /// unit, and naming the outer one would report every program a user starts as belonging to the
  /// manager that started it.
  /// </para>
  /// </remarks>
  private void UpdateServices() {
    if (this._servicesRead)
      return;

    // Before the first sample there is no cgroup to look a unit up by, and the answer would latch —
    // somebody quick enough to click this tab inside the first tick would have been told for the rest
    // of the pane's life that the cgroup could not be read. Nothing is settled until there is
    // something to settle it from; the tick calls this again.
    if (this._row is null)
      return;

    this._servicesRead = true;
    var services = this._probe.GetServices();
    if (services.Count == 0) {
      // No service manager this build can read — which is a fact about the machine, not about this
      // process, and so is the one case the tab may be taken off the strip.
      this.Settle(ref this._servicesPage);
      this._serviceFacts.ShowUnavailable(
        "Nothing on this machine publishes services in a form this build reads. Only systemd is read, "
        + "from the unit files and the cgroup tree rather than over D-Bus."
      );

      return;
    }

    if (CgroupUnit.Of(this._containerPath) is not { } unit) {
      // A finding rather than a hole, and the tab stays: most of a desktop is like this. A slice is
      // deliberately not a unit for this purpose — it holds no processes of its own — so a cgroup
      // with only slices in it answers nothing rather than the nearest thing that looks like one.
      this._serviceFacts.Update([
        new("Service", "none — this process is under no systemd unit"),
        new(
          "Why",
          this._containerPath is { Length: > 0 } path
            ? $"its cgroup is {path}, and no segment of that is a service, a scope or a socket unit"
            : "its cgroup could not be read, so there is nothing to look a unit up by"
        ),
        new("Units on this machine", services.Count.ToString(CultureInfo.InvariantCulture)),
      ]);

      return;
    }

    if (FindService(services, unit) is not { } service) {
      // The cgroup names a unit that the unit-file walk did not produce: a transient scope systemd
      // made without a file on disk is the usual one. The name is still the truth about the process,
      // so it is reported, and the absence is explained rather than shown as an empty page.
      this._serviceFacts.Update([
        new("Service", unit),
        new("Unit file", "none on disk — a transient unit, created at runtime and never written out"),
        new("Read from", "the cgroup this process is in, which is what a systemd unit is"),
      ]);

      return;
    }

    this._serviceFacts.Update([
      new("Service", service.Name),
      new("Description", service.Description is { Length: > 0 } text ? text : "—"),
      new("State", DescribeServiceState(service)),
      new("Starts at boot", StartsAtBoot(service)),
      // Its own row and not folded into the one above: masked units can never run whatever else is
      // configured, and it is the setting people forget they made.
      new("Masked", service.Masked ? "yes — it can never be started while this stands" : "no"),
      new("Main process", MainProcess(service, this._key.Pid)),
      new("Service type", service.Type ?? "—"),
      new("Runs as", service.Account ?? "the unit file names no account, so the manager's default applies: root"),
      new("Restart policy", service.RestartPolicy is { Length: > 0 } policy ? policy : "—"),
      new("Command", service.Command is { Length: > 0 } command ? command : "—"),
      new("Unit file", service.Path.Length > 0 ? service.Path : "none on disk"),
    ]);
  }

  private static string DescribeServiceState(ServiceRecord service) => service.State switch {
    ServiceState.Running => "running",
    // Not folded into "inactive", which is what this used to say about it: a oneshot unit that set
    // something up and finished is still doing its job and has nothing in a cgroup to find.
    ServiceState.Active => "active, with no processes of its own",
    ServiceState.Inactive => "inactive",
    _ => "unknown",
  };

  /// <summary>The unit of that name, or null when the walk of the unit files did not produce one.</summary>
  private static ServiceRecord? FindService(IReadOnlyList<ServiceRecord> services, string unit) {
    foreach (var service in services)
      if (string.Equals(service.Name, unit, StringComparison.Ordinal))
        return service;

    return null;
  }

  /// <summary>
  /// Whether the unit starts at boot.
  /// </summary>
  /// <remarks>
  /// Three answers and not two. A unit started only by a socket or a timer is neither enabled nor
  /// disabled in the sense the row means, and saying "no" about one would be wrong about a service
  /// that starts perfectly reliably (PRD §41, §72.3).
  /// </remarks>
  private static string StartsAtBoot(ServiceRecord service) => service.Enabled switch {
    true => "yes",
    false => "no",
    _ => "neither — nothing links it into a boot target, so something else starts it: a socket, a timer, or another unit",
  };

  /// <summary>
  /// The unit's main process, and whether it is this one.
  /// </summary>
  /// <remarks>
  /// The distinction the page is worth opening for. A service's main process is the one systemd
  /// watches and restarts; everything else in the cgroup is a child it will take down with it, and
  /// the two are not the same thing to be looking at.
  /// </remarks>
  private static string MainProcess(ServiceRecord service, int pid) {
    if (service.MainPid <= 0)
      return "none recorded — the unit's cgroup was empty when it was read";

    var number = service.MainPid.ToString(CultureInfo.InvariantCulture);
    return service.MainPid == pid
      ? $"{number} — this process"
      : $"{number} — this process is one of its children, not the one systemd watches";
  }

  /// <summary>
  /// Takes a tab off the strip once, where that is the preference.
  /// </summary>
  /// <remarks>
  /// Only ever called for a state that is a statement about <em>this build</em> rather than about the
  /// machine or the process. A Wayland session refusing to list windows and a kernel refusing to walk
  /// a page table are facts about the machine and keep their tab, saying which — collapsing them
  /// would make "this desktop will not tell you" and "this build cannot ask" the same answer
  /// (PRD §5.3, §26).
  /// </remarks>
  private void Settle(ref TabPage? page) {
    if (this.Unavailable != UnavailableTabs.Hidden || page is not { } removing)
      return;

    this._tabs.TabPages.Remove(removing);
    page = null;
  }

  #endregion

  /// <summary>
  /// Describes the selected process in the overview tab. Called every sample, because the numbers
  /// here come from the snapshot that was just taken and cost nothing.
  /// </summary>
  public void UpdateOverview(in ProcessRecord process, ProcessRow row) {
    // Kept for the four pages that are filled only when they are the one showing, which cannot ask
    // the sample for a row of their own.
    this._row = row;
    this._containerPath = process.ContainerPath;
    this.ProcessName = process.Name;

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

    // The two sheets that are re-read on every tick while they are showing, unlike the lists beside
    // them. A security context is two small files and a unit look-up is settled once per process, and
    // half of what is on the security page — the groups, the label — moves under a running process
    // (PRD §26).
    switch (selected) {
      case _SecurityTab:
        this._dirty = false;
        if (this._row is { } row)
          this.UpdateSecurity(row);

        return;

      case _ServicesTab:
        this._dirty = false;
        this.UpdateServices();
        return;

      default: break;
    }

    if (!this._dirty)
      return;

    this._dirty = false;
    switch (selected) {
      case "Modules": this.FillModules(); break;
      case "Handles": this.FillHandles(); break;
      case "Environment": this.FillEnvironment(); break;
      case "Network": this.FillNetwork(); break;
      // The two whose cost is the size of the process rather than a constant: filled when the tab is
      // opened, with a button underneath for a fresher answer. The tab is only taken off the strip
      // for the one state that is a statement about this build (PRD §5.4, §26).
      case _MemoryMapTab:
        this._map.EnsureFilled();
        if (!this._mapSettled && this._map.State == MemoryMapState.NotImplemented) {
          this._mapSettled = true;
          this.Settle(ref this._mapPage);
        }

        break;

      case _WindowsTab:
        this._windowList.EnsureFilled();
        if (!this._windowsSettled && this._windowList.State == WindowSourceState.NotImplemented) {
          this._windowsSettled = true;
          this.Settle(ref this._windowsPage);
        }

        break;

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
    menu.Items.Add(ModuleItem("Copy row", path => Clipboard.SetText(this.DescribeModule(path))));

    var all = new ToolStripMenuItem("Copy all modules");
    all.Click += (_, _) => Clipboard.SetText(this.DescribeModules());
    menu.Items.Add(all);

    menu.Items.Add(ModuleItem("Open folder", this.RevealModule));
    menu.Items.Add(ModuleItem(
      "File properties…",
      // The verify delegate is what turns the hash button into §31's signature status: one read of
      // the image answers "what are these bytes" and "does whoever shipped them still recognise
      // them", and neither is paid for until it is pressed (PRD §5.4, §70).
      path => new FilePropertiesDialog(
        path,
        this.ModuleFacts(path),
        this.Actions,
        image => this._probe.DescribeImage(image, verify: true)
      ).ShowDialog()
    ));
    return menu;
  }

  /// <summary>One module as text, using the visible columns and their headers (PRD §31, §95).</summary>
  /// <remarks>
  /// The whole row rather than the path: the build identity is forty hex characters shown eight at a
  /// time, and the mitigation list is what somebody is most likely to want to paste into a bug
  /// report. Copying is the only way to either of them in full.
  /// </remarks>
  private string DescribeModule(string path) {
    foreach (TreeNode node in this._modules.Nodes)
      if (node.Tag is string[] cells && cells.Length > 0 && cells[0].StartsWith(path, StringComparison.Ordinal))
        return Headers(this._modules) + "\n" + Row(node);

    return string.Empty;
  }

  private string DescribeModules() {
    var text = new System.Text.StringBuilder();
    text.Append(Headers(this._modules));
    foreach (TreeNode node in this._modules.Nodes)
      text.Append('\n').Append(Row(node));

    return text.ToString();
  }

  /// <summary>
  /// What can be done with one open descriptor (PRD §32).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Four of §32's five actions. "Go to owning process" is here in the only form that means
  /// anything: every row belongs to the process the pane is showing, so the useful navigation is to
  /// the process a descriptor <em>names</em> — the target of a pidfd, or a process at the other end
  /// of a pipe.
  /// </para>
  /// <para>
  /// Closing a descriptor in another process is absent rather than disabled. Linux offers no
  /// supported way to do it, and an item that could only ever refuse is a lie dressed as a feature
  /// (PRD §32, §69).
  /// </para>
  /// </remarks>
  private ContextMenuStrip BuildHandleMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(HandleItem("Copy row", (in HandleRecord handle) => Clipboard.SetText(this.DescribeHandle(in handle))));

    var all = new ToolStripMenuItem("Copy all descriptors");
    all.Click += (_, _) => Clipboard.SetText(this.DescribeHandles());
    menu.Items.Add(all);

    menu.Items.Add(HandleItem("Open folder", this.RevealDescriptor));
    menu.Items.Add(HandleItem("Resource properties…", (in HandleRecord handle) => this.ShowHandleProperties(in handle)));
    menu.Items.Add(HandleItem("Go to the process this names", this.GoToNamedProcess));
    menu.Items.Add(HandleItem(
      "Find what else holds this…",
      (in HandleRecord handle) => this.ShowHandleProperties(in handle, holders: true)
    ));
    return menu;
  }

  private delegate void HandleAction(in HandleRecord handle);

  private ToolStripMenuItem HandleItem(string text, HandleAction action) {
    var item = new ToolStripMenuItem(text);
    item.Click += (_, _) => {
      if (this.SelectedHandle is { } handle)
        action(in handle);
    };

    return item;
  }

  /// <summary>
  /// The record behind the selected descriptor row, or null when nothing is selected.
  /// </summary>
  /// <remarks>
  /// Matched on the descriptor number in the second cell rather than on the row's position: the list
  /// is refilled while the menu exists, and a process that closed something between the fill and the
  /// click would otherwise hand back a different descriptor than the one under the pointer.
  /// </remarks>
  private HandleRecord? SelectedHandle {
    get {
      if (this._handles.SelectedNode?.Tag is not string[] cells || cells.Length < 2)
        return null;

      if (!ulong.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fd))
        return null;

      for (var i = 0; i < this._handleRows.Count; ++i)
        if (this._handleRows[i].Handle == fd)
          return this._handleRows[i];

      return null;
    }
  }

  /// <summary>Opens the folder a descriptor's name lives in, when it has one (PRD §32).</summary>
  /// <remarks>
  /// A socket, a pipe and an anonymous inode are named by the kernel and not by the file system, so
  /// there is no folder to open and this says which of the two it is rather than opening the file
  /// manager on a name that is not a path.
  /// </remarks>
  private void RevealDescriptor(in HandleRecord handle) {
    if (handle.Name is not { Length: > 0 } name || !name.StartsWith('/')) {
      MessageBox.Show(
        "This descriptor has no path: the kernel names it, not the file system.",
        "Process Manager"
      );

      return;
    }

    this.RevealModule(name);
  }

  /// <summary>
  /// Opens a properties window on the process a descriptor names (PRD §32).
  /// </summary>
  /// <remarks>
  /// A pidfd names one directly. Nothing else does — which is why this refuses rather than opening
  /// the process the descriptor belongs to, since that one is already the window the reader is in.
  /// </remarks>
  private void GoToNamedProcess(in HandleRecord handle) {
    if (!handle.TargetPid.TryGetValue(out var pid)) {
      MessageBox.Show(
        "This descriptor names no process. Only a pidfd does, and this is a "
        + Humanize.ResourceKind(handle.Kind) + ".",
        "Process Manager"
      );

      return;
    }

    this.OpenProcess((int)pid);
  }

  /// <summary>
  /// Opens a properties window on another process, by pid.
  /// </summary>
  /// <remarks>
  /// A pid on its own is not an identity — §3.2 pairs it with the process's start time, because the
  /// number is reused — so the machine is sampled to find the pair. That is also what turns the
  /// number into a name for the title bar, and what makes "that process has already gone" a
  /// sentence this can say.
  /// </remarks>
  private void OpenProcess(int pid) {
    foreach (var process in this.Machine().Processes)
      if (process.Pid == pid) {
        new ProcessPropertiesWindow(this._probe, process.Key, process.Name, this.Actions).Show();
        return;
      }

    MessageBox.Show($"There is no process {pid} any more.", "Process Manager");
  }

  /// <summary>Everything about one descriptor, in a window of its own (PRD §32).</summary>
  private void ShowHandleProperties(in HandleRecord handle, bool holders = false) {
    var dialog = new HandlePropertiesDialog(
      this._probe,
      this._key,
      in handle,
      handle.Inode.TryGetValue(out var inode) && this._endpoints.TryGetValue(inode, out var endpoint) ? endpoint : null,
      this.Actions,
      handle.Inode.TryGetValue(out var key) && this._socketReferences.TryGetValue(key, out var references)
        ? references
        : null
    );

    dialog.Show();
    if (holders)
      dialog.FindHolders();
  }

  /// <summary>The machine, sampled once and kept for the actions that need more than this process.</summary>
  private SystemSnapshot Machine() {
    this._machine ??= new();
    this._probe.Sample(this._machine);
    return this._machine;
  }

  /// <summary>One descriptor as text, using the visible columns and their headers (PRD §32, §95).</summary>
  private string DescribeHandle(in HandleRecord handle) {
    var number = handle.Handle.ToString(CultureInfo.InvariantCulture);
    foreach (TreeNode node in this._handles.Nodes)
      if (node.Tag is string[] cells && cells.Length > 1 && cells[1] == number)
        return Headers(this._handles) + "\n" + Row(node);

    return string.Empty;
  }

  /// <summary>Every descriptor as text, in the order the list shows them.</summary>
  private string DescribeHandles() {
    var text = new System.Text.StringBuilder();
    text.Append(Headers(this._handles));
    foreach (TreeNode node in this._handles.Nodes)
      text.Append('\n').Append(Row(node));

    return text.ToString();
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
      extra.Add(new("runtime", Humanize.ImageRuntime(module.Runtime)));
      extra.Add(new("architecture", module.Architecture ?? "—"));
      extra.Add(new("soname", module.Soname ?? "—"));
      extra.Add(new("mapped at", Humanize.Address(module.BaseAddress)));
      extra.Add(new("mappings", module.MappingCount.ToString(CultureInfo.InvariantCulture)));
      extra.Add(new("loads", Humanize.LoadCount(module.LoadCount)));
      break;
    }

    this.AddPublisherFacts(path, extra);
    return extra;
  }

  /// <summary>
  /// The four things §31 asks a file for that an ELF does not carry: version, description, company
  /// and product.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A PE keeps them in a version resource. ELF has no such section and never has, so on this
  /// platform they are the packaging system's account of the package the file arrived in — which is
  /// what a Linux machine actually publishes about a file. Each line says so: "product" is a package
  /// name and the reader is told it is, because a package version read as a file version is exactly
  /// the false equivalence §5.3 forbids.
  /// </para>
  /// <para>
  /// Asked when the box is opened and not while the list is filled. The lookup builds an index of
  /// every path every installed package owns — half a million of them on an ordinary desktop — and
  /// putting it behind a column would spend that on a tab somebody clicked (PRD §5.4). The bytes are
  /// not read at all: the signature check that would read them is the "Compute SHA-256" button, and
  /// this is the question that can be answered without opening the file.
  /// </para>
  /// </remarks>
  private void AddPublisherFacts(string path, List<KeyValuePair<string, string>> extra) {
    var trust = this._probe.DescribeImage(path);
    if (trust.Package.Text is not { Length: > 0 } package) {
      // Nothing claims it, or nothing could be asked. Said rather than left out: four missing lines
      // read as four fields nobody implemented (PRD §72.3).
      extra.Add(new("product", trust.Package.WasChecked
        ? "no package on this machine claims this file"
        : Humanize.Placeholder(trust.Package.Reason)));

      return;
    }

    extra.Add(new("product", package));
    extra.Add(new("version", trust.Package.Version ?? "the package database records none"));
    extra.Add(new("company", trust.Publisher ?? "the package database names nobody"));
    extra.Add(new("description", trust.Summary ?? "the package database carries none"));
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
    this._moduleRows = modules;
    Fill(this._modules, modules.Count, i => [
      // A deleted image is still mapped and still running; saying so on the path is the only warning
      // that the file on disk is no longer the code in memory (PRD §31).
      modules[i].IsDeleted ? modules[i].Path + "  (deleted)" : modules[i].Path,
      Humanize.Address(modules[i].BaseAddress),
      Humanize.Bytes(modules[i].Size),
      Humanize.Bytes(modules[i].ResidentBytes),
      // The kernel's own four characters: read, write, execute, and shared-or-private. Three of §31's
      // flags in one column, in the notation anybody who has read a maps file already knows.
      modules[i].Permissions.Length > 0 ? modules[i].Permissions : "—",
      Humanize.ImageType(modules[i].Type),
      Humanize.ImageRuntime(modules[i].Runtime),
      Humanize.LoadReason(modules[i].LoadReason),
      Humanize.LoadCount(modules[i].LoadCount),
      Humanize.Mitigations(modules[i].Mitigations),
      modules[i].Architecture ?? "—",
      modules[i].Soname ?? "—",
      Humanize.Address(modules[i].EndAddress),
      Humanize.Address(modules[i].FileOffset),
      modules[i].Device ?? "—",
      Humanize.Count(modules[i].Inode),
      Humanize.Bytes(modules[i].FileSizeBytes),
      Humanize.Timestamp(modules[i].FileModifiedUtcTicks),
      modules[i].MappingCount.ToString(CultureInfo.InvariantCulture),
      // An image built without --build-id has no such note, which is a fact about the build and not
      // about our access to it — hence the dash rather than a refusal placeholder.
      modules[i].BuildId ?? "—",
      modules[i].Interpreter ?? "—",
    ]);
  }

  private void FillHandles() {
    var handles = this._probe.GetHandles(this._key);
    this._handleRows = handles;

    // The socket join, done once for the list rather than once per row: the five network tables are
    // read whole either way, and a per-row lookup would read them once per socket the process holds.
    this._endpoints.Clear();
    this._socketReferences.Clear();
    // The same named ports the network tab shows. A socket described as :631 on one tab and :ipp on
    // the next is one window disagreeing with itself about one socket (PRD §40).
    var services = this._probe.DescribePortNames();
    foreach (var connection in this._probe.GetConnections(this._key)) {
      this._endpoints[connection.Inode] = connection.RemotePort == 0
        ? $"{Humanize.LocalEndpoint(connection, services, null)} {connection.State}"
        : $"{Humanize.LocalEndpoint(connection, services, null)} → {Humanize.RemoteEndpoint(connection, services, null)}";
      this._socketReferences[connection.Inode] = connection.References;
    }

    this._handleSummary.Text = HandleTally.From(handles).Describe();
    Fill(this._handles, handles.Count, i => [
      Humanize.ResourceKind(handles[i].Kind),
      handles[i].Handle.ToString(CultureInfo.InvariantCulture),
      Humanize.FileNode(handles[i].NodeType, handles[i].NodeDevice),
      handles[i].Access ?? "—",
      Humanize.Count(handles[i].Position),
      this.ReferencesOf(handles[i]),
      // A descriptor on no mounted file system — a socket, a pipe, an anonymous inode — has no
      // device to name, and the mount id it carries names a file system the kernel keeps to itself.
      // The dash is that answer; a refusal shows the reason the mount id carries instead.
      handles[i].Device ?? Unmounted(handles[i].MountId),
      handles[i].FileSystem ?? Unmounted(handles[i].MountId),
      Humanize.Count(handles[i].Inode),
      this.EndpointOf(handles[i]),
      DescriptorParser.DescribeFlags(handles[i].OpenFlags) ?? Humanize.Placeholder(handles[i].OpenFlags.Reason),
      // A handle the kernel would not name is a normal outcome on Windows, not a failure — see
      // HandleNameResolver. Saying so beats a blank cell nobody can interpret.
      handles[i].TargetPid.TryGetValue(out var target)
        ? $"{handles[i].Name} → pid {target}"
        : handles[i].Name ?? "<not named>",
      // One line of it. An epoll set watching four hundred descriptors would otherwise be four
      // hundred lines in a cell one line high, and the properties box is where the rest of it is.
      OneLine(handles[i].Detail),
    ], identity: 1);
  }

  /// <summary>
  /// How many references the kernel holds to what this descriptor points at (PRD §32).
  /// </summary>
  /// <remarks>
  /// A socket, and nothing else. The five network tables print <c>sk_refcnt</c> beside every row;
  /// no file under <c>/proc</c> prints the count in the <c>struct file</c> behind a file, a pipe or
  /// an event descriptor, so those say "there is no such number here" rather than showing one they
  /// do not have.
  /// </remarks>
  private string ReferencesOf(in HandleRecord handle) {
    if (handle.Kind != HandleKind.Socket)
      return Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    return handle.Inode.TryGetValue(out var inode) && this._socketReferences.TryGetValue(inode, out var references)
      ? Humanize.Count(references)
      // The socket closed between the two reads, which is the same reason its endpoint is a dash.
      : "—";
  }

  /// <summary>The row of the five network tables this descriptor's inode belongs to, if any.</summary>
  private string EndpointOf(in HandleRecord handle)
    => handle.Inode.TryGetValue(out var inode) && this._endpoints.TryGetValue(inode, out var endpoint)
      ? endpoint
      // A socket with no row in the five tables closed between the two reads — and every other kind
      // of descriptor has no endpoint at all, which is the same dash for a different reason.
      : "—";

  /// <summary>
  /// Why a descriptor has no device: because it is on nothing mountable, or because nobody could
  /// read its <c>fdinfo</c> to find out.
  /// </summary>
  private static string Unmounted(Counter mountId)
    => mountId.HasValue ? "—" : Humanize.Placeholder(mountId.Reason);

  /// <summary>Several lines of kernel detail as one, for a cell one line high.</summary>
  private static string OneLine(string? detail)
    => detail is { Length: > 0 } ? string.Join(" · ", detail.Split('\n')) : "—";

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

    // Named ports, from the machine's own file by way of the probe. Free where the CLI is free: the
    // table is read once and kept, and asking for it per fill costs a dictionary lookup rather than
    // a read. Without this the window said 443 where --connections said https, which is the one
    // thing the parity contract is for (PRD §40, §58).
    var services = this._probe.DescribePortNames();
    Fill(this._network, connections.Count, i => {
      var connection = connections[i];
      var statistics = connection.Statistics;
      return [
        connection.Protocol.ToString(),
        Humanize.SocketKindName(connection.Kind),
        Humanize.LocalEndpoint(connection, services, hosts),
        Humanize.RemoteEndpoint(connection, services, hosts),
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

    // The engine is in the item's own label rather than only in the code. This is the one thing in
    // the program that reaches the network, and §97's promise is that nothing goes out unasked — an
    // item that said only "search online" would be asking for consent without saying to what
    // (PRD §40, §70, §97).
    this._searchRemote.Click += (_, _) => this.SearchRemoteEndpoint();
    menu.Items.Add(this._searchRemote);

    // Greyed on a row with no far end rather than shown and then refusing: a listening socket and a
    // Unix socket both have nothing to search for, and an item that could only apologise is the thing
    // §32 objects to. Settled as the menu opens, because that is when the selected row is known.
    menu.Opening += (_, _) => this.RefreshNetworkMenu();

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

  private readonly ToolStripMenuItem _searchRemote =
    new($"Search remote address on {DesktopOpen.SearchEngine}…");

  /// <summary>
  /// Settles which items on the network menu apply to the row that is selected (PRD §40).
  /// </summary>
  /// <remarks>
  /// Called as the menu opens, which is when the selection is known. Public so that a test with no
  /// display can put the menu into the state a right-click would and then work the item — the toolkit
  /// will not open a menu without a backend to open it on.
  /// </remarks>
  public void RefreshNetworkMenu() => this._searchRemote.Enabled = this.SelectedRemoteHost is not null;

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
  /// The address at the far end of the selected row, without its port — the term worth searching for.
  /// </summary>
  /// <remarks>
  /// Taken off the drawn cell for the same reason "copy endpoint" is: the table is a moment old, a
  /// connection can close between a right-click and a menu choice, and re-reading the kernel would
  /// sometimes hand back a different socket. The port is dropped because it is noise in a search —
  /// what somebody wants to know is who <c>140.82.121.4</c> belongs to, and adding <c>:443</c> to the
  /// query only narrows it to pages that happen to mention the port too.
  /// </remarks>
  private string? SelectedRemoteHost
    => this._network.SelectedNode?.Tag is string[] cells && cells.Length > 3
      ? Humanize.EndpointHost(cells[3])
      : null;

  /// <summary>
  /// Opens a search for the far end of the selected connection (PRD §40).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The only thing in this program that reaches the network, and it happens because somebody clicked
  /// an item that names where it is going. Nothing about the process is sent — the query is the
  /// address, which is the one fact a reader is trying to put a name to.
  /// </para>
  /// <para>
  /// Through <see cref="Abstractions.IProcessActions.Launch"/>, like every other program this starts:
  /// one code path, one set of refusals, one place a test can watch.
  /// </para>
  /// </remarks>
  private void SearchRemoteEndpoint() {
    // Unreachable from the menu, which greys the item on such a row. Kept because "no far end" must
    // never become a search for the em dash the cell shows.
    if (this.SelectedRemoteHost is not { } host)
      return;

    if (this.Actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (DesktopOpen.Search(host) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the search to.", "Process Manager");
      return;
    }

    var result = this.Actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
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
  /// <param name="identity">
  /// Which cell says that two rows are the same row. The first, except where the first cell is a
  /// category rather than a name: every descriptor of a process that holds thirty files has "file"
  /// in its first cell, and restoring the selection by that put it on whichever of the thirty came
  /// first.
  /// </param>
  private static void Fill(TreeListView list, int count, Func<int, string[]> row, int identity = 0) {
    var wasSelected = list.SelectedNode?.Tag is string[] selected && identity < selected.Length
      ? selected[identity]
      : list.SelectedNode?.Text;
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
      var name = identity < cells.Length ? cells[identity] : cells[0];
      if (wasSelected is not null && string.Equals(name, wasSelected, StringComparison.Ordinal))
        list.SelectedNode = node;
    }
  }

}
