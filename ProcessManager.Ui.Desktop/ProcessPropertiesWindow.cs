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
  /// The ceilings that belong to the group rather than to the process (PRD §38).
  /// </summary>
  /// <remarks>
  /// No fields, because none of this is a column of the process table: a quota, a memory cap and a
  /// throttle count are facts about the cgroup, and several processes share one. The answer to "why
  /// is this slow when the machine is idle".
  /// </remarks>
  private readonly ProcessFactsPage _cgroup = new();

  private TabPage? _gpuPage;
  private TabPage? _cgroupPage;
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
    // The memory map, the window list, the security context and the unit are the pane's four pages
    // now, not this window's. §26 asks for one row of tabs and not two, so a window that hosts the
    // pane and added its own Security page would have put two tabs of that name on one strip — and
    // the main window's lower pane gets the same four modes out of it, which is what §10 asked for.
    this._pane = new(probe) {
      Actions = actions,
      ProcessName = name,
      Unavailable = unavailable,
    };

    this._pane.Select(key);

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
      this._cgroupPage = AddPage(this._tabs, _CgroupTab, this._cgroup.Control);
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
      // The tab carries the title and the table inside it carries nothing, so a reader who moves off
      // the strip into the page is told only that it is a table. Named from the tab it is under,
      // unless the page named itself something better (PRD §74).
      content.AccessibleName ??= title;
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

  /// <summary>
  /// Which fields the performance page keeps an hour of (PRD §28).
  /// </summary>
  /// <remarks>
  /// Public because the catalogue declares the same thing — a field kept per process says so — and
  /// the two are held to each other by a test rather than by anybody remembering (PRD §5.1).
  /// </remarks>
  public static IReadOnlyList<ProcessField> PlottedFields => ProcessPerformancePage.Plotted;

  /// <summary>What the General page says, for a test with no display to read it off.</summary>
  public string GeneralText => this._general.Description;

  /// <summary>What the graphs are drawing, for the same reason (PRD §9.6).</summary>
  public string PerformanceText => this._performance.Description;

  /// <summary>
  /// What the Security page says (PRD §36).
  /// </summary>
  /// <remarks>
  /// Forwarded to the pane, which owns that page and the three beside it. Kept on this window because
  /// it is what a test and the capture log read, and moving where the page lives is not a reason to
  /// move where the test looks.
  /// </remarks>
  public string SecurityText => this._pane.SecurityText;

  /// <summary>What the cgroup page says (PRD §38).</summary>
  public string CgroupText => this._cgroup.Description;

  /// <summary>What the Services page says (PRD §41).</summary>
  public string ServicesText => this._pane.ServicesText;

  /// <summary>The sentence above the memory map, which is the half that explains an empty one.</summary>
  public string MemoryMapHeading => this._pane.MemoryMapHeading;

  /// <summary>How many mappings the memory map is showing (PRD §34).</summary>
  public int MemoryMapRows => this._pane.MemoryMapRows;

  /// <summary>The sentence above the window list — the half that explains an empty one (PRD §39).</summary>
  public string WindowsHeading => this._pane.WindowsHeading;

  /// <summary>How many windows this process has on screen, as the page last read them (PRD §39).</summary>
  public int WindowRows => this._pane.WindowRows;

  /// <summary>The graphs themselves, for a caller that wants to point at a moment (PRD §28).</summary>
  public IReadOnlyList<HistoryPlot> PerformancePlots => this._performance.Plots;

  /// <summary>What the strip under the graphs says — the axis, or the readings under the cursor.</summary>
  public string PerformanceFooter => this._performance.Footer;

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
    // Once, on the first sample. The cgroup read is a dozen small files, which is cheap enough to
    // spend on knowing whether there is anything to put on the tab — so the hidden preference has
    // its answer before somebody clicks it rather than after, the way the graphics tab's does. Every
    // tick after this it is filled only while its page is showing.
    if (!this._cgroupProbed)
      this.UpdateCgroup();

    this.FillVisiblePage();
    this._performance.Append(in process, delta, index, descriptors);
    this._performance.Refresh();

    // The overview call is also what hands the pane the row and the cgroup path its own four pages
    // need, so it comes before the refresh that fills whichever of them is showing.
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
    var rows = new List<KeyValuePair<string, string>> {
      new("cgroup", cgroup.Path),
      new("Container", Container(cgroup.Path)),
      new("Controllers enabled here", cgroup.Controllers.Count > 0 ? string.Join(", ", cgroup.Controllers) : "none"),
      new("Processor", Limited(cgroup, "cpu", Cores(cgroup.CpuQuotaCores))),
      new("Processor, in force", EffectiveCores(cgroup)),
      new("Throttled", Limited(cgroup, "cpu", Humanize.Count(cgroup.ThrottledCount))),
      new("Memory in use", Humanize.Bytes(cgroup.MemoryCurrentBytes)),
      new("Memory, hard cap", Limited(cgroup, "memory", Limit(cgroup.MemoryMaxBytes))),
      new("Memory, soft cap", Limited(cgroup, "memory", Limit(cgroup.MemoryHighBytes))),
      new("Memory, in force", Effective(cgroup.TightestMemoryLimit(), static value => Humanize.Bytes(value))),
      // Tasks and not processes, which is not a quibble: pids.current counts threads. The cgroup
      // this program's own window was in reported 892 against 58 entries in cgroup.procs — a figure
      // fifteen times the one a row headed "processes" would have been read as. It is what systemd
      // calls TasksMax for the same reason (PRD §5.3).
      // The explanation goes in the value and not in the label: the label column is a fixed 220
      // pixels, and "Tasks — processes and their threads" photographed running into the number
      // beside it.
      new("Tasks", $"{Humanize.Count(cgroup.PidsCurrent)} — a task is a thread, so this counts every thread of every process in the group"),
      new("Tasks, limit", Limited(cgroup, "pids", TaskLimit(cgroup.PidsMax))),
      new("Tasks, in force", Effective(cgroup.TightestTaskLimit(), static value => Humanize.Count(value))),
    };

    // One row per capped device rather than one row headed "disk". The limit is per device — a group
    // may be held to a megabyte a second on the disk its database is on and left alone on the one
    // its logs are on — and a single figure could not say that (PRD §38).
    rows.Add(new("Disk, allowed here", DiskLimits(cgroup)));
    foreach (var limit in cgroup.Io)
      if (limit.IsLimited)
        rows.Add(new($"  {limit.Name}", Device(limit)));

    rows.Add(new("Stalled on CPU", Pressure(cgroup.CpuPressure)));
    rows.Add(new("Stalled on memory", Pressure(cgroup.MemoryPressure)));
    rows.Add(new("Stalled on I/O", Pressure(cgroup.IoPressure)));
    rows.Add(new("Frozen", Frozen(cgroup.Freezer)));

    // The chain last, because it is the explanation for the three "in force" rows above rather than
    // a reading of its own: a reader who has seen a ceiling they did not set comes down here to find
    // out who did (PRD §38).
    if (cgroup.Chain.Count > 0) {
      rows.Add(new("Hierarchy", $"{cgroup.Chain.Count.ToString(CultureInfo.InvariantCulture)} levels, outermost first — each one's limit applies to everything below it"));

      // The path goes in the value and the label carries only the depth. The label column is a fixed
      // 220 pixels and a cgroup path is routinely three times that: the first version put the path in
      // the label, and the capture showed "/user.slice/user-1000.slice," with the next level's limit
      // starting where the rest of the path had been cut off.
      for (var level = 0; level < cgroup.Chain.Count; ++level)
        rows.Add(new(
          $"  {(level + 1).ToString(CultureInfo.InvariantCulture)}",
          $"{cgroup.Chain[level].Path} — {Level(cgroup.Chain[level])}"
        ));
    }

    this._cgroup.Update(rows);
  }

  /// <summary>
  /// What is running this process, where something other than the machine is (PRD §38).
  /// </summary>
  /// <remarks>
  /// The runtime always, and then whichever of the id and the name the machine itself can answer.
  /// Docker and its relatives keep the name in a daemon rather than on the filesystem, and the row
  /// says so instead of leaving a blank that reads as "it has no name".
  /// </remarks>
  private static string Container(string? path) {
    var container = ContainerDetector.Of(path);
    if (!container.IsIdentified)
      return container.Runtime == ContainerRuntime.None
        ? "no — the cgroup path names none, though a chroot or a bare namespace would look like this too"
        : "not known";

    if (container.Name is { Length: > 0 } name)
      return $"{container.RuntimeName} · {name}";

    return container.Id is { Length: > 0 } id
      ? $"{container.RuntimeName} · {id} — the name is in the runtime's own daemon rather than on this machine"
      : container.RuntimeName;
  }

  /// <summary>The tightest quota anywhere in the chain, and which cgroup imposes it.</summary>
  private static string EffectiveCores(CgroupInfo cgroup) {
    if (cgroup.Chain.Count == 0)
      return "the hierarchy above this cgroup was not read";

    var (cores, path, unit) = cgroup.TightestCpuQuota();
    return cores is null
      ? "no quota anywhere above it either"
      : $"{Cores(cores)} — set by {unit ?? path}";
  }

  /// <summary>A ceiling from the chain, with the level that set it.</summary>
  private static string Effective(CgroupCeiling ceiling, Func<Counter, string> format) {
    if (ceiling.Path is null)
      return ceiling.Value.Reason == UnknownReason.NoLimit
        ? "no limit anywhere above it either"
        : "no cgroup above it has that controller on";

    return $"{format(ceiling.Value)} — set by {ceiling.Unit ?? ceiling.Path}";
  }

  private static string DiskLimits(CgroupInfo cgroup) => cgroup.IoLimitsReason switch {
    UnknownReason.None => $"{cgroup.Io.Count.ToString(CultureInfo.InvariantCulture)} device(s) capped",
    UnknownReason.NoLimit => "the io controller is on here and nothing is capped",
    _ => "the io controller is not enabled here — an ancestor's throttling applies instead",
  };

  private static string Device(CgroupIoLimit limit) => string.Join(" · ", (string[])[
    $"read {Rate(limit.ReadBytesPerSecond, "/s")}",
    $"write {Rate(limit.WriteBytesPerSecond, "/s")}",
    $"read {Operations(limit.ReadOperationsPerSecond)}",
    $"write {Operations(limit.WriteOperationsPerSecond)}",
  ]);

  private static string Rate(Counter counter, string suffix)
    => counter.HasValue ? Humanize.Bytes(counter) + suffix : "unlimited";

  private static string Operations(Counter counter)
    => counter.HasValue ? Humanize.Count(counter) + " ops/s" : "unlimited ops/s";

  /// <summary>One level of the chain, in one line: what it sets, and nothing about what it does not.</summary>
  private static string Level(CgroupLevel level) {
    var parts = new List<string>();
    if (level.CpuQuotaCores is { } cores)
      parts.Add(Cores(cores));

    if (level.MemoryMaxBytes.HasValue)
      parts.Add(Humanize.Bytes(level.MemoryMaxBytes) + " memory");

    if (level.PidsMax.HasValue)
      parts.Add(Humanize.Count(level.PidsMax) + " tasks");

    foreach (var limit in level.IoLimits)
      if (limit.IsLimited)
        parts.Add(limit.Name + " capped");

    return parts.Count == 0 ? "sets no limit" : string.Join(" · ", parts);
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
  /// A ceiling that counts things rather than measuring them.
  /// </summary>
  /// <remarks>
  /// <c>pids.max</c> went through the byte formatter, which divides by 1024 and appends a binary
  /// suffix: a limit of 153 425 tasks appeared as <c>150K</c>, which is the wrong number in a unit
  /// tasks do not have. They are counted one at a time (PRD §5.3).
  /// </remarks>
  private static string TaskLimit(Counter counter) => counter.HasValue ? Humanize.Count(counter) : "no limit";

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

  #region what the tick fills, and what waits to be asked for

  /// <summary>
  /// Fills the page somebody is actually looking at.
  /// </summary>
  /// <remarks>
  /// One page now rather than five. The memory map, the security context, the unit and the window
  /// list moved onto the pane with the tabs that show them, and the pane fills whichever of its own
  /// is up; what is left here is the cgroup, which is this window's and not a mode of the lower pane
  /// (PRD §10, §26).
  /// <para>
  /// It is re-read on every tick while its page is showing, unlike the map: this is a dozen small
  /// files rather than a page-table walk, and half of what is on it — the memory in use, the throttle
  /// count, the pressure figures — moves while somebody watches it (PRD §5.4).
  /// </para>
  /// </remarks>
  private void FillVisiblePage() {
    if (this._tabs?.SelectedTab is { } page && string.Equals(page.Text, _CgroupTab, StringComparison.Ordinal))
      this.UpdateCgroup();
  }

  #endregion

  /// <summary>
  /// The facts about a process that are not fields of the table.
  /// </summary>
  /// <remarks>
  /// The image is read once and kept. Its ELF header and its directory entry do not change under a
  /// running process — and if the file on disk is replaced, the mapping in memory is still the old
  /// one, which is a fact the Modules page reports rather than something to re-read every second.
  /// </remarks>
  /// <summary>
  /// Which process is holding the file lock this one is queued behind, if any.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Read when the row is refreshed rather than cached: the whole reason to look is that a process
  /// is stuck now, and a remembered answer would go on saying so after it was freed. The table is
  /// small — sixty-odd rows on an ordinary desktop — and this is one open of one file.
  /// </para>
  /// <para>
  /// The wording says what was and was not looked at. "Nothing is holding a file lock it wants" is
  /// what the table supports; it is not "this process is not blocked", because a process waiting on
  /// a futex, a pipe or a socket is blocked and is not in this table at all. Saying the stronger
  /// thing would be exactly the false equivalence §5.3 forbids.
  /// </para>
  /// </remarks>
  private string BlockedBy(int pid) {
    var waits = this._probe.DescribeLockWaits();
    if (!waits.TryGetValue(pid, out var holder))
      return "nothing is holding a file lock this process wants";

    // The pid alone. This window has the probe and not the table, and naming the holder would mean
    // sampling the machine again from here just to turn a number into a word — the main window's
    // "Go to" already does that, and it is one step away.
    return $"PID {holder.ToString(CultureInfo.InvariantCulture)}, on a file lock";
  }

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
      // The one wait chain a kernel states outright, and the answer to "why is this hanging" for the
      // case it can answer it for. A thread's wait channel says what a process is blocked in and
      // stops there — nothing publishes who holds a futex — but the lock table lists every waiter
      // beside the holder it is queued behind, both by pid (PRD §33, §91).
      new("Blocked by", this.BlockedBy(process.Pid)),
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

    // Said rather than left blank, and all three of them. A properties window with no signature row
    // reads as one that checked and found nothing wrong, which is the one thing this must never
    // imply (PRD §70) — and the package an image came from and the digest of its bytes are the same
    // kind of silence: questions this page does not ask rather than questions with no answer.
    //
    // Not asked here because each costs the size of the file, or a walk of every installed package's
    // file list, and a properties window that stalled for a second on opening is what §5.4 exists to
    // prevent. They are one button away, which is where a reading somebody has to ask for belongs
    // (PRD §5.2, §27).
    //
    // "not checked" rather than the old "this build verifies no signatures", which was untrue on
    // both platforms by the time it was read: the button behind it verifies an Authenticode
    // signature on Windows, and on Linux reports what the packaging system recorded — the digest the
    // package kept and who validated the package — which is the whole of what an ELF admits of.
    extras.Add(new("Signature", "not checked — File properties… checks it"));
    extras.Add(new("Package", "not looked up — File properties… names it"));
    extras.Add(new("Image hash", "not computed — File properties… computes it"));
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
    // The one page whose control is a panel rather than the table itself, so AddPage names the panel
    // and the table inside it would be left saying nothing (PRD §74).
    this._general.Control.AccessibleName = _GeneralTab;
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

    // With the verify delegate, so §27's missing Verify button is the hash button rather than a
    // second one: the same read of the file answers what the bytes are, whether the package that
    // shipped them still recognises them and whether anybody signed for that package. Three separate
    // statements from one read, and never one word standing for all of them (PRD §25.6, §70).
    new FilePropertiesDialog(
      path,
      extra,
      this._actions,
      image => this._probe.DescribeImage(image, verify: true)
    ).ShowDialog();
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
      this._cgroup,
    ])
      page.Stretch();

    // The pane's own four go with it. A control outside the toolkit's assembly cannot see its own
    // resize, so whoever owns a page lays it out — and these are the pane's pages now (PRD §10).
    this._pane.ApplyLayout();
    this._performance.Refresh();
  }

}
