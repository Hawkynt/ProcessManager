using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Settings;

/// <summary>
/// What survives a restart (PRD §11, §67).
/// </summary>
/// <remarks>
/// <para>
/// Stored as <c>key=value</c> lines rather than JSON, and deliberately. A settings file is a thing
/// people edit, diff and paste into a bug report; a hand-written JSON parser is a liability nobody
/// needs for eleven scalars and a few lists, and a source-generated serialiser is a lot of machinery
/// to make a format worse to read.
/// </para>
/// <para>
/// Unknown keys are kept and written back out. A newer build writing a key this one does not
/// understand must not have it silently deleted by an older build, which is what happens to every
/// settings format that round-trips through a fixed schema.
/// </para>
/// </remarks>
public sealed record UserSettings {

  /// <summary>Seconds between samples (PRD §12).</summary>
  public double IntervalSeconds { get; init; } = 1;

  /// <summary>
  /// Whether the sample tick is off and a refresh is asked for by hand (PRD §12).
  /// </summary>
  /// <remarks>
  /// Kept beside the interval rather than folded into it as a nought, because they are two different
  /// statements and a program that forgot the difference would put somebody who chose "by hand" back
  /// on a quarter-second tick the next time they opened it. The interval underneath is remembered,
  /// so leaving manual refresh goes back to the rate they were on.
  /// <para>
  /// A pause is <em>not</em> this. Pausing is a toggle somebody flips for a few seconds to read a
  /// row that will not hold still, and a monitor that opened paused because it was paused when it
  /// was last closed is a monitor showing a table of nothing at all.
  /// </para>
  /// </remarks>
  public bool ManualRefresh { get; init; }

  /// <summary>
  /// The intervals both front-ends offer (PRD §12).
  /// </summary>
  /// <remarks>
  /// One list, so the window's menu and the terminal's picker cannot come to hold different ideas of
  /// what is on offer — the same reason the fields are one catalogue. Anything else is still
  /// settable: <c>--interval</c> and the file take any number, and this is what is worth a line in
  /// a menu.
  /// </remarks>
  public static IReadOnlyList<double> OfferedIntervalSeconds { get; } = [0.25, 0.5, 1, 2, 5, 10];

