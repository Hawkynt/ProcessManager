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
    ProcessField.HandleCount,
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

  private TabPage? _gpuPage;
  private ImageInfo? _image;
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

    this.Text = $"{name} ({key.Pid})";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 980, 640);
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
      this._tabs.SelectedTab = this.PageNamed(_GeneralTab);
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

  /// <summary>What the General page says, for a test with no display to read it off.</summary>
  public string GeneralText => this._general.Description;

  /// <summary>What the graphs are drawing, for the same reason (PRD §9.6).</summary>
  public string PerformanceText => this._performance.Description;

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
    var descriptors = handles.HasValue ? handles : process.HandleCount;

    this._general.Update(row, this.GeneralExtras(in process));
    this._cpu.Update(row);
    this._memory.Update(row);
    this._io.Update(row, [
      new("Read, total", Humanize.Bytes(process.ReadBytes)),
      new("Written, total", Humanize.Bytes(process.WriteBytes)),
      new("Other, total", Humanize.Bytes(process.OtherBytes)),
    ]);

    this.UpdateGpu(row, in process, delta, index);
    this._performance.Append(in process, delta, index, this.Descriptors(descriptors));
    this._performance.Refresh();

    this._pane.UpdateOverview(in process, row);
    this._pane.Refresh();
  }

  /// <summary>
  /// The descriptor count for the graph.
  /// </summary>
  /// <remarks>
  /// Counted here, and only while the Performance page is the one showing. On Linux this makes the
  /// kernel walk the process's descriptor table, which is why it is not in the sample (PRD §5.4) —
  /// but this window is pinned to one process, and a graph of thread count next to an empty box
  /// where the descriptor count should be is the kind of half-answer §72.3 exists to stop.
  /// </remarks>
  private Counter Descriptors(Counter fallback) {
    if (this._tabs?.SelectedTab?.Text != _PerformanceTab)
      return fallback;

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

  /// <summary>
  /// The facts about a process that are not fields of the table.
  /// </summary>
  /// <remarks>
  /// The image is read once and kept. Its ELF header and its directory entry do not change under a
  /// running process — and if the file on disk is replaced, the mapping in memory is still the old
  /// one, which is a fact the Modules page reports rather than something to re-read every second.
  /// </remarks>
  private List<KeyValuePair<string, string>> GeneralExtras(in ProcessRecord process) {
    var extras = new List<KeyValuePair<string, string>> {
      new("Running for", Uptime(process.StartTimeUtcTicks)),
    };

    if (!this._imageRead) {
      this._imageRead = true;
      this._imagePath = process.ImagePath;
      this._image = this._probe.DescribeImage(this.Key);
    }

    if (this._image is { } image) {
      extras.Add(new("Architecture", image.Architecture ?? (image.HeaderRead ? "unknown" : "—")));
      extras.Add(new("Interpreter", image.Interpreter ?? (image.HeaderRead ? "statically linked" : "—")));
      extras.Add(new("Working directory", image.WorkingDirectory ?? "—"));
    }

    if (this._imagePath is { Length: > 0 } path) {
      var facts = FileFacts.Describe(path);
      extras.Add(new("Image size", FileFactsFormatting.Size(in facts)));
      extras.Add(new("Image modified", FileFactsFormatting.Modified(in facts)));
      extras.Add(new("Image permissions", facts.Permissions ?? "n/a"));
    }

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
    this._performance.Refresh();
  }

}
