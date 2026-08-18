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
      ["security"] = [
        ProcessField.Name, ProcessField.Pid, ProcessField.UserName, ProcessField.Elevated,
        ProcessField.Seccomp, ProcessField.NoNewPrivileges, ProcessField.Capabilities,
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

  /// <summary>
  /// Parses a settings file. A line that cannot be understood is kept verbatim and never thrown
  /// away, and a value that cannot be parsed leaves its setting at the default rather than failing
  /// the whole file: a settings file with one bad line must still start the program.
  /// </summary>
  public static UserSettings Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);

    var settings = new UserSettings();
    var sets = new Dictionary<string, ProcessField[]>(StringComparer.OrdinalIgnoreCase);
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

        default:
          unknown.Add(line);
          break;
      }
    }

    return settings with { ColumnSets = sets, Unknown = unknown };
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

  private static bool TryParseBool(string text, out bool value) {
    switch (text.ToLowerInvariant()) {
      case "true" or "yes" or "on" or "1": value = true; return true;
      case "false" or "no" or "off" or "0": value = false; return true;
      default: value = false; return false;
    }
  }

  #endregion

}