  /// <summary>What an interval is called on screen — <c>250 ms</c>, <c>1 s</c>, <c>2.5 s</c>.</summary>
  /// <remarks>
  /// Beside the list it labels, so the window's menu and the terminal's picker read the same. Under
  /// a second the figure is milliseconds, because "0.25 s" is a number somebody has to convert
  /// before it means anything.
  /// </remarks>
  public static string NameOfInterval(double seconds) => seconds < 1
    ? (seconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms"
    : seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

  public ProcessField SortField { get; init; } = ProcessField.CpuPercent;

  public bool SortDescending { get; init; } = true;

  /// <summary>
  /// Whether the rows are the process tree.
  /// </summary>
  /// <remarks>
  /// Derived from <see cref="Grouping"/> rather than kept beside it. They are one decision — a list
  /// is nested by parentage or headed by something else, never both — and two fields for one
  /// decision is two fields to disagree, which is what the field catalogue was split up to stop
  /// (PRD §5.1). The <c>tree=</c> key stays because settings files and the command line already use
  /// that word.
  /// </remarks>
  public bool TreeMode {
    get => this.Grouping == ProcessGrouping.ParentTree;
    init => this.Grouping = value ? ProcessGrouping.ParentTree : ProcessGrouping.None;
  }

  /// <summary>Which convention CPU percentages are expressed in (PRD §3.2).</summary>
  public CpuPercentMode CpuMode { get; init; } = CpuPercentMode.Normalized;

  /// <summary>
  /// How many decimals a percentage is written with (PRD §15).
  /// </summary>
  /// <remarks>
  /// §15 asks for this of the processor's percentage and it governs every percentage the program
  /// prints, which is the same decision rather than a wider one: a window writing CPU to two decimals
  /// and memory to one would be claiming a precision about one of them it does not have about the
  /// other. Nought for a column that does not flicker, two for chasing a process that uses a
  /// twentieth of a core.
  /// </remarks>
  public int PercentDecimals { get; init; } = Humanize.DefaultPercentDecimals;

  /// <summary>
  /// Keep a record of what each program has cost this machine, across sessions (PRD §44).
  /// </summary>
  /// <remarks>
  /// <b>Off, and off is the design rather than a preference about it.</b> A file recording which
  /// applications a person ran and for how long is surveillance if it appears without being asked
  /// for, however useful it is when it is asked for. Nothing is accumulated and no file is written
  /// until this says otherwise.
  /// </remarks>
  public bool UsageHistory { get; init; }

  /// <summary>
  /// How many days of that record to keep, or nought for all of it (PRD §44).
  /// </summary>
  /// <remarks>
  /// Counted from when a program was last seen rather than from when its record began: a program run
  /// every day since January is not old, and dropping it because its record is old would delete
  /// exactly the rows worth keeping.
  /// </remarks>
  public int UsageHistoryDays { get; init; }

  /// <summary>
  /// Which resources have an indicator in the tray, or none at all (PRD §65).
  /// </summary>
  /// <remarks>
  /// Empty means no tray, which is the default: a program that puts icons in somebody's panel
  /// without being asked has taken a decision about their screen that is theirs to take. Each one is
  /// named rather than a count, because §65's fourth box is that they can be turned on and off one
  /// at a time and a number cannot say which.
  /// </remarks>
  public IReadOnlyList<IndicatorKind> TrayIndicators { get; init; } = [];

  /// <summary>Draw the terminal's history columns with block characters rather than ASCII.</summary>
  public bool BlockCharacters { get; init; } = true;

  /// <summary>
  /// How the terminal draws an in-row history, or null to let it decide from the terminal (PRD §57.4).
  /// </summary>
  /// <remarks>
  /// Null is the default and is not the same as any of the four: it means the terminal reads the
  /// locale and the <c>TERM</c> it was given and picks what that terminal can actually draw. Somebody
  /// who has said "braille" has said it about every terminal they will ever run this in, which is a
  /// stronger claim and is theirs to make — so a stated style is honoured even where the font may not
  /// have the glyphs, and the empty column that results is the answer to a question they asked.
  /// </remarks>
  public GraphStyle? TerminalGraphs { get; init; }

  /// <summary>The columns the window opens with.</summary>
  public ProcessField[] DesktopColumns { get; init; } = [];

  /// <summary>The columns the terminal opens with.</summary>
  public ProcessField[] TerminalColumns { get; init; } = [];

  /// <summary>
  /// How many leading columns are pinned in each front-end (PRD §11).
  /// </summary>
  /// <remarks>
  /// A count rather than a list of fields, because that is what pinning is: the leading run of the
  /// column order, which moves when the order does. One apiece by default, so a table scrolled
  /// sideways always has a name column left on it.
  /// <para>
  /// Two keys and not one. The window and the terminal keep their own column orders, and a machine
  /// they share is a machine where five pinned columns in a 200-pixel-wide list mean nothing at all
  /// in an eighty-column terminal.
  /// </para>
  /// </remarks>
  public int PinnedDesktopColumns { get; init; } = 1;

  public int PinnedTerminalColumns { get; init; } = 1;

  /// <summary>
  /// Widths somebody dragged in the window, by field (PRD §11).
  /// </summary>
  /// <remarks>
  /// Only the ones that differ from the registry's, so a file does not pin every width forever: a
  /// column whose default is improved in a later build should get the improvement unless somebody
  /// has actually chosen a width for it.
  /// </remarks>
  public IReadOnlyList<KeyValuePair<ProcessField, int>> DesktopColumnWidths { get; init; } = [];

  /// <summary>What the rows are grouped by (PRD §83).</summary>
  public ProcessGrouping Grouping { get; init; } = ProcessGrouping.ParentTree;

  /// <summary>
  /// The window's size, so it opens where it was left (PRD §11).
  /// </summary>
  /// <remarks>
  /// Size and not position: a window restored to a screen that is no longer plugged in opens
  /// off-screen, and there is no way to ask this program's toolkit where the monitors are.
  /// </remarks>
  public int WindowWidth { get; init; }

  public int WindowHeight { get; init; }

  /// <summary>Where the splitter between the process list and the detail pane sat, in percent.</summary>
  public int SplitPercent { get; init; }

  /// <summary>
  /// Whether the lower pane was showing (PRD §10).
  /// </summary>
  /// <remarks>
  /// Kept, because it is a decision about how much of the screen the process list gets and nobody
  /// wants to make it twice a day. On by default: the pane is what the window is shaped around.
  /// </remarks>
  public bool LowerPaneVisible { get; init; } = true;

  /// <summary>
  /// Whether a properties tab this machine cannot fill is removed rather than left saying so
  /// (PRD §26).
  /// </summary>
  /// <remarks>
  /// A preference and not a decision, because the two answer different questions: "can this machine
  /// do it" wants the tab there, saying it cannot, and "get out of my way" wants it gone. Off by
  /// default — a missing tab is indistinguishable from a feature nobody wrote.
  /// </remarks>
  public bool HideUnavailableTabs { get; init; }

  /// <summary>
  /// Whether a destructive action asks before it happens (PRD §67, §90).
  /// </summary>
  /// <remarks>
  /// On by default, and the only setting in this file whose default is chosen for somebody who has
  /// not read it: ending the wrong process is not undoable. Turned off, the prompt goes from every
  /// single-process action — the ones whose target the row under the cursor already names.
  /// <para>
  /// A bulk action still asks, and that is deliberate rather than an oversight. "End 14 processes?"
  /// and "End Firefox?" are the same gesture and very different requests, and the count is the whole
  /// of what the confirmation is for; a setting that removed it would remove the only place the size
  /// of the request is ever stated (PRD §11, §90).
  /// </para>
  /// </remarks>
  public bool ConfirmDestructiveActions { get; init; } = true;

  /// <summary>
  /// How long a newly started process stays highlighted, in seconds (PRD §87).
  /// </summary>
  /// <remarks>
  /// One second, which is what the flash already was at the default refresh rate — it lasted until
  /// the next sample, so the rate decided it. That is the whole reason this exists: at a
  /// quarter-second tick the green went before an eye could land on it, and at ten seconds it stayed
  /// for ten. Nought is the off switch, and is §12's "optionally highlighted" said from this end.
  /// </remarks>
  public double NewHighlightSeconds { get; init; } = 1;

  /// <summary>
  /// How long a row is kept after the process behind it has gone, in seconds (PRD §14, §87).
  /// </summary>
  /// <remarks>
  /// <b>Nought, and off.</b> A table that keeps its dead is showing something that is not there — a
  /// considered thing to ask for and a bad thing to assume — and on a machine that churns through
  /// processes it doubles the table for nobody who was not looking for one that ended. It is also
  /// what makes `exit.time` answerable at all: with this off, that column is a dash everywhere,
  /// because there is nowhere for the answer to live.
  /// </remarks>
  public double KeepExitedSeconds { get; init; }

  /// <summary>
  /// Whether the table follows a process that has just started (PRD §87).
  /// </summary>
  /// <remarks>
  /// Off, and it has to be: §12 promises that a refresh leaves the scroll position where it was, and
  /// this is the one thing in the program allowed to break that promise. It is for watching for
  /// something specific to appear — a build step, a service restarting — and on an ordinary desktop,
  /// where something starts most seconds, it would move the table out from under whoever was reading
  /// it. So it is asked for rather than arrived at.
  /// </remarks>
  public bool ScrollToNewProcess { get; init; }

  /// <summary>
  /// Whether the performance page opens tightened up (PRD §45.7, §67).
  /// </summary>
  /// <remarks>
  /// The one piece of §67's "Appearance" the program actually has: density is a real switch on the
  /// performance page with a real effect, and it was reachable from that page's own View menu and
  /// forgotten the moment the window closed. Theme, font and icon size are not here, because this
  /// program has no such switches to remember — a key in a settings file that drives nothing is a
  /// lie the file tells the person editing it.
  /// </remarks>
  public bool CompactPerformancePage { get; init; }

  /// <summary>
  /// Whether the terminal front-end reads the mouse (PRD §57.5, §67).
  /// </summary>
  /// <remarks>
  /// The persistent form of <c>--no-mouse</c>. Worth remembering because the people who turn it off
  /// are the people whose terminal or multiplexer wants the selection back, and that is a property of
  /// their setup rather than of one run.
  /// </remarks>
  public bool TerminalMouse { get; init; } = true;

  /// <summary>
  /// Whether the performance page opens on whatever is under the greatest load (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// On by default, because somebody opening that page has a machine that is doing something and
  /// wants to know what. Off for the people who keep it open on one resource and do not want it
  /// moved out from under them by a disk that was briefly busy — which is a preference and not a
  /// mistake, and is why it is a setting rather than a decision.
  /// </remarks>
  public bool PerformanceOpensOnBusiest { get; init; } = true;

  /// <summary>Whether older performance history is progressively compressed into the selected span.</summary>
  /// <remarks>
  /// On by default because it extends context without taking resolution away from the newest data.
  /// The graph still caps the request to what its backing ring actually retained, so this setting can
  /// never manufacture history that was not sampled.
  /// </remarks>
  public bool CompressPerformanceHistory { get; init; } = true;

  /// <summary>The default requested history horizon relative to the selected recent span.</summary>
  public const double DefaultPerformanceHistoryMultiplier = 15;

  /// <summary>Multipliers offered by the desktop UI, smallest useful compression first.</summary>
  public static IReadOnlyList<double> OfferedPerformanceHistoryMultipliers { get; } = [2, 4, 8, 15];

  /// <summary>
  /// How much older history the graph asks to fit behind the recent span when compression is on.
  /// </summary>
  /// <remarks>
  /// This is deliberately independent of <see cref="CompressPerformanceHistory"/>. Turning the
  /// feature off and on again restores the chosen horizon rather than replacing it with the default.
  /// Hand-written values are accepted from 1× through 64×; the UI offers a smaller useful set.
  /// </remarks>
  public double PerformanceHistoryMultiplier { get; init; } = DefaultPerformanceHistoryMultiplier;

  /// <summary>
  /// Colours the file overrides, by the names <see cref="ColourNames"/> lists.
  /// </summary>
  /// <remarks>
  /// A sparse map rather than a full palette: a file that names one colour must keep following the
  /// program for the other twelve, and a palette written out whole in version four would pin every
  /// colour of it forever.
  /// </remarks>
  public IReadOnlyDictionary<string, uint> Colours { get; init; }
    = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// The terminal's own palette, by the names <see cref="TerminalColourNames"/> lists (PRD §67).
  /// </summary>
  /// <remarks>
  /// Separate from <see cref="Colours"/> and deliberately, because the two name different things.
  /// That map names what a <em>process</em> is — a zombie, a service, something of yours — and the
  /// window paints each of those in its own colour. The terminal has ten appearances and no more:
  /// everything it draws is one of them, so half a dozen meanings share an ink and naming a meaning
  /// here would be a promise the renderer cannot keep.
  /// <para>
  /// A key is <c>tui.color.&lt;name&gt;</c> for the ink and <c>tui.color.&lt;name&gt;.bg</c> for the
  /// ground, and naming either replaces the appearance outright rather than tinting the built-in
  /// one. Three of the ten paint a ground of their own, so an ink named alone puts them on the
  /// terminal's background — which is a visible answer to what was actually asked rather than a
  /// half-applied one.
  /// </para>
  /// </remarks>
  public IReadOnlyDictionary<string, uint> TerminalColours { get; init; }
    = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// When a cell is busy enough to be marked (PRD §23).
  /// </summary>
  /// <remarks>
  /// Worth being settable because the right answer depends on the machine: a whole core is a lot on
  /// a laptop and nothing on a build server, and a hundred megabytes a second is saturation for a
  /// spinning disk and idle for an NVMe.
  /// </remarks>
  public UsageThresholds Thresholds { get; init; } = UsageThresholds.Default;

  /// <summary>
  /// What this machine's owner asked to be told about (PRD §64).
  /// </summary>
  /// <remarks>
  /// Empty by default and empty on every machine that has not written a <c>notify.</c> line, which is
  /// what §64's "rules are explicit" means in practice: nothing is inferred, nothing is on because it
  /// seemed useful, and a program nobody has configured interrupts nobody.
  /// </remarks>
  public NotificationRules Notifications { get; init; } = new();

  /// <summary>Named column sets, as PRD §11 requires and §94 names.</summary>
  public IReadOnlyDictionary<string, ProcessField[]> ColumnSets { get; init; }
    = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase);

