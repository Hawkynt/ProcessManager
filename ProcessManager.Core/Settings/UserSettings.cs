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

  public ProcessField SortField { get; init; } = ProcessField.CpuPercent;

  public bool SortDescending { get; init; } = true;

  public bool TreeMode { get; init; } = true;

  /// <summary>Which convention CPU percentages are expressed in (PRD §3.2).</summary>
  public CpuPercentMode CpuMode { get; init; } = CpuPercentMode.Normalized;

  /// <summary>Draw the terminal's history columns with block characters rather than ASCII.</summary>
  public bool BlockCharacters { get; init; } = true;

  /// <summary>The columns the window opens with.</summary>
  public ProcessField[] DesktopColumns { get; init; } = [];

  /// <summary>The columns the terminal opens with.</summary>
  public ProcessField[] TerminalColumns { get; init; } = [];

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
          if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
              && seconds is > 0 and <= 3600)
            settings = settings with { IntervalSeconds = seconds };

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

  private static bool TryParseBool(string text, out bool value) {
    switch (text.ToLowerInvariant()) {
      case "true" or "yes" or "on" or "1": value = true; return true;
      case "false" or "no" or "off" or "0": value = false; return true;
      default: value = false; return false;
    }
  }

  #endregion

}
