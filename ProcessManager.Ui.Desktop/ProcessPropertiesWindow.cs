using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>What becomes of a tab whose capability this machine does not have (PRD §26).</summary>
/// <remarks>
/// A preference rather than a decision, because the two answer different questions. Somebody working
/// out what a machine can do wants the tab there, saying it cannot; somebody who already knows wants
/// it out of the way.
/// </remarks>
public enum UnavailableTabs : byte {

  /// <summary>Left in place, showing the reason there is nothing on it.</summary>
  Disabled = 0,

  /// <summary>Removed, so the strip only holds tabs with something behind them.</summary>
  Hidden,

}

/// <summary>
/// One process, in a window of its own (PRD §26).
/// </summary>
/// <remarks>
/// <para>
/// The detail pane at the foot of the main window follows the selection, so it can only ever show
/// the process that is selected now. This is the same pane pinned to one process, which is what
/// makes it possible to have two of them open and compare them — the thing Process Explorer is good
/// at and the reason §26 asks for a window rather than a pane.
/// </para>
/// <para>
/// It is no longer only that pane. §26 asks for pages the main window has no room for: what the
/// process <em>is</em> (§27), and an hour of what it has been doing (§28), plus the three resource
/// sheets that would each be twenty columns in the table. Those are built here and added to the
/// pane's own tab strip, so the window has one row of tabs rather than two.
/// </para>
/// <para>
/// Refreshed from the main window's sample tick. When the process ends the window stays, says so in
/// its title, and stops asking about a pid that now belongs to somebody else (PRD §86).
/// </para>
/// </remarks>
public sealed class ProcessPropertiesWindow : Form {

  private const string _GeneralTab = "General";
  private const string _PerformanceTab = "Performance";
  private const string _GpuTab = "GPU";
  private const string _MemoryMapTab = "Memory map";
  private const string _SecurityTab = "Security";
  private const string _ServicesTab = "Services";
  // The kernel's own word for it. "Jobs" is a Windows object and "container" is a convention built on
  // top of this one; naming the tab after either would be the false equivalence §5.3 forbids.
  private const string _CgroupTab = "cgroup";

  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private readonly DetailPane _pane;
  private readonly string _name;
  private readonly TabControl? _tabs;

  private readonly Panel _generalPage = new();
  private readonly Panel _buttons = new();
  private readonly Button _copy = new() { Text = "Copy" };
  private readonly Button _reveal = new() { Text = "Open folder" };
  private readonly Button _file = new() { Text = "File properties…" };

  private readonly ProcessFactsPage _general = new(
    ProcessField.Name,
    ProcessField.Pid,
    ProcessField.ParentPid,
    ProcessField.ParentName,
    ProcessField.State,
    ProcessField.UserName,
    ProcessField.EffectiveUserName,
    ProcessField.Elevated,
    ProcessField.SessionId,
    ProcessField.Terminal,
    ProcessField.StartTime,
    ProcessField.Priority,
    ProcessField.Nice,
    ProcessField.ThreadCount,
    // The descriptor count is not here. The table's column is filled on demand and reads "…" for
    // every process nobody has asked about, and this window has asked — it counts them every sample
    // for the graph. The freshly counted one goes in as an extra row below.
    ProcessField.Container,
    ProcessField.ImagePath,
    ProcessField.CommandLine
  );

  private readonly ProcessPerformancePage _performance = new();

  private readonly ProcessFactsPage _cpu = new(
    ProcessField.CpuPercent,
    ProcessField.CpuPercentPerCore,
    ProcessField.CpuTime,
    ProcessField.UserTime,
    ProcessField.KernelTime,
    ProcessField.CyclesDelta,
    ProcessField.ContextSwitchesDelta,
    ProcessField.LastCpu,
    ProcessField.ThreadCount,
    ProcessField.Priority,
    ProcessField.Nice
  );

  private readonly ProcessFactsPage _memory = new(
    ProcessField.PrivateBytes,
    ProcessField.PrivateBytesDelta,
    ProcessField.PrivateWorkingSet,
    ProcessField.WorkingSetBytes,
    ProcessField.PeakWorkingSet,
    ProcessField.VirtualBytes,
    ProcessField.PeakVirtualBytes,
    ProcessField.MemoryPercent,
    ProcessField.ProportionalSet,
    ProcessField.UniqueSet,
    ProcessField.SharedSet,
    ProcessField.FileBackedSet,
    ProcessField.Swap,
    ProcessField.ProportionalSwap,
    ProcessField.PageFaultsDelta,
    ProcessField.PagedPool,
    ProcessField.NonPagedPool
  );

  private readonly ProcessFactsPage _io = new(
    ProcessField.IoTotalRate,
    ProcessField.ReadBytesPerSecond,
    ProcessField.WriteBytesPerSecond
  );