  /// <summary>Lines this build did not understand, kept so an older build cannot eat them.</summary>
  public IReadOnlyList<string> Unknown { get; init; } = [];

  /// <summary>
  /// What was wrong with each <c>alert=</c> line that would not parse (PRD §84).
  /// </summary>
  /// <remarks>
  /// Kept and reported rather than swallowed, which is the one place this file departs from "a line
  /// that will not parse leaves its setting at the default". A mistyped colour leaves a column the
  /// colour it already was and nobody is misled; a mistyped rule leaves somebody believing they are
  /// being watched for something. The line itself is also kept verbatim in <see cref="Unknown"/>, so
  /// saving the settings does not eat the rule they were halfway through writing.
  /// </remarks>
  public IReadOnlyList<string> AlertProblems { get; init; } = [];

  #region the presets of §94

  /// <summary>
  /// The built-in column sets. Offered when the file names none, and never written to it — a preset
  /// that got copied into everybody's settings could never be improved again.
  /// </summary>
  public static IReadOnlyDictionary<string, ProcessField[]> Presets { get; } =
    new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
      // §94's everyday set: what a person opens a process table to see. The graphics share is in it
      // and the network one is not, and the difference is not effort — §18 refuses per-process
      // traffic because no honest source for it exists on either platform, while §19 has one and it
      // costs a descriptor read per process. Naming this set is what pays for that (PRD §5.4).
      ["basic"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.State, ProcessField.CpuPercent,
        ProcessField.PrivateBytes, ProcessField.IoTotalRate, ProcessField.GpuPercent,
      ],
      // §94 names a signature column here as well and it is deliberately absent, which is a decision
      // rather than a gap. "Signature" is two columns and not one — a PE carries one the publisher
      // put inside the file and an ELF does not, so what signs a Linux program is the package
      // database that recorded its digest (PRD §5.3, §21, §70). The pair is fifty characters wide,
      // one half of it reads n/a on whichever platform this is, and filling either means reading and
      // digesting every image on the machine. This is the set somebody names to get a broad everyday
      // table, and eleven columns of it fit a terminal eighty wide; the two verdict columns are in
      // the security and forensic sets, which exist to pay for them (PRD §5.4).
      ["expert"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.ParentPid, ProcessField.CpuPercent,
        ProcessField.PrivateBytes, ProcessField.WorkingSetBytes, ProcessField.IoTotalRate,
        ProcessField.UserName, ProcessField.StartTime, ProcessField.CommandLine,
      ],
      // Both accounts, because the pair is the story: a row where they differ is a process running
      // with an authority nobody at the keyboard has. The bounding set is here rather than the
      // permitted one because it answers the question a reader is usually asking — what could this
      // ever do — while the effective set answers what it may do this instant.
      //
      // §70's five questions are five columns and are never read off one another: what the bytes are
      // (the digest), whether the file's own signature still covers them, whether anybody this
      // machine trusts signed for it, whether the package database's record still matches, and what
      // an online service says — which is nothing, and which has a column of its own so that a
      // digest computed here can never be mistaken for a file submitted from here. Each is Windows'
      // or Linux's and not both, and the platform that has no answer says so.
      ["security"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.EffectiveUserName,
        ProcessField.PrivilegeChanged, ProcessField.Elevated, ProcessField.Integrity,
        ProcessField.Seccomp, ProcessField.NoNewPrivileges, ProcessField.Capabilities,
        ProcessField.BoundingCapabilities, ProcessField.SecurityContext,
        ProcessField.ConfinementMode, ProcessField.Protected, ProcessField.ProtectionLevel,
        ProcessField.ImageSignature, ProcessField.ImageSigner, ProcessField.PackageStatus,
        ProcessField.TrustChain, ProcessField.ImageSha256, ProcessField.Reputation,
        ProcessField.ImagePath,
      ],
      // The scheduling class is in it because §94 asks for it and because it is the one column here
      // that says why a row's rates look the way they do: a process at idle I/O reads slowly because
      // it is yielding the disk, not because it has little to read. It costs an ioprio_get per
      // process per sample, which naming this set is what pays for (PRD §5.4, §17).
      //
      // The two cumulative totals are the other half of the set's question and were missing from it
      // while the fields existed: a rate says what a process is doing now, and a process that spent
      // an hour reading and is idle when somebody looks at it answers that with a nought. The other
      // rate is Windows' — /proc/[pid]/io keeps no third figure — and is in the set anyway, because
      // the platform that cannot fill it says so in the cell rather than being quietly dropped from
      // a named set (PRD §5.3, §72.3).
      ["io"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.ReadBytesPerSecond,
        ProcessField.WriteBytesPerSecond, ProcessField.ReadBytesTotal, ProcessField.WriteBytesTotal,
        ProcessField.OtherBytesPerSecond, ProcessField.IoTotalRate, ProcessField.IoPriority,
        ProcessField.IoHistory,
      ],
      // §94's network set, less the two columns §18 refuses. Endpoints rather than traffic, and
      // deliberately: Linux attributes no bytes to a process without packet accounting or eBPF, so a
      // send and a receive column here would be filled by summing the sockets a process happens to
      // hold open at the moment somebody looked — which is not the quantity the header would claim
      // and which nothing in the cell would betray (PRD §18, §72.3).
      ["network"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.TcpConnectionCount,
        ProcessField.UdpSocketCount, ProcessField.ListeningSocketCount,
        ProcessField.RemoteEndpointCount,
      ],
      ["memory"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.PrivateBytes,
        ProcessField.PrivateBytesDelta, ProcessField.PrivateWorkingSet, ProcessField.WorkingSetBytes,
        ProcessField.PeakWorkingSet, ProcessField.Swap, ProcessField.PageFaultsDelta,
        ProcessField.MemoryHistory,
      ],
      ["cpu"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.CpuPercent, ProcessField.CpuPercentPerCore,
        ProcessField.CpuTime, ProcessField.ContextSwitchesDelta, ProcessField.LastCpu,
        ProcessField.ThreadCount, ProcessField.CpuHistory,
      ],
      // Everything the expert set has, plus the two halves it is missing: who a process really is
      // and what it is doing to the disk (PRD §11, §94). Deliberately the dearest set in the file —
      // the package, the digest and the descriptor count each cost a reading the sampler does not
      // otherwise take, and asking for a forensic table is asking to pay for them (PRD §5.4). It is
      // also the widest, which is what the pinned columns and the sideways scroll are for.
      ["forensic"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.ParentPid, ProcessField.UserName,
        ProcessField.EffectiveUserName, ProcessField.PrivilegeChanged, ProcessField.Elevated,
        ProcessField.Capabilities, ProcessField.SecurityContext, ProcessField.Seccomp,
        ProcessField.NoNewPrivileges, ProcessField.TracerPid,
        ProcessField.CpuPercent, ProcessField.PrivateBytes, ProcessField.WorkingSetBytes,
        ProcessField.ReadBytesPerSecond, ProcessField.WriteBytesPerSecond, ProcessField.IoTotalRate,
        ProcessField.ThreadCount, ProcessField.HandleCount, ProcessField.StartTime,
        ProcessField.Package, ProcessField.ImageSha256, ProcessField.ImagePath,
        ProcessField.CommandLine,
      ],
      ["minimal"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.State,
        ProcessField.CpuPercent, ProcessField.PrivateBytes,
      ],
    };

  /// <summary>A named set, whether it came from the file or from the presets.</summary>
  public bool TryGetColumnSet(string name, out ProcessField[] fields) {
    if (this.ColumnSets.TryGetValue(name, out var saved)) {
      fields = saved;
      return true;
    }

    return Presets.TryGetValue(name, out fields!);
  }

  /// <summary>Every set name that can be asked for, saved ones first.</summary>
  public IReadOnlyList<string> ColumnSetNames() {
    var names = new List<string>(this.ColumnSets.Keys);
    foreach (var preset in Presets.Keys)
      if (!this.ColumnSets.ContainsKey(preset))
        names.Add(preset);

    names.Sort(StringComparer.OrdinalIgnoreCase);
    return names;
  }

  #endregion

  #region reading and writing

  private const string _ColumnSetPrefix = "columnset.";
  private const string _ColourPrefix = "color.";
  private const string _TerminalColourPrefix = "tui.color.";
  private const string _BackgroundSuffix = ".bg";

  /// <summary>
  /// Parses a settings file. A line that cannot be understood is kept verbatim and never thrown
  /// away, and a value that cannot be parsed leaves its setting at the default rather than failing
  /// the whole file: a settings file with one bad line must still start the program.
  /// </summary>
  public static UserSettings Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);

    var settings = new UserSettings();
    var sets = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase);
    var colours = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    var terminalColours = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    var unknown = new List<string>();
    var alerts = new List<AlertRule>();
    var alertProblems = new List<string>();

    foreach (var raw in text.Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] == '#')
        continue;

      var separator = line.IndexOf('=', StringComparison.Ordinal);
      if (separator <= 0) {
        unknown.Add(line);
        continue;
      }

      var key = line[..separator].Trim();
      var value = line[(separator + 1)..].Trim();

      // Before the window's palette, because "tui.color.good" is not "color.good" and a reader
      // skimming the two branches should not have to work that out.
      if (key.StartsWith(_TerminalColourPrefix, StringComparison.OrdinalIgnoreCase)) {
        var slot = key[_TerminalColourPrefix.Length..];
        if (IsTerminalColourKey(slot) && TryParseColour(value, out var ink))
          terminalColours[slot] = ink;
        else
          unknown.Add(line);

        continue;
      }

      if (key.StartsWith(_ColourPrefix, StringComparison.OrdinalIgnoreCase)) {
        var name = key[_ColourPrefix.Length..];
        if (name.Length > 0 && TryParseColour(value, out var argb))
          colours[name] = argb;
        else
          unknown.Add(line);

        continue;
      }

      if (key.StartsWith(_ColumnSetPrefix, StringComparison.OrdinalIgnoreCase)) {
        var name = key[_ColumnSetPrefix.Length..];
        if (name.Length > 0 && TryParseFields(value, out var members))
          sets[name] = members;
        else
          unknown.Add(line);

        continue;
      }

      switch (key.ToLowerInvariant()) {
        case "interval":
          // "manual" leaves the interval where it was: it says the tick is off, not how fast it
          // would run, and going back to a rate somebody never chose is the wrong answer (PRD §12).
          if (value.Equals("manual", StringComparison.OrdinalIgnoreCase))
            settings = settings with { ManualRefresh = true };
          else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
              && seconds is > 0 and <= 3600)
            settings = settings with { IntervalSeconds = seconds };

          break;

        case "interval.seconds":
          if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var underneath)
              && underneath is > 0 and <= 3600)
            settings = settings with { IntervalSeconds = underneath };

          break;

        case "sort":
          if (FieldRegistry.TryParse(value, out var sort))
            settings = settings with { SortField = sort };

          break;

        case "sort.descending":
          if (TryParseBool(value, out var descending))
            settings = settings with { SortDescending = descending };

          break;

        case "tree":
          if (TryParseBool(value, out var tree))
            settings = settings with { TreeMode = tree };

          break;

        case "cpu.mode":
          settings = value.ToLowerInvariant() switch {
            "normalized" or "normalised" => settings with { CpuMode = CpuPercentMode.Normalized },
            "percore" or "per-core" or "raw" => settings with { CpuMode = CpuPercentMode.PerCore },
            _ => settings,
          };

          break;

        case "percent.decimals":
          // Out of range is a typo rather than a preference and leaves the setting where it was; a
          // percentage written to nine decimals would be nine digits of a number sampled once a
          // second.
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimals)
              && decimals is >= 0 and <= Humanize.MaximumPercentDecimals)
            settings = settings with { PercentDecimals = decimals };

          break;

        case "tray":
          // A list of names rather than a boolean, so that turning one off does not mean turning
          // the tray off. "none" and an empty value both mean no tray at all.
          settings = settings with { TrayIndicators = ParseIndicators(value) };
          break;

        case "history.usage":
          if (TryParseBool(value, out var usage))
            settings = settings with { UsageHistory = usage };

          break;

        case "history.usage.days":
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days >= 0)
            settings = settings with { UsageHistoryDays = days };

          break;

        case "blocks":
          if (TryParseBool(value, out var blocks))
            settings = settings with { BlockCharacters = blocks };

          break;

        case "tui.graphs":
          // "auto" is written out as the absence of a stated style rather than as a fifth member of
          // the enum, so that a file saying "auto" and a file saying nothing mean the same thing.
          settings = value.ToLowerInvariant() switch {
            "auto" => settings with { TerminalGraphs = null },
            "blocks" => settings with { TerminalGraphs = GraphStyle.Blocks },
            "braille" => settings with { TerminalGraphs = GraphStyle.Braille },
            "ascii" => settings with { TerminalGraphs = GraphStyle.Ascii },
            "numbers" or "none" => settings with { TerminalGraphs = GraphStyle.Numbers },
            _ => settings,
          };

          break;

        case "columns.desktop":
          if (TryParseFields(value, out var desktop))
            settings = settings with { DesktopColumns = desktop };

          break;

        case "columns.terminal":
          if (TryParseFields(value, out var terminal))
            settings = settings with { TerminalColumns = terminal };

          break;

        case "columns.desktop.pinned":
          if (TryParseCount(value, out var pinnedDesktop))
            settings = settings with { PinnedDesktopColumns = pinnedDesktop };

          break;

        case "columns.terminal.pinned":
          if (TryParseCount(value, out var pinnedTerminal))
            settings = settings with { PinnedTerminalColumns = pinnedTerminal };

          break;

        case "columns.desktop.widths":
          if (TryParseWidths(value, out var widths))
            settings = settings with { DesktopColumnWidths = widths };

          break;

        case "grouping":
          if (TryParseGrouping(value, out var grouping))
            settings = settings with { Grouping = grouping };

          break;

        case "window.width":
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width is >= 320 and <= 30000)
            settings = settings with { WindowWidth = width };

          break;

        case "window.height":
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height is >= 240 and <= 30000)
            settings = settings with { WindowHeight = height };

          break;

        case "heat.cpu.warm":
          settings = settings with { Thresholds = settings.Thresholds with { WarmCpuPercent = Number(value, settings.Thresholds.WarmCpuPercent) } };
          break;

        case "heat.cpu.hot":
          settings = settings with { Thresholds = settings.Thresholds with { HotCpuPercent = Number(value, settings.Thresholds.HotCpuPercent) } };
          break;

        case "heat.memory.warm":
          settings = settings with { Thresholds = settings.Thresholds with { WarmMemoryPercent = Number(value, settings.Thresholds.WarmMemoryPercent) } };
          break;

        case "heat.memory.hot":
          settings = settings with { Thresholds = settings.Thresholds with { HotMemoryPercent = Number(value, settings.Thresholds.HotMemoryPercent) } };
          break;

        case "heat.io.warm":
          settings = settings with { Thresholds = settings.Thresholds with { WarmBytesPerSecond = Number(value, settings.Thresholds.WarmBytesPerSecond) } };
          break;

        case "heat.io.hot":
          settings = settings with { Thresholds = settings.Thresholds with { HotBytesPerSecond = Number(value, settings.Thresholds.HotBytesPerSecond) } };
          break;

        case "heat.gpu.warm":
          settings = settings with { Thresholds = settings.Thresholds with { WarmGpuPercent = Number(value, settings.Thresholds.WarmGpuPercent) } };
          break;

        case "heat.gpu.hot":
          settings = settings with { Thresholds = settings.Thresholds with { HotGpuPercent = Number(value, settings.Thresholds.HotGpuPercent) } };
          break;

        case "window.lowerpane":
          if (TryParseBool(value, out var lowerPane))
            settings = settings with { LowerPaneVisible = lowerPane };

          break;

        case "tabs.unavailable":
          settings = value.ToLowerInvariant() switch {
            "hidden" or "hide" => settings with { HideUnavailableTabs = true },
            "disabled" or "disable" or "show" => settings with { HideUnavailableTabs = false },
            _ => settings,
          };

          break;

        case "performance.busiest":
          if (TryParseBool(value, out var busiest))
            settings = settings with { PerformanceOpensOnBusiest = busiest };

          break;

        case "performance.density":
          settings = value.ToLowerInvariant() switch {
            "compact" or "dense" => settings with { CompactPerformancePage = true },
            "comfortable" or "normal" => settings with { CompactPerformancePage = false },
            _ => settings,
          };

          break;

        case "performance.history.compress":
          if (TryParseBool(value, out var compressHistory))
            settings = settings with { CompressPerformanceHistory = compressHistory };

          break;

        case "performance.history.multiplier":
          if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var historyMultiplier)
              && double.IsFinite(historyMultiplier) && historyMultiplier is >= 1 and <= 64)
            settings = settings with { PerformanceHistoryMultiplier = historyMultiplier };

          break;

        case "confirm.destructive":
          if (TryParseBool(value, out var confirm))
            settings = settings with { ConfirmDestructiveActions = confirm };

          break;

        // Seconds rather than samples, so it means the same thing at every refresh rate (PRD §87).
        // "off" as well as nought, because a reader of the file should not have to know that a
        // duration of nothing is how the highlight is switched off.
        case "highlight.new":
          if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            settings = settings with { NewHighlightSeconds = 0 };
          else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var highlight)
              && highlight is >= 0 and <= 3600)
            settings = settings with { NewHighlightSeconds = highlight };

          break;

        // The other half of the pair, and off rather than defaulted to a duration: keeping the dead
        // is a thing to ask for (PRD §14, §87).
        case "highlight.exited":
        case "keep.exited":
          if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            settings = settings with { KeepExitedSeconds = 0 };
          else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var keep)
              && keep is >= 0 and <= 3600)
            settings = settings with { KeepExitedSeconds = keep };

          break;

        case "scroll.new":
          if (TryParseBool(value, out var follow))
            settings = settings with { ScrollToNewProcess = follow };

          break;

        case "tui.mouse":
          if (TryParseBool(value, out var mouse))
            settings = settings with { TerminalMouse = mouse };

          break;

        case "window.split":
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var split) && split is >= 10 and <= 90)
            settings = settings with { SplitPercent = split };

          break;

        // PRD §64. Seven lines, each of them somebody saying out loud what they want to be told
        // about. A threshold that cannot be parsed leaves the rule unset rather than at nought,
        // because nought is a threshold somebody could mean and "every process that used any CPU at
        // all" is not what a mistyped number should turn into.
        case "notify.started":
          if (TryParseBool(value, out var started))
            settings = settings with { Notifications = settings.Notifications with { ProcessStarted = started } };

          break;

        case "notify.ended":
          if (TryParseBool(value, out var ended))
            settings = settings with { Notifications = settings.Notifications with { ProcessEnded = ended } };

          break;

        case "notify.name":
          settings = settings with { Notifications = settings.Notifications with { Names = SplitList(value) } };
          break;

        case "notify.cpu":
          if (TryParseThreshold(value, out var notifyCpu))
            settings = settings with { Notifications = settings.Notifications with { CpuPercent = notifyCpu } };

          break;

        case "notify.memory":
          if (TryParseThreshold(value, out var notifyMemory))
            settings = settings with { Notifications = settings.Notifications with { MemoryPercent = notifyMemory } };

          break;

        case "notify.disk":
          if (TryParseThreshold(value, out var notifyDisk))
            settings = settings with { Notifications = settings.Notifications with { DiskBytesPerSecond = notifyDisk } };

          break;

        case "notify.service":
          settings = settings with { Notifications = settings.Notifications with { Services = SplitList(value) } };
          break;

        // PRD §84. Repeatable, unlike every other key in this file: the six above are one rule each
        // and this one is a language, and somebody watching three things has written three lines.
        case "alert":
          if (AlertRule.TryParse(value, out var alert, out var wrong) && alert is not null) {
            alerts.Add(alert);
            break;
          }

          // Kept verbatim so that saving the settings does not eat the rule somebody was halfway
          // through writing, and reported so they find out now rather than the first time it should
          // have fired.
          alertProblems.Add($"{value} — {wrong}");
          unknown.Add(line);
          break;

        default:
          unknown.Add(line);
          break;
      }
    }

    return settings with {
      ColumnSets = sets,
      Colours = colours,
      TerminalColours = terminalColours,
      Unknown = unknown,
      Notifications = settings.Notifications with { Alerts = alerts },
      AlertProblems = alertProblems,
    };
  }

  public string Write() {
    var text = new StringBuilder();
    text.AppendLine("# ProcessManager settings. Edited by hand quite deliberately: every value here");
    text.AppendLine("# is a field key or a plain number, and `procman --help-fields` lists them all.");
    text.AppendLine();
    // The rate underneath is written on its own line when the tick is off, so that turning the tick
    // back on returns to the rate somebody chose rather than to whatever the default happens to be.
    if (this.ManualRefresh) {
      text.AppendLine("interval=manual");
      text.Append("interval.seconds=").AppendLine(this.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
    } else
      text.Append("interval=").AppendLine(this.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture));

    text.Append("sort=").AppendLine(FieldRegistry.Get(this.SortField).Key);
    text.Append("sort.descending=").AppendLine(this.SortDescending ? "true" : "false");
    text.Append("tree=").AppendLine(this.TreeMode ? "true" : "false");
    text.Append("cpu.mode=").AppendLine(this.CpuMode == CpuPercentMode.PerCore ? "percore" : "normalized");
    text.Append("percent.decimals=").AppendLine(this.PercentDecimals.ToString(CultureInfo.InvariantCulture));
    text.Append("blocks=").AppendLine(this.BlockCharacters ? "true" : "false");

    if (this.DesktopColumns.Length > 0)
      text.Append("columns.desktop=").AppendLine(Join(this.DesktopColumns));

    if (this.TerminalColumns.Length > 0)
      text.Append("columns.terminal=").AppendLine(Join(this.TerminalColumns));

    // Only when they are not the one column every table opens with. A line in everybody's file
    // saying the first column is pinned is a line nobody reads.
    if (this.PinnedDesktopColumns != 1)
      text.Append("columns.desktop.pinned=").AppendLine(this.PinnedDesktopColumns.ToString(CultureInfo.InvariantCulture));

    if (this.PinnedTerminalColumns != 1)
      text.Append("columns.terminal.pinned=").AppendLine(this.PinnedTerminalColumns.ToString(CultureInfo.InvariantCulture));

    if (this.DesktopColumnWidths.Count > 0) {
      var widths = new List<string>(this.DesktopColumnWidths.Count);
      foreach (var (field, width) in this.DesktopColumnWidths)
        widths.Add($"{FieldRegistry.Get(field).Key}:{width.ToString(CultureInfo.InvariantCulture)}");

      text.Append("columns.desktop.widths=").AppendLine(string.Join(",", widths));
    }

    // Only when it is not the tree. The tree is what the window opens on, so its being there is not
    // a preference worth a line in everybody's file.
    if (this.Grouping != ProcessGrouping.ParentTree)
      text.Append("grouping=").AppendLine(NameOfGrouping(this.Grouping));

    if (this.WindowWidth > 0 && this.WindowHeight > 0) {
      text.AppendLine();
      text.Append("window.width=").AppendLine(this.WindowWidth.ToString(CultureInfo.InvariantCulture));
      text.Append("window.height=").AppendLine(this.WindowHeight.ToString(CultureInfo.InvariantCulture));
    }

    if (this.SplitPercent > 0)
      text.Append("window.split=").AppendLine(this.SplitPercent.ToString(CultureInfo.InvariantCulture));

    // Only when it is off. The pane is what the window is shaped around, so its being there is not
    // a preference worth a line in everybody's file.
    if (!this.LowerPaneVisible)
      text.AppendLine("window.lowerpane=false");

    // Only when it is off, like the pane above: the page opening on whatever is busiest is what it
    // does, and a line saying so in every file is a line nobody reads.
    if (!this.PerformanceOpensOnBusiest) {
      text.AppendLine();
      text.AppendLine("# The performance page opens on the processor rather than on whatever is");
      text.AppendLine("# under the greatest load.");
      text.AppendLine("performance.busiest=false");
    }

    // Only when it is off, and for a reason the two above do not have: this one is a safety net, and
    // a file that says nothing about it must leave it up rather than have somebody's default depend
    // on which build wrote the file last.
    if (!this.ConfirmDestructiveActions) {
      text.AppendLine();
      text.AppendLine("# Single-process actions happen without asking first. A bulk action still");
      text.AppendLine("# asks, because the count is the whole of what that confirmation is for.");
      text.AppendLine("confirm.destructive=false");
    }

    if (this.CompactPerformancePage) {
      text.AppendLine();
      text.AppendLine("# The performance page opens tightened up, with its diagnostics block open.");
      text.AppendLine("performance.density=compact");
    }

    if (!this.CompressPerformanceHistory || this.PerformanceHistoryMultiplier != DefaultPerformanceHistoryMultiplier) {
      text.AppendLine();
      text.AppendLine("# Older performance data is progressively compressed while the newest part keeps");
      text.AppendLine("# ordinary resolution. The multiplier is a requested horizon and is capped by");
      text.AppendLine("# however much history the in-memory ring actually retained.");
      if (!this.CompressPerformanceHistory)
        text.AppendLine("performance.history.compress=false");
      if (this.PerformanceHistoryMultiplier != DefaultPerformanceHistoryMultiplier)
        text.Append("performance.history.multiplier=")
          .AppendLine(this.PerformanceHistoryMultiplier.ToString("0.###", CultureInfo.InvariantCulture));
    }

    // Only when it is not the one second it has always been, which is the rule every block here
    // follows: the absence of a line means the default, and a file full of defaults is a file
    // nobody reads (PRD §87).
    if (this.NewHighlightSeconds != 1) {
      text.AppendLine();
      text.AppendLine("# How long a newly started process stays highlighted, in seconds. \"off\"");
      text.AppendLine("# for no highlight at all. Seconds and not samples, so it means the same");
      text.AppendLine("# thing whatever the refresh rate is.");
      text.Append("highlight.new=").AppendLine(this.NewHighlightSeconds <= 0
        ? "off"
        : this.NewHighlightSeconds.ToString(CultureInfo.InvariantCulture));
    }

    if (this.KeepExitedSeconds > 0) {
      text.AppendLine();
      text.AppendLine("# How long a row stays in the table after the process behind it has gone, in");
      text.AppendLine("# seconds. Off by default: a table that keeps its dead is showing something");
      text.AppendLine("# that is not there. It is also what makes the exit.time column answerable.");
      text.Append("keep.exited=")
        .AppendLine(this.KeepExitedSeconds.ToString(CultureInfo.InvariantCulture));
    }

    if (this.ScrollToNewProcess) {
      text.AppendLine();
      text.AppendLine("# The table scrolls to a process that has just started. Off by default, and");
      text.AppendLine("# deliberately: it is the one thing allowed to move the view during a");
      text.AppendLine("# refresh, which is otherwise promised not to happen.");
      text.AppendLine("scroll.new=true");
    }

    // Written out only where a rule was actually set, for the reason every other block here is:
    // seven lines of "notify.cpu=" in everybody's file would be seven lines nobody reads, and the
    // absence of a line is what "no rule" already means (PRD §64, §67).
    if (this.Notifications.Any) {
      text.AppendLine();
      text.AppendLine("# What to be told about, on the status line. Every one of these is off unless");
      text.AppendLine("# it is here: nothing is inferred and nothing fires that was not asked for.");
      if (this.Notifications.ProcessStarted)
        text.AppendLine("notify.started=true");

      if (this.Notifications.ProcessEnded)
        text.AppendLine("notify.ended=true");

      if (this.Notifications.Names.Count > 0)
        text.Append("notify.name=").AppendLine(string.Join(",", this.Notifications.Names));

      if (this.Notifications.CpuPercent is { } notifyCpu)
        text.Append("notify.cpu=").AppendLine(notifyCpu.ToString(CultureInfo.InvariantCulture));

      if (this.Notifications.MemoryPercent is { } notifyMemory)
        text.Append("notify.memory=").AppendLine(notifyMemory.ToString(CultureInfo.InvariantCulture));

      if (this.Notifications.DiskBytesPerSecond is { } notifyDisk)
        text.Append("notify.disk=").AppendLine(notifyDisk.ToString(CultureInfo.InvariantCulture));

      if (this.Notifications.Services.Count > 0)
        text.Append("notify.service=").AppendLine(string.Join(",", this.Notifications.Services));

      // In the words they were written in. A rule is a sentence somebody composed, and writing back
      // a reconstruction of it — even an equivalent one — makes them read it twice to see whether
      // the program understood them (PRD §84, §67).
      foreach (var alert in this.Notifications.Alerts)
        text.Append("alert=").AppendLine(alert.Text);
    }

    if (this.TerminalGraphs is { } graphs) {
      text.AppendLine();
      text.AppendLine("# How the terminal draws an in-row history: blocks, braille, ascii or numbers.");
      text.AppendLine("# Leave it out, or say auto, to let the terminal pick what it can draw.");
      text.Append("tui.graphs=").AppendLine(graphs switch {
        GraphStyle.Braille => "braille",
        GraphStyle.Ascii => "ascii",
        GraphStyle.Numbers => "numbers",
        _ => "blocks",
      });
    }

    if (this.TrayIndicators.Count > 0) {
      var names = new List<string>(this.TrayIndicators.Count);
      foreach (var kind in this.TrayIndicators)
        names.Add(IndicatorIcon.Name(kind));

      text.AppendLine();
      text.AppendLine("# Which resources get an indicator in the tray: cpu, memory, disk, network, gpu.");
      text.AppendLine("# Leave it out, or say none, for no tray at all.");
      text.Append("tray=").AppendLine(string.Join(",", names));
    }

    if (this.UsageHistory) {
      text.AppendLine();
      text.AppendLine("# Keep a record of what each program has cost this machine, across sessions.");
      text.AppendLine("# Off unless this says otherwise, and the file is not written until it does.");
      text.AppendLine("history.usage=true");
      if (this.UsageHistoryDays > 0)
        text.Append("history.usage.days=").AppendLine(this.UsageHistoryDays.ToString(CultureInfo.InvariantCulture));
    }

    if (!this.TerminalMouse) {
      text.AppendLine();
      text.AppendLine("# The terminal front-end leaves the mouse to the terminal, as --no-mouse does.");
      text.AppendLine("tui.mouse=false");
    }

    if (this.HideUnavailableTabs) {
      text.AppendLine();
      text.AppendLine("# A properties tab this machine cannot fill: `disabled` leaves it in place");
      text.AppendLine("# saying so, `hidden` takes it off the strip.");
      text.AppendLine("tabs.unavailable=hidden");
    }

    if (this.Thresholds != UsageThresholds.Default) {
      text.AppendLine();
      text.AppendLine("# When a cell is marked as busy. CPU and memory are percentages — CPU of one");
      text.AppendLine("# core, memory of the machine, GPU of the whole adapter — and the I/O pair are");
      text.AppendLine("# bytes per second.");
      text.Append("heat.cpu.warm=").AppendLine(this.Thresholds.WarmCpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
      text.Append("heat.cpu.hot=").AppendLine(this.Thresholds.HotCpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
      text.Append("heat.memory.warm=").AppendLine(this.Thresholds.WarmMemoryPercent.ToString("0.###", CultureInfo.InvariantCulture));
      text.Append("heat.memory.hot=").AppendLine(this.Thresholds.HotMemoryPercent.ToString("0.###", CultureInfo.InvariantCulture));
      text.Append("heat.io.warm=").AppendLine(this.Thresholds.WarmBytesPerSecond.ToString("0", CultureInfo.InvariantCulture));
      text.Append("heat.io.hot=").AppendLine(this.Thresholds.HotBytesPerSecond.ToString("0", CultureInfo.InvariantCulture));
      text.Append("heat.gpu.warm=").AppendLine(this.Thresholds.WarmGpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
      text.Append("heat.gpu.hot=").AppendLine(this.Thresholds.HotGpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
    }

    if (this.Colours.Count > 0) {
      text.AppendLine();
      text.AppendLine("# Colours, as #rrggbb. Only the ones named here are overridden; the rest follow");
      text.AppendLine($"# the program. The names are: {string.Join(", ", ColourNames)}");
      foreach (var (name, argb) in this.Colours)
        text.Append(_ColourPrefix).Append(name).Append("=#").AppendLine((argb & 0xFFFFFFu).ToString("x6", CultureInfo.InvariantCulture));
    }

    if (this.TerminalColours.Count > 0) {
      text.AppendLine();
      text.AppendLine("# The terminal's own palette, as #rrggbb. `<name>` is the ink and `<name>.bg`");
      text.AppendLine("# the ground; naming either replaces that appearance outright, so header,");
      text.AppendLine("# marked and match — the three that paint a ground — want both. A terminal");
      text.AppendLine("# that cannot show the exact colour is given the nearest it has, and one with");
      text.AppendLine("# no colour at all keeps its reverse video and its bold.");
      text.AppendLine($"# The names are: {string.Join(", ", TerminalColourNames)}");
      foreach (var (slot, argb) in this.TerminalColours)
        text.Append(_TerminalColourPrefix).Append(slot).Append("=#").AppendLine((argb & 0xFFFFFFu).ToString("x6", CultureInfo.InvariantCulture));
    }

    if (this.ColumnSets.Count > 0) {
      text.AppendLine();
      text.AppendLine("# Named column sets. Ones with the same name as a built-in preset replace it.");
      foreach (var (name, fields) in this.ColumnSets)
        text.Append(_ColumnSetPrefix).Append(name).Append('=').AppendLine(Join(fields));
    }

    if (this.Unknown.Count > 0) {
      text.AppendLine();
      text.AppendLine("# Written by a different version of ProcessManager and kept untouched.");
      foreach (var line in this.Unknown)
        text.AppendLine(line);
    }

    return text.ToString();
  }

  private static string Join(ProcessField[] fields) {
    var keys = new List<string>(fields.Length);
    foreach (var field in fields)
      keys.Add(FieldRegistry.Get(field).Key);

    return string.Join(",", keys);
  }

  /// <summary>
  /// <c>name:220,pid:60</c> — a field key and the width somebody gave it (PRD §11).
  /// </summary>
  /// <remarks>
  /// A pair this build cannot make sense of is skipped rather than failing the line, the same way an
  /// unknown field key is: a settings file written by a newer version must still open an older one.
  /// </remarks>
  /// <summary>
  /// The indicators named in a line, in the order they were named.
  /// </summary>
  /// <remarks>
  /// Order matters: it is the order they appear in the panel, and somebody who wrote "memory,cpu"
  /// meant memory first. A name this build does not know is skipped rather than failing the line,
  /// the same rule every other setting follows, and a duplicate is dropped — two icons of the same
  /// resource is a panel with a mistake in it rather than a preference.
  /// </remarks>
  private static IReadOnlyList<IndicatorKind> ParseIndicators(string value) {
    var chosen = new List<IndicatorKind>();
    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      if (part.Equals("none", StringComparison.OrdinalIgnoreCase))
        return [];

      var kind = part.ToLowerInvariant() switch {
        "cpu" or "processor" => IndicatorKind.Cpu,
        "memory" or "mem" => IndicatorKind.Memory,
        "disk" or "io" => IndicatorKind.Disk,
        "network" or "net" => IndicatorKind.Network,
        "gpu" or "graphics" => IndicatorKind.Gpu,
        _ => (IndicatorKind?)null,
      };

      if (kind is { } known && !chosen.Contains(known))
        chosen.Add(known);
    }

    return chosen;
  }

  private static bool TryParseWidths(string text, out IReadOnlyList<KeyValuePair<ProcessField, int>> widths) {
    var parsed = new List<KeyValuePair<ProcessField, int>>();
    foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var colon = part.LastIndexOf(':');
      if (colon <= 0
          || !FieldRegistry.TryParse(part[..colon], out var field)
          || !int.TryParse(part[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
          || width <= 0)
        continue;

      parsed.Add(new(field, width));
    }

    widths = parsed;
    return parsed.Count > 0;
  }

  /// <summary>The word a grouping is written as, which is also what <c>--group</c> takes.</summary>
  public static string NameOfGrouping(ProcessGrouping grouping) => grouping switch {
    ProcessGrouping.None => "none",
    ProcessGrouping.ParentTree => "tree",
    ProcessGrouping.User => "user",
    ProcessGrouping.Session => "session",
    ProcessGrouping.Service => "service",
    ProcessGrouping.Executable => "executable",
    ProcessGrouping.Container => "container",
    ProcessGrouping.Package => "package",
    ProcessGrouping.Publisher => "publisher",
    ProcessGrouping.Category => "category",
    _ => "cgroup",
  };

  /// <summary>Reads a grouping by name. False for a word no build of this understands.</summary>
  public static bool TryParseGrouping(string? text, out ProcessGrouping grouping) {
    grouping = ProcessGrouping.None;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    switch (text.Trim().ToLowerInvariant()) {
      case "none" or "flat" or "off": grouping = ProcessGrouping.None; return true;
      case "tree" or "parent" or "parent-tree": grouping = ProcessGrouping.ParentTree; return true;
      case "user": grouping = ProcessGrouping.User; return true;
      case "session": grouping = ProcessGrouping.Session; return true;
      case "service" or "unit": grouping = ProcessGrouping.Service; return true;
      case "executable" or "exe" or "image": grouping = ProcessGrouping.Executable; return true;
      case "container": grouping = ProcessGrouping.Container; return true;
      case "cgroup": grouping = ProcessGrouping.Cgroup; return true;
      case "package" or "pkg": grouping = ProcessGrouping.Package; return true;
      case "publisher" or "signer" or "signed.by": grouping = ProcessGrouping.Publisher; return true;
      case "category" or "kind" or "friendly": grouping = ProcessGrouping.Category; return true;
      default: return false;
    }
  }

  private static bool TryParseFields(string text, out ProcessField[] fields) {
    var parsed = new List<ProcessField>();
    foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      // A field this build does not know is skipped rather than failing the line: a settings file
      // written by a newer version must still open an older one.
      if (FieldRegistry.TryParse(part, out var field))
        parsed.Add(field);
    }

    fields = [.. parsed];
    return fields.Length > 0;
  }

  /// <summary>
  /// Every colour the file may name. Written into the file's own comment, so somebody editing it by
  /// hand is told what can go there rather than having to guess (PRD §67).
  /// </summary>
  public static IReadOnlyList<string> ColourNames { get; } = [
    "new", "exited", "zombie", "suspended", "system", "elevated", "service", "own",
    "image.replaced", "packaged", "managed",
    "cpu", "cpu.kernel", "memory", "io", "plot.background", "plot.grid",
  ];

  /// <summary>
  /// Every appearance the terminal draws with, and so every name <c>tui.color.</c> may carry
  /// (PRD §57.4, §67).
  /// </summary>
  /// <remarks>
  /// Ten and not one per meaning, because ten is what the renderer has: a cell's appearance is one
  /// byte, and half a dozen meanings share each of these inks. <c>header</c>, <c>marked</c> and
  /// <c>match</c> paint a ground as well as an ink, which is what <c>.bg</c> is for.
  /// <para>
  /// Here rather than beside the renderer for the reason <c>GraphStyle</c> is: the settings file has
  /// to be able to tell a name it does not know from a name a newer build added, and it cannot do
  /// that from inside a front-end this assembly does not reference.
  /// </para>
  /// </remarks>
  public static IReadOnlyList<string> TerminalColourNames { get; } = [
    "normal", "dim", "accent", "good", "warn", "bad", "header", "selected", "marked", "match",
  ];

  /// <summary>
  /// Whether <paramref name="slot"/> is one of the ten, with or without the <c>.bg</c> half.
  /// </summary>
  /// <remarks>
  /// A name that is not one of them is kept verbatim rather than dropped, like every other line this
  /// build does not understand: an eleventh appearance in a later build must survive being read by
  /// this one.
  /// </remarks>
  private static bool IsTerminalColourKey(string slot) {
    var name = slot.EndsWith(_BackgroundSuffix, StringComparison.OrdinalIgnoreCase)
      ? slot[..^_BackgroundSuffix.Length]
      : slot;

    foreach (var known in TerminalColourNames)
      if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
        return true;

    return false;
  }

  /// <summary>The shared reader, so the settings and the rules mean one thing by a hash (§66).</summary>
  private static bool TryParseColour(string text, out uint argb) => Query.Colour.TryParse(text, out argb);

  /// <summary>
  /// A threshold, or the one already there.
  /// </summary>
  /// <remarks>
  /// A line that will not parse leaves the setting alone rather than zeroing it — a threshold of
  /// nought marks every cell, which is the most annoying possible response to a typo.
  /// </remarks>
  private static double Number(string text, double fallback)
    => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
      ? value
      : fallback;

  /// <summary>
  /// A count of columns. Negative is a typo rather than a preference, and so is a number larger than
  /// any column list anybody will ever have — both leave the setting alone.
  /// </summary>
  private static bool TryParseCount(string text, out int value)
    => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 64;

  private static bool TryParseBool(string text, out bool value) {
    switch (text.ToLowerInvariant()) {
      case "true" or "yes" or "on" or "1": value = true; return true;
      case "false" or "no" or "off" or "0": value = false; return true;
      default: value = false; return false;
    }
  }

  /// <summary>
  /// A notification threshold, which is a number or nothing at all (PRD §64).
  /// </summary>
  /// <remarks>
  /// Empty clears the rule, which is how somebody switches one off without deleting the line. A
  /// negative number is a typo rather than a preference — there is no reading here that can be below
  /// nought — and leaves the rule as it was rather than arming it at a value that would fire on
  /// every process on the machine.
  /// </remarks>
  private static bool TryParseThreshold(string text, out double? value) {
    value = null;
    if (text.Length == 0)
      return true;

    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
      return false;

    value = number;
    return true;
  }

  /// <summary>
  /// A comma-separated list of names, with the empty entries dropped (PRD §64).
  /// </summary>
  /// <remarks>
  /// Dropping the empties matters more than it looks: an empty string matches nothing under
  /// <c>Equals</c>, so a trailing comma would silently do nothing at all rather than doing something
  /// wrong — but a list that keeps them cannot be told from one somebody meant to leave empty, and
  /// the rendered file would grow a comma every time it was written out.
  /// </remarks>
  private static string[] SplitList(string text) {
    var parts = text.Split(',');
    var kept = new List<string>(parts.Length);
    foreach (var part in parts)
      if (part.Trim() is { Length: > 0 } name)
        kept.Add(name);

    return [.. kept];
  }

  #endregion

}
