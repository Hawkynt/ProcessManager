using System.Globalization;
using System.Text;
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

  /// <summary>Draw the terminal's history columns with block characters rather than ASCII.</summary>
  public bool BlockCharacters { get; init; } = true;

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
  /// Whether the performance page opens on whatever is under the greatest load (PRD §45.3).
  /// </summary>
  /// <remarks>
  /// On by default, because somebody opening that page has a machine that is doing something and
  /// wants to know what. Off for the people who keep it open on one resource and do not want it
  /// moved out from under them by a disk that was briefly busy — which is a preference and not a
  /// mistake, and is why it is a setting rather than a decision.
  /// </remarks>
  public bool PerformanceOpensOnBusiest { get; init; } = true;

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
  /// When a cell is busy enough to be marked (PRD §23).
  /// </summary>
  /// <remarks>
  /// Worth being settable because the right answer depends on the machine: a whole core is a lot on
  /// a laptop and nothing on a build server, and a hundred megabytes a second is saturation for a
  /// spinning disk and idle for an NVMe.
  /// </remarks>
  public UsageThresholds Thresholds { get; init; } = UsageThresholds.Default;

  /// <summary>Named column sets, as PRD §11 requires and §94 names.</summary>
  public IReadOnlyDictionary<string, ProcessField[]> ColumnSets { get; init; }
    = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase);

  /// <summary>Lines this build did not understand, kept so an older build cannot eat them.</summary>
  public IReadOnlyList<string> Unknown { get; init; } = [];

  #region the presets of §94

  /// <summary>
  /// The built-in column sets. Offered when the file names none, and never written to it — a preset
  /// that got copied into everybody's settings could never be improved again.
  /// </summary>
  public static IReadOnlyDictionary<string, ProcessField[]> Presets { get; } =
    new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase) {
      ["basic"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.State, ProcessField.CpuPercent,
        ProcessField.PrivateBytes, ProcessField.IoTotalRate,
      ],
      ["expert"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.ParentPid, ProcessField.CpuPercent,
        ProcessField.PrivateBytes, ProcessField.WorkingSetBytes, ProcessField.IoTotalRate,
        ProcessField.UserName, ProcessField.StartTime, ProcessField.CommandLine,
      ],
      // Both accounts, because the pair is the story: a row where they differ is a process running
      // with an authority nobody at the keyboard has. The bounding set is here rather than the
      // permitted one because it answers the question a reader is usually asking — what could this
      // ever do — while the effective set answers what it may do this instant.
      ["security"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.EffectiveUserName,
        ProcessField.PrivilegeChanged, ProcessField.Elevated, ProcessField.Seccomp,
        ProcessField.NoNewPrivileges, ProcessField.Capabilities, ProcessField.BoundingCapabilities,
        ProcessField.SecurityContext, ProcessField.ImagePath,
      ],
      ["io"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.ReadBytesPerSecond,
        ProcessField.WriteBytesPerSecond, ProcessField.IoTotalRate, ProcessField.IoHistory,
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
    var unknown = new List<string>();

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

        case "blocks":
          if (TryParseBool(value, out var blocks))
            settings = settings with { BlockCharacters = blocks };

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

        case "window.split":
          if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var split) && split is >= 10 and <= 90)
            settings = settings with { SplitPercent = split };

          break;

        default:
          unknown.Add(line);
          break;
      }
    }

    return settings with { ColumnSets = sets, Colours = colours, Unknown = unknown };
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
  /// <c>#rrggbb</c>, with or without the hash, and <c>#rgb</c> for the people who write CSS. The
  /// alpha is never taken from the file: a half-transparent row colour is a bug report, not a preference.
  /// </summary>
  private static bool TryParseColour(string text, out uint argb) {
    argb = 0;
    var digits = text.StartsWith('#') ? text[1..] : text;
    if (digits.Length == 3) {
      Span<char> expanded = stackalloc char[6];
      for (var i = 0; i < 3; ++i) {
        expanded[i * 2] = digits[i];
        expanded[(i * 2) + 1] = digits[i];
      }

      digits = new(expanded);
    }

    if (digits.Length != 6 || !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
      return false;

    argb = 0xFF000000u | rgb;
    return true;
  }

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

  #endregion

}