  private readonly ProcessFactsPage _gpu = new(
    ProcessField.GpuPercent,
    ProcessField.GpuEngineName,
    ProcessField.GpuGraphicsPercent,
    ProcessField.GpuComputePercent,
    ProcessField.GpuCopyPercent,
    ProcessField.GpuEncodePercent,
    ProcessField.GpuDecodePercent,
    ProcessField.GpuDedicatedMemory,
    ProcessField.GpuSharedMemory,
    ProcessField.GpuTotalMemory,
    ProcessField.GpuAdapter
  );

  /// <summary>
  /// What confines the process (PRD §36).
  /// </summary>
  /// <remarks>
  /// Every field here is already in the sample — the uids and gids come off one line of
  /// <c>status</c> each, and the five capability sets off five more — so the page costs nothing to
  /// draw. The two that are not in the sample, the LSM label and the group list, cost a read apiece
  /// and arrive as extras below (PRD §5.4).
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
  /// The ceilings that belong to the group rather than to the process (PRD §38).
  /// </summary>
  /// <remarks>
  /// No fields, because none of this is a column of the process table: a quota, a memory cap and a
  /// throttle count are facts about the cgroup, and several processes share one. The answer to "why
  /// is this slow when the machine is idle".
  /// </remarks>
  private readonly ProcessFactsPage _cgroup = new();

  /// <summary>
  /// The unit this process belongs to, and what its unit file says (PRD §41).
  /// </summary>
  /// <remarks>
  /// No fields either, for the same reason the cgroup page has none: a restart policy and a unit file
  /// path are facts about the <em>service</em>, and several processes share one. What belongs to the
  /// process is which unit it is in, and that is a row on the General page.
  /// </remarks>
  private readonly ProcessFactsPage _services = new();

  private readonly ProcessMemoryMapPage _map;

  private TabPage? _gpuPage;
  private TabPage? _mapPage;
  private TabPage? _cgroupPage;
  private TabPage? _servicesPage;
  private ImageInfo? _image;
  private FileFacts? _imageFacts;
  private bool _imageRead;
  private bool _availabilitySettled;
  private string? _imagePath;

  /// <param name="actions">
  /// What may be done to the process from inside this window, or null for a read-only one.
  /// </param>
  /// <param name="unavailable">
  /// What to do with a tab this machine cannot fill — see <see cref="UnavailableTabs"/>.
  /// </param>
  /// <remarks>
  /// Without the actions the Threads tab's own menu — per-thread priority and affinity (PRD §25.2) —
  /// was built, drawn and inert here, because the pane bails when it holds no actions. The same pane
  /// docked at the foot of the main window had them, so the feature worked in one place and silently
  /// did nothing in the other.
  /// </remarks>
  public ProcessPropertiesWindow(
    ISystemProbe probe,
    ProcessKey key,
    string name,
    IProcessActions? actions = null,
    UnavailableTabs unavailable = UnavailableTabs.Disabled
  ) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this._actions = actions;
    this.Key = key;
    this._name = name;
    this.Unavailable = unavailable;
    this._pane = new(probe) { Actions = actions };
    this._map = new(probe, actions) { Key = key };

    this.Text = $"{name} ({key.Pid})";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    // Wide because of the thread tab: §29 carries twenty-one columns and the list scrolls up and
    // down but not sideways, so a column past the right-hand edge cannot be reached by scrolling at
    // all — only by dragging the window wider, which nobody knows to do.
    this.Bounds = new(0, 0, 1280, 640);
    // Without this the window can be grown and never shrunk: GTK computes a floor from the content
    // when none is named, and every docked child asks for the width it currently has.
    this.MinimumSize = new(700, 460);

    this.BuildGeneralPage();

    this._pane.Control.Dock = DockStyle.Fill;
    this.Controls.Add(this._pane.Control);

    // The pane's own tab strip, which is where these pages belong: one strip and not two. The cast
    // is the seam — DetailPane hands out a Control, and its collection has Add and Remove but no
    // Insert, so these land after the pane's own tabs and the window opens on General instead. A
    // test asserts every page is present, so an upstream change fails a build rather than shipping a
    // properties window with no properties on it.
    this._tabs = this._pane.Control as TabControl;
    if (this._tabs is not null) {
      AddPage(this._tabs, _GeneralTab, this._generalPage);
      AddPage(this._tabs, _PerformanceTab, this._performance.Control);
      AddPage(this._tabs, "CPU", this._cpu.Control);
      AddPage(this._tabs, "Memory", this._memory.Control);
      AddPage(this._tabs, "I/O", this._io.Control);
      this._gpuPage = AddPage(this._tabs, _GpuTab, this._gpu.Control);
      this._mapPage = AddPage(this._tabs, _MemoryMapTab, this._map.Control);
      AddPage(this._tabs, _SecurityTab, this._security.Control);
      this._cgroupPage = AddPage(this._tabs, _CgroupTab, this._cgroup.Control);
      this._servicesPage = AddPage(this._tabs, _ServicesTab, this._services.Control);
      this._tabs.SelectedTab = this.PageNamed(_GeneralTab);
      // The map is the one page whose cost is the size of the process, so it is filled when somebody
      // asks for it rather than when the window opens. The tick fills it too, for the same reason and
      // idempotently — a tab selected between two ticks must not wait a second to show anything.
      this._tabs.SelectedIndexChanged += (_, _) => this.FillVisiblePage();
    }

    this._pane.Select(key);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();

    static TabPage AddPage(TabControl tabs, string title, Control content) {
      var page = new TabPage(title);
      content.Dock = DockStyle.Fill;
      page.Controls.Add(content);
      tabs.TabPages.Add(page);
      return page;
    }
  }

  /// <summary>Which process this window is about. It never changes — that is the point of it.</summary>
  public ProcessKey Key { get; }

  /// <summary>True once the process has ended and the window has stopped following it.</summary>
  public bool Ended { get; private set; }

  /// <summary>What this window does with a tab whose capability the machine does not have.</summary>
  public UnavailableTabs Unavailable { get; }

  /// <summary>The captions on the tab strip, top to bottom — what a test reads instead of a picture.</summary>
  public IReadOnlyList<string> TabTitles {
    get {
      if (this._tabs is null)
        return [];

      var titles = new List<string>(this._tabs.TabPages.Count);
      foreach (var page in this._tabs.TabPages)
        titles.Add(page.Text);

      return titles;
    }
  }

  /// <summary>
  /// Brings one page to the front, by the caption on its tab.
  /// </summary>
  /// <remarks>
  /// For the capture run: a page nobody photographs is one whose layout regressions ship, and the
  /// graphs are the pages here most able to come up empty while every test around them passes
  /// (PRD §9.6).
  /// </remarks>
  public bool ShowPage(string title) {
    if (this._tabs is null || this.PageNamed(title) is not { } page)
      return false;

    this._tabs.SelectedTab = page;
    // Asking for a page is the request to fill it, which is the discipline the pane's own tabs
    // follow. Setting the selection from code does not always raise the toolkit's own event, and a
    // caller that had to remember to fill it afterwards is a caller that will not.
    this.FillVisiblePage();
    return true;
  }

  /// <summary>What the General page says, for a test with no display to read it off.</summary>
  public string GeneralText => this._general.Description;

  /// <summary>What the graphs are drawing, for the same reason (PRD §9.6).</summary>
  public string PerformanceText => this._performance.Description;

  /// <summary>What the Security page says (PRD §36).</summary>
  public string SecurityText => this._security.Description;

  /// <summary>What the cgroup page says (PRD §38).</summary>
  public string CgroupText => this._cgroup.Description;

  /// <summary>What the Services page says (PRD §41).</summary>
  public string ServicesText => this._services.Description;

  /// <summary>The sentence above the memory map, which is the half that explains an empty one.</summary>
  public string MemoryMapHeading => this._map.Heading;

  /// <summary>How many mappings the memory map is showing (PRD §34).</summary>
  public int MemoryMapRows => this._map.RowCount;

  /// <summary>How wide the graphs' axis is, in seconds (PRD §28).</summary>
  public int SpanSeconds => this._performance.SpanSeconds;

  /// <summary>Sets the width of the graphs' axis. 60, 300, 900 or 3600.</summary>
  public void SetSpan(int seconds) => this._performance.SetSpan(seconds);

  /// <summary>How far apart the samples are, so the axis says something true (PRD §45.4).</summary>
  public double SecondsPerSample {
    get => this._performance.SecondsPerSample;
    set => this._performance.SecondsPerSample = value;
  }

  /// <summary>
  /// The pane, for a caller that needs one of its own tabs or one of its dialogs.
  /// </summary>
  /// <remarks>
  /// The pane's tabs and this window's pages share one tab strip, so <see cref="ShowPage"/> reaches
  /// both and there is no second method for the pane's half of them.
  /// </remarks>
  public DetailPane Pane => this._pane;

  /// <summary>What the window holds, for a capture log with no display to read it off (PRD §9.6).</summary>
  public string DescribeForCapture()
    => $"detail window:{this.Text}, {this.Bounds.Width}x{this.Bounds.Height}\n" + this._pane.DescribeForCapture();

  /// <summary>
  /// Refreshes from the latest sample.
  /// </summary>
  /// <remarks>
  /// Identity, not pid: a window left open while its process exits must not start describing
  /// whatever the kernel handed that number to next (PRD §72.2).
  /// </remarks>
  public void UpdateFromSample(SystemSnapshot snapshot, SnapshotDelta delta, ProcessRow? row, Counter handles) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    if (this.Ended)
      return;

    var index = IndexOf(snapshot, this.Key);
    if (row is null || index < 0) {
      this.Ended = true;
      // Kept open rather than closed from under somebody who is reading it. The lists keep whatever
      // they last held, which is usually why the window was open in the first place.
      this.Text = $"{this._name} ({this.Key.Pid}) — ended";
      return;
    }

    ref readonly var process = ref snapshot.Processes[index];

    // A miss on the main window's on-demand map leaves default(Counter) behind, whose reason is None
    // — "the value is present" — so an uncounted process would claim to hold no descriptors at all.
    // The record's own count is the fallback, and the reason it carries is the truth about it.
    var descriptors = this.Descriptors(handles.HasValue ? handles : process.HandleCount);

    this._general.Update(row, this.GeneralExtras(in process, descriptors));
    this._cpu.Update(row);
    this._memory.Update(row);
    this._io.Update(row, [
      new("Read, total", Humanize.Bytes(process.ReadBytes)),
      new("Written, total", Humanize.Bytes(process.WriteBytes)),
      new("Other, total", Humanize.Bytes(process.OtherBytes)),
    ]);

    this.UpdateGpu(row, in process, delta, index);
    // Kept for the pages that are filled only when they are the one showing, which cannot ask the
    // sample for a row of their own.
    this._row = row;
    this._containerPath = process.ContainerPath;
    // Once, on the first sample. The cgroup read is a dozen small files, which is cheap enough to
    // spend on knowing whether there is anything to put on the tab — so the hidden preference has
    // its answer before somebody clicks it rather than after, the way the graphics tab's does. Every
    // tick after this it is filled only while its page is showing.
    if (!this._cgroupProbed)
      this.UpdateCgroup();

    this.FillVisiblePage();
    this._performance.Append(in process, delta, index, descriptors);
    this._performance.Refresh();

    this._pane.UpdateOverview(in process, row);
    this._pane.Refresh();

    // The window cannot see its own resize — a control outside the toolkit's assembly has no event
    // for it — so the layout runs on the tick as well. Both halves of it are no-ops once the size
    // has settled.
    this.ApplyLayout();
  }

  /// <summary>
  /// The descriptor count for the graph.
  /// </summary>
  /// <remarks>
  /// Counted here, every sample, for as long as this window is open. On Linux that makes the kernel
  /// walk the process's descriptor table, which is why it is not in the sample loop (PRD §5.4) — but
  /// this is one process rather than a thousand, and opening a properties window <em>is</em> the
  /// opt-in that §5.4 asks for.
  /// <para>
  /// It was collected only while the Performance page was the one showing, which is the discipline
  /// the lists follow and the wrong one for a graph: switching to the page then showed an hour of
  /// axis with nothing on it, because nothing had been counted until the moment somebody looked.
  /// </para>
  /// </remarks>
  private Counter Descriptors(Counter fallback) {
    var counted = this._probe.GetHandleCount(this.Key);
    return counted.Reason == UnknownReason.NotSampledYet ? fallback : counted;
  }

  /// <summary>
  /// Whether this machine has anything to say about a process's use of the graphics device.
  /// </summary>
  /// <remarks>
  /// Asked of the readings rather than of the platform: a build with the GPU code in it still has
  /// nothing to show on a machine whose driver publishes no per-process accounting, and "this build
  /// cannot" and "this machine will not" are answers a reader needs to tell apart (PRD §5.3).
  /// </remarks>
  private static bool HasGpuReadings(in ProcessRecord process, SnapshotDelta delta, int index)
    => process.GpuAdapter is { Length: > 0 }
      || process.GpuDedicatedBytes.HasValue
      || process.GpuBusyPercent.HasValue
      || delta.GpuEnginePercent(index).HasValue;

  private void UpdateGpu(ProcessRow row, in ProcessRecord process, SnapshotDelta delta, int index) {
    if (HasGpuReadings(in process, delta, index)) {
      this._gpu.Update(row);
      return;
    }

    // Settled once. A page removed and added back as the readings come and go would move every tab
    // to its right every time a driver went quiet for a second.
    if (!this._availabilitySettled) {
      this._availabilitySettled = true;
      if (this.Unavailable == UnavailableTabs.Hidden && this._gpuPage is { } page && this._tabs is not null) {
        this._tabs.TabPages.Remove(page);
        this._gpuPage = null;
        return;
      }
    }

    this._gpu.ShowUnavailable(
      process.GpuAdapterReason == UnknownReason.NotSupportedOnPlatform
        ? "this platform has no per-process graphics accounting"
        : "no driver on this machine reports what a process uses the graphics device for"
    );
  }

  #region the Security page (PRD §36)

  /// <summary>
  /// What confines the process: the identity from the sample, and the two things that cost a read.
  /// </summary>
  /// <remarks>
  /// The two extras are read on every tick while the window is open rather than once, because unlike
  /// the image they can change under a running process: a program may drop groups, and a label
  /// changes at an <c>exec</c>. Two small files for one process is not a cost worth caching wrongly.
  /// </remarks>
  private void UpdateSecurity(ProcessRow row) {
    var extras = new List<KeyValuePair<string, string>>();
    var security = this._probe.DescribeSecurity(this.Key);

    extras.Add(new("Security module", Label(security)));
    extras.Add(new("Supplementary groups", Groups(security)));

    // The namespaces, from the image description this window already reads once. They are where a
    // container actually is: two processes sharing an inode share that namespace, which is a harder
    // fact than a cgroup path anybody may write (PRD §14, §36).
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

  #endregion

  #region the cgroup page (PRD §38)

  /// <summary>
  /// What the process's cgroup allows it and what it is using.
  /// </summary>
  /// <remarks>
  /// Read every tick, unlike the map: this is a dozen small files rather than a page-table walk, and
  /// half of what is on the page — the memory in use, the throttle count, the pressure figures —
  /// moves while somebody watches it. The other half does not, and reading them together is cheaper
  /// than deciding which is which.
  /// </remarks>
  private void UpdateCgroup() {
    this._cgroupProbed = true;
    if (this._probe.DescribeCgroup(this.Key) is not { } cgroup) {
      this.SettleCgroupTab();
      this._cgroup.ShowUnavailable(
        "This process is in no cgroup this build can read. Only the unified hierarchy is read; "
        + "cgroup v1 splits a process across several hierarchies and no single one of them answers this."
      );

      return;
    }

    // The two kinds of ceiling are never added together and never share a heading: RLIMIT_NPROC is a
    // limit on the user, pids.max is a limit on the cgroup, and one combined number would be the
    // false equivalence §5.3 forbids. The process's own ceilings are on the Limits dialog (§25.2).
    this._cgroup.Update([
      new("cgroup", cgroup.Path),
      new("Controllers enabled here", cgroup.Controllers.Count > 0 ? string.Join(", ", cgroup.Controllers) : "none"),
      new("Processor", Limited(cgroup, "cpu", Cores(cgroup.CpuQuotaCores))),
      new("Throttled", Limited(cgroup, "cpu", Humanize.Count(cgroup.ThrottledCount))),
      new("Memory in use", Humanize.Bytes(cgroup.MemoryCurrentBytes)),
      new("Memory, hard cap", Limited(cgroup, "memory", Limit(cgroup.MemoryMaxBytes))),
      new("Memory, soft cap", Limited(cgroup, "memory", Limit(cgroup.MemoryHighBytes))),
      // Tasks and not processes, which is not a quibble: pids.current counts threads. The cgroup
      // this program's own window was in reported 892 against 58 entries in cgroup.procs — a figure
      // fifteen times the one a row headed "processes" would have been read as. It is what systemd
      // calls TasksMax for the same reason (PRD §5.3).
      // The explanation goes in the value and not in the label: the label column is a fixed 220
      // pixels, and "Tasks — processes and their threads" photographed running into the number
      // beside it.
      new("Tasks", $"{Humanize.Count(cgroup.PidsCurrent)} — a task is a thread, so this counts every thread of every process in the group"),
      new("Tasks, limit", Limited(cgroup, "pids", Limit(cgroup.PidsMax))),
      new("Stalled on CPU", Pressure(cgroup.CpuPressure)),
      new("Stalled on memory", Pressure(cgroup.MemoryPressure)),
      new("Stalled on I/O", Pressure(cgroup.IoPressure)),
      new("Frozen", Frozen(cgroup.Freezer)),
    ]);
  }

  /// <summary>
  /// Takes the cgroup tab off the strip once, where that is the preference.
  /// </summary>
  /// <remarks>
  /// Settled once, like the GPU tab's: a page removed and added back as a process moved between
  /// cgroups would shift every tab to its right while somebody was reading one.
  /// </remarks>
  private void SettleCgroupTab() {
    if (this._cgroupSettled)
      return;

    this._cgroupSettled = true;
    if (this.Unavailable != UnavailableTabs.Hidden || this._cgroupPage is not { } page || this._tabs is null)
      return;

    this._tabs.TabPages.Remove(page);
    this._cgroupPage = null;
  }

  private bool _cgroupSettled;

  /// <summary>Whether the cgroup has been asked about at all yet — see the first sample above.</summary>
  private bool _cgroupProbed;

  /// <summary>
  /// A quota as a number of cores, because that is the sentence somebody wants.
  /// </summary>
  /// <remarks>
  /// No quota is <em>unlimited</em> rather than a very large number: unlimited is not a quantity, and
  /// this is the difference between a process being held back and one that simply is not busy.
  /// </remarks>
  private static string Cores(double? quota)
    => quota is { } cores
      ? string.Format(CultureInfo.InvariantCulture, "{0:0.##} core{1}", cores, cores == 1 ? string.Empty : "s")
      : "unlimited";

  private static string Limit(Counter counter) => counter.HasValue ? Humanize.Bytes(counter) : "no limit";

  /// <summary>
  /// A ceiling, but only where the controller that enforces it is switched on here.
  /// </summary>
  /// <remarks>
  /// A limit file existing is not the same as its controller being enabled, and the reader cannot
  /// tell an absent file from the literal word <c>max</c> — both arrive as "no value". Where the
  /// controller is off, "no limit" would be an outright false statement: a delegated cgroup with
  /// <c>memory</c> and without <c>cpu</c> is still held to whichever ancestor's processor quota, and
  /// that is exactly the case somebody opens this page to find (PRD §38).
  /// </remarks>
  private static string Limited(CgroupInfo cgroup, string controller, string value)
    => cgroup.Has(controller)
      ? value
      : $"the {controller} controller is not enabled here — an ancestor's limit applies instead";

  private static string Pressure(PressureReading reading)
    => reading.Some.HasValue
      ? $"{Humanize.Percent(reading.Some.Average10)} % · {Humanize.Percent(reading.Some.Average60)} % · {Humanize.Percent(reading.Some.Average300)} % (10 s · 1 min · 5 min)"
      : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

  /// <summary>
  /// Whether every process in the cgroup is stopped.
  /// </summary>
  /// <remarks>
  /// Nothing in a process table will say so: there is no process state for frozen, and a frozen task
  /// reports itself as sleeping whatever it was doing when the freeze landed (PRD §25.1, §38).
  /// </remarks>
  private static string Frozen(CgroupFreezer? freezer) => freezer switch {
    { Supported: false } or null => "this kernel's cgroups have no freezer",
    { Frozen: true } => "yes — every process in it is stopped, and each still reports itself as sleeping",
    _ => "no",
  };

  #endregion

  #region the Services page (PRD §41)

  /// <summary>
  /// Which service this process belongs to, and what that service's unit file says.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read once, when the page is first asked for, and not again. The reading is a walk of every unit
  /// file on the machine — 372 of them here — which is far too much to spend on a tick, and it does
  /// not need spending twice: a process cannot move between units while it runs, and a unit that
  /// stops takes its processes with it, so this window would say "ended" rather than showing a stale
  /// service. What can change underneath it is somebody running <c>systemctl disable</c> in another
  /// window, and that is a fair price for not walking a thousand files a second (PRD §5.4).
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

    this._servicesRead = true;
    var services = this._probe.GetServices();
    if (services.Count == 0) {
      // No service manager this build can read — which is a fact about the machine, not about this
      // process, and so is the one case the tab may be taken off the strip.
      this.SettleServicesTab();
      this._services.ShowUnavailable(
        "Nothing on this machine publishes services in a form this build reads. Only systemd is read, "
        + "from the unit files and the cgroup tree rather than over D-Bus."
      );

      return;
    }

    if (CgroupUnit.Of(this._containerPath) is not { } unit) {
      // A finding rather than a hole, and the tab stays: most of a desktop is like this. A slice is
      // deliberately not a unit for this purpose — it holds no processes of its own — so a cgroup
      // with only slices in it answers nothing rather than the nearest thing that looks like one.
      this._services.Update([
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

    if (Find(services, unit) is not { } service) {
      // The cgroup names a unit that the unit-file walk did not produce: a transient scope systemd
      // made without a file on disk is the usual one. The name is still the truth about the process,
      // so it is reported, and the absence is explained rather than shown as an empty page.
      this._services.Update([
        new("Service", unit),
        new("Unit file", "none on disk — a transient unit, created at runtime and never written out"),
        new("Read from", "the cgroup this process is in, which is what a systemd unit is"),
      ]);

      return;
    }

    this._services.Update([
      new("Service", service.Name),
      new("Description", service.Description is { Length: > 0 } text ? text : "—"),
      new("State", service.State == ServiceState.Unknown ? "unknown" : service.State.ToString().ToLowerInvariant()),
      new("Starts at boot", StartsAtBoot(service)),
      // Its own row and not folded into the one above: masked units can never run whatever else is
      // configured, and it is the setting people forget they made.
      new("Masked", service.Masked ? "yes — it can never be started while this stands" : "no"),
      new("Main process", MainProcess(service, this.Key.Pid)),
      new("Restart policy", service.RestartPolicy is { Length: > 0 } policy ? policy : "—"),
      new("Command", service.Command is { Length: > 0 } command ? command : "—"),
      new("Unit file", service.Path),
    ]);
  }

  /// <summary>The unit of that name, or null when the walk of the unit files did not produce one.</summary>
  private static ServiceRecord? Find(IReadOnlyList<ServiceRecord> services, string unit) {
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
  /// Takes the Services tab off the strip once, on a machine with no service manager to read.
  /// </summary>
  /// <remarks>
  /// Only for that case. A process under no unit is a finding about the process and keeps its tab,
  /// the same way a GPU tab stays on a machine whose driver simply says nothing about one process.
  /// </remarks>
  private void SettleServicesTab() {
    if (this.Unavailable != UnavailableTabs.Hidden || this._servicesPage is not { } page || this._tabs is null)
      return;

    this._tabs.TabPages.Remove(page);
    this._servicesPage = null;
  }

  private bool _servicesRead;

  /// <summary>
  /// The cgroup path from the last sample, which is what the unit is looked up by.
  /// </summary>
  /// <remarks>
  /// Kept rather than read off the row's Container cell: that cell is formatted for a table, and
  /// looking a unit up by our own formatting is how the two quietly stop agreeing.
  /// </remarks>
  private string? _containerPath;

  #endregion

  #region the Memory map page (PRD §34)

  /// <summary>
  /// Fills the page somebody is actually looking at.
  /// </summary>
  /// <remarks>
  /// The three pages that cost a read to fill, and the only three that are not refilled from a sample
  /// already taken. Nothing is collected for a tab nobody has opened, which is the discipline the
  /// pane's own tabs follow and the reason this is a method rather than three calls in the tick
  /// (PRD §5.4).
  /// <para>
  /// They are not the same kind of expensive, so they are not filled the same way. The map is a walk
  /// of the process's page tables and is done once, with a button for a fresher one; the other two
  /// are two or three small files and are re-read on every tick while their page is showing, because
  /// a cgroup's memory, throttle count and pressure all move while somebody watches them.
  /// </para>
  /// </remarks>
  private void FillVisiblePage() {
    if (this._tabs?.SelectedTab is not { } page)
      return;

    if (string.Equals(page.Text, _MemoryMapTab, StringComparison.Ordinal)) {
      this._map.EnsureFilled();
      this.SettleMapTab();
      return;
    }

    if (string.Equals(page.Text, _SecurityTab, StringComparison.Ordinal)) {
      if (this._row is { } row)
        this.UpdateSecurity(row);

      return;
    }

    if (string.Equals(page.Text, _CgroupTab, StringComparison.Ordinal)) {
      this.UpdateCgroup();
      return;
    }

    if (string.Equals(page.Text, _ServicesTab, StringComparison.Ordinal))
      this.UpdateServices();
  }

  /// <summary>The last sample's row, for the pages that are filled only while they are showing.</summary>
  private ProcessRow? _row;

  /// <summary>
  /// Takes the map off the strip once, on a platform that does not read one.
  /// </summary>
  /// <remarks>
  /// On the state and not on the row count. A refused read and a kernel thread both give nought rows
  /// and neither is a statement about this build's capability — the tab stays for both of those,
  /// saying which it was (PRD §26).
  /// </remarks>
  private void SettleMapTab() {
    if (this._mapSettled || this._map.State != MemoryMapState.NotImplemented)
      return;

    this._mapSettled = true;
    if (this.Unavailable != UnavailableTabs.Hidden || this._mapPage is not { } page || this._tabs is null)
      return;

    this._tabs.TabPages.Remove(page);
    this._mapPage = null;
  }

  private bool _mapSettled;

  #endregion

  /// <summary>
  /// The facts about a process that are not fields of the table.
  /// </summary>
  /// <remarks>
  /// The image is read once and kept. Its ELF header and its directory entry do not change under a
  /// running process — and if the file on disk is replaced, the mapping in memory is still the old
  /// one, which is a fact the Modules page reports rather than something to re-read every second.
  /// </remarks>
  private List<KeyValuePair<string, string>> GeneralExtras(in ProcessRecord process, Counter descriptors) {
    var extras = new List<KeyValuePair<string, string>> {
      new("Handles", Humanize.Count(descriptors)),
      new("Running for", Uptime(process.StartTimeUtcTicks)),
      // The service association §27 named as the one thing on this page that could be answered and
      // was not. It costs nothing: the cgroup is already in the sample, and a systemd unit is a
      // cgroup — the same join §40's owning-service column makes, through the same code. What the
      // unit itself says is a page of its own, because it is a fact about the service rather than
      // about this process.
      new("Service", CgroupUnit.Of(process.ContainerPath) ?? "none — this process is under no systemd unit"),
    };

    if (!this._imageRead) {
      this._imageRead = true;
      this._imagePath = process.ImagePath;
      this._image = this._probe.DescribeImage(this.Key);
      // The directory entry with it, and once. This was a stat of the image on every sample for as
      // long as the window stayed open, under a comment claiming it was read once — which is the
      // more expensive half of the two, and the half nothing would ever have noticed.
      if (this._imagePath is { Length: > 0 } path)
        this._imageFacts = FileFacts.Describe(path);
    }

    if (this._image is { } image) {
      extras.Add(new("Architecture", image.Architecture ?? (image.HeaderRead ? "unknown" : "—")));
      extras.Add(new("Interpreter", image.Interpreter ?? (image.HeaderRead ? "statically linked" : "—")));
      extras.Add(new("Working directory", image.WorkingDirectory ?? "—"));
      // From the module list rather than from what the program calls itself, which is the whole
      // reason it is worth a row: a .NET application and a shell script that launches one have the
      // same name and are not the same thing (PRD §14, §80).
      if (image.Runtime != ProcessRuntime.Unknown)
        extras.Add(new("Running", image.Runtime.Text()));
    }

    if (this._imageFacts is { } facts) {
      extras.Add(new("Image size", FileFactsFormatting.Size(in facts)));
      extras.Add(new("Image modified", FileFactsFormatting.Modified(in facts)));
      extras.Add(new("Image permissions", facts.Permissions ?? "n/a"));
    }

    // Only where the file system carries a birth time, which most do not. A row saying so on most
    // machines would be a row of dashes; the fact that there is no row is the answer, and unlike a
    // signature it is not one anybody would read as reassurance (PRD §14).
    if (this._image?.CreatedUtc is { } created)
      extras.Add(new("Image created", created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z"));

    // Said rather than left blank. A properties window with no signature row reads as one that
    // checked and found nothing wrong, which is the one thing this must never imply (PRD §70).
    extras.Add(new("Signature", "not read — this build verifies no signatures"));
    return extras;
  }

  /// <summary>How long the process has been up, from its start time.</summary>
  /// <remarks>
  /// A start time answers "when" and this answers "how long", and on a machine that has been up for
  /// three weeks those are not the same glance.
  /// </remarks>
  private static string Uptime(long startTimeUtcTicks) {
    if (startTimeUtcTicks <= 0)
      return "—";

    var span = DateTime.UtcNow - new DateTime(startTimeUtcTicks, DateTimeKind.Utc);
    if (span < TimeSpan.Zero)
      return "—";

    return span.TotalDays >= 1
      ? string.Format(CultureInfo.InvariantCulture, "{0:0} d {1:00} h {2:00} m", (int)span.TotalDays, span.Hours, span.Minutes)
      : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds);
  }

  private static int IndexOf(SystemSnapshot snapshot, ProcessKey key) {
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i)
      if (processes[i].Key == key)
        return i;

    return -1;
  }

  private TabPage? PageNamed(string title) {
    if (this._tabs is null)
      return null;

    foreach (var page in this._tabs.TabPages)
      if (string.Equals(page.Text, title, StringComparison.Ordinal))
        return page;

    return null;
  }

  #region the General page (PRD §27)

  private void BuildGeneralPage() {
    this._general.Control.Dock = DockStyle.Fill;
    this._buttons.Dock = DockStyle.Bottom;
    this._buttons.Height = 40;

    this._copy.Click += (_, _) => Clipboard.SetText(this._general.Description);
    this._reveal.Click += (_, _) => this.Reveal();
    this._file.Click += (_, _) => this.ShowFileProperties();

    this._buttons.Controls.Add(this._copy);
    this._buttons.Controls.Add(this._reveal);
    this._buttons.Controls.Add(this._file);

    // The list is added after the strip: docked children claim their edge in the order they are
    // added and a Fill child takes what is left, so the strip gets its band and the list the rest.
    this._generalPage.Controls.Add(this._buttons);
    this._generalPage.Controls.Add(this._general.Control);
  }

  private void Reveal() {
    if (this._imagePath is not { Length: > 0 } path) {
      MessageBox.Show($"{this._name} has no readable executable. A kernel thread has none, and another user's is not readable without privilege.", "Process Manager");
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (DesktopOpen.Reveal(path) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the folder to.", "Process Manager");
      return;
    }

    var result = this._actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
  }

  private void ShowFileProperties() {
    if (this._imagePath is not { Length: > 0 } path) {
      MessageBox.Show($"{this._name} has no readable executable to describe.", "Process Manager");
      return;
    }

    var extra = new List<KeyValuePair<string, string>>();
    if (this._image is { } image) {
      extra.Add(new("architecture", image.Architecture ?? (image.HeaderRead ? "unknown" : "—")));
      extra.Add(new("interpreter", image.Interpreter ?? (image.HeaderRead ? "statically linked" : "—")));
      extra.Add(new("directory", image.WorkingDirectory ?? "—"));
    }

    new FilePropertiesDialog(path, extra, this._actions).ShowDialog();
  }

  #endregion

  /// <summary>
  /// The layout the toolkit cannot do for us.
  /// </summary>
  /// <remarks>
  /// A control outside the toolkit's assembly cannot observe its own resize, so the window lays out
  /// its pages: the button strip by arithmetic, and the graphs by tiling. Both are no-ops once the
  /// size has settled.
  /// </remarks>
  public void ApplyLayout() {
    const int Margin = 10;
    var y = (this._buttons.Height - 28) / 2;
    this._copy.Bounds = new(Margin, y, 90, 28);
    this._reveal.Bounds = new(Margin + 100, y, 120, 28);
    this._file.Bounds = new(Margin + 230, y, 150, 28);

    foreach (var page in (ReadOnlySpan<ProcessFactsPage>)[
      this._general,
      this._cpu,
      this._memory,
      this._io,
      this._gpu,
      this._security,
      this._cgroup,
      this._services,
    ])
      page.Stretch();

    this._map.Stretch();
    this._performance.Refresh();
  }

}
