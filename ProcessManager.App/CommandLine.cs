using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.App;

/// <summary>Which face of the program the arguments asked for.</summary>
internal enum RunMode : byte { Desktop, Terminal, List, Find, Kill, SelfTest, HelperCheck, Help, HelpFields, Version }

/// <summary>
/// The whole command line, parsed once into a value.
/// </summary>
/// <remarks>
/// Hand-written rather than reached for a parser package: the surface is a dozen switches, it has to
/// stay reflection-free for NativeAOT (PRD §2), and a dependency that pulls in reflection to read a
/// dozen switches is a poor trade.
/// </remarks>
internal sealed record CommandLineOptions {

  public RunMode Mode { get; init; } = RunMode.Desktop;
  public ProcessField SortColumn { get; init; } = ProcessField.CpuPercent;
  public bool SortDescending { get; init; } = true;
  public bool TreeMode { get; init; }

  /// <summary>True when --flat was given, so the desktop's tree default can be overridden.</summary>
  public bool FlatRequested { get; init; }

  /// <summary>
  /// Draw the terminal's in-row history with the ASCII ramp rather than the eighth-block characters.
  /// Detected from the locale otherwise; this is for a terminal the detection gets wrong.
  /// </summary>
  public bool AsciiOnly { get; init; }
  public bool Json { get; init; }
  public bool AllUsers { get; init; } = true;
  public bool KillTree { get; init; }
  public int TargetPid { get; init; }
  public string? Pattern { get; init; }

  /// <summary>A filter in the query language of PRD §56, applied to --list and the two UIs.</summary>
  public string? Filter { get; init; }

  /// <summary>What --list writes: text, csv, tsv, json, jsonl or markdown (PRD §61).</summary>
  public ExportFormat Format { get; init; } = ExportFormat.Text;

  /// <summary>Which fields --list writes, in order. Null means the default set.</summary>
  public ProcessField[]? Fields { get; init; }

  /// <summary>
  /// Whether anything this run asked for needs the LSM label, which costs a file per process.
  /// </summary>
  /// <remarks>
  /// Inferred rather than flagged: naming the field in --columns or in --filter is already a clear
  /// request for it, and a separate --security switch would only be a way to get an empty column
  /// by forgetting it (PRD §5.4).
  /// </remarks>
  public bool WantsSecurityContext {
    get {
      if (this.Fields is { } fields)
        // Not "field": in C# 14 that is a keyword inside a property accessor and binds to the
        // synthesised backing field rather than to the loop variable.
        foreach (var candidate in fields)
          if (candidate == ProcessField.SecurityContext)
            return true;

      var key = FieldRegistry.Get(ProcessField.SecurityContext).Key;
      return this.Filter is { } filter
        && filter.Contains(key, StringComparison.OrdinalIgnoreCase);
    }
  }

  /// <summary>
  /// Every field, printed from the registry rather than from a list kept alongside it — which is how
  /// the old help text came to name ten sort keys when there were seventeen (PRD §5.1).
  /// </summary>
  public static string FieldHelpText {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine("Fields. Any of these can be used with --sort, --filter and --find,");
      text.AppendLine("by the key or by any of its aliases.");
      text.AppendLine();
      text.AppendLine($"  {"KEY",-20} {"ALIASES",-24} DESCRIPTION");
      foreach (var descriptor in FieldRegistry.All) {
        var aliases = descriptor.Aliases?.Replace(' ', ',') ?? "";
        var note = descriptor.Platforms == FieldPlatforms.All
          ? string.Empty
          : $" [{descriptor.Platforms.ToString().Replace(", ", "/", StringComparison.Ordinal)} only]";

        text.AppendLine($"  {descriptor.Key,-20} {aliases,-24} {descriptor.Description}{note}");
      }

      text.AppendLine();
      text.AppendLine("Filters: field:value  field=value  field>value  field>=value  field<value");
      text.AppendLine("""         field!=value  field:/regex/  "quoted text"  /regex/""");
      text.AppendLine("         AND OR NOT  &&  ||  !  ( )   — terms side by side mean AND");
      text.AppendLine("Sizes:   1024  1K  1KiB  1kB  1MiB  1GB      Times: 500ms  1.5s  2h");
      text.AppendLine();
      text.AppendLine("  procman --filter 'cpu:>50 AND user:alice'");
      text.AppendLine("  procman --filter 'memory:>1GiB NOT name:chrome'");
      return text.ToString();
    }
  }
  public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>Read a recorded /proc tree instead of the live one (PRD §9.1).</summary>
  public string? ProbeRoot { get; init; }

  /// <summary>Compose one frame, write it here as text, and exit (PRD §9.6).</summary>
  public string? CaptureFramePath { get; init; }

  /// <summary>Compare the captured frame against this file and exit non-zero on a difference.</summary>
  public string? GoldenFramePath { get; init; }

  /// <summary>
  /// How many samples to take before capturing. Two is the minimum for any rate to exist at all; a
  /// screenshot wants a dozen so the in-row plots have a shape rather than a single mark.
  /// </summary>
  public int CaptureSamples { get; init; } = 2;

  /// <summary>Bring the window up, photograph it and exit — the CI desktop smoke leg.</summary>
  public string? ShootPath { get; init; }

  /// <summary>
  /// Hold the window open this many seconds before exiting, so something outside can photograph it.
  /// Zero keeps the smoke run's behaviour: up, described, gone.
  /// </summary>
  public double ShootHoldSeconds { get; init; }

  /// <summary>Also write the captured terminal frame as an SVG picture of itself.</summary>
  public string? CaptureSvgPath { get; init; }

  /// <summary>
  /// Whether the privileged helper may be started at all. It is never started without a request that
  /// needs it, so the flag is for people who would rather it could not happen (PRD §8.1).
  /// </summary>
  public bool UseHelper { get; init; } = true;

  public string? Error { get; init; }

  public static CommandLineOptions Parse(string[] args) {
    var options = new CommandLineOptions();
    var explicitMode = false;

    for (var i = 0; i < args.Length; ++i) {
      var argument = args[i];
      var (name, inlineValue) = Split(argument);

      switch (name) {
        case "--tui" or "-t":
          options = options with { Mode = RunMode.Terminal };
          explicitMode = true;
          break;
        case "--list" or "-l":
          options = options with { Mode = RunMode.List };
          explicitMode = true;
          break;
        case "--format": {
          if (!TryValue(args, ref i, inlineValue, out var formatName))
            return options with { Error = "--format needs one of: text, csv, tsv, json, jsonl, markdown" };
          if (!Exporter.TryParseFormat(formatName, out var format))
            return options with { Error = $"unknown format '{formatName}'; try text, csv, tsv, json, jsonl or markdown" };

          options = options with { Format = format };
          break;
        }

        case "--columns": {
          if (!TryValue(args, ref i, inlineValue, out var list))
            return options with { Error = "--columns needs a comma-separated list of fields" };
          if (!Exporter.TryParseFields(list, out var fields, out var reason))
            return options with { Error = $"--columns: {reason}" };

          options = options with { Fields = fields };
          break;
        }

        case "--filter": {
          if (!TryValue(args, ref i, inlineValue, out var query))
            return options with { Error = "--filter needs a query" };

          // Parsed here rather than at first use so a typo is reported before the screen clears,
          // and reported with the reason rather than as an empty list.
          if (!ProcessQuery.TryParse(query, out _, out var problem))
            return options with { Error = $"--filter: {problem}" };

          options = options with { Filter = query };
          break;
        }

        case "--find" or "-f": {
          if (!TryValue(args, ref i, inlineValue, out var pattern))
            return options with { Error = "--find needs a pattern" };

          options = options with { Mode = RunMode.Find, Pattern = pattern };
          explicitMode = true;
          break;
        }
        case "--kill": {
          if (!TryValue(args, ref i, inlineValue, out var pid) || !int.TryParse(pid, out var target))
            return options with { Error = "--kill needs a pid" };

          options = options with { Mode = RunMode.Kill, TargetPid = target };
          explicitMode = true;
          break;
        }
        case "--tree":
          options = options with { TreeMode = true, KillTree = true };
          break;
        case "--flat":
          // The desktop opens as a tree, which is what the reference tools do. This starts it flat
          // and sorted, which is what somebody looking for the busiest process wants — and what a
          // screenshot of a process manager should show.
          options = options with { TreeMode = false, FlatRequested = true };
          break;
        case "--json":
          options = options with { Json = true };
          break;
        case "--sort": {
          if (!TryValue(args, ref i, inlineValue, out var column))
            return options with { Error = "--sort needs a column" };
          if (!FieldRegistry.TryParse(column, out var parsed))
            return options with { Error = $"unknown sort column '{column}'" };

          options = options with { SortColumn = parsed, SortDescending = parsed.PrefersDescending() };
          break;
        }
        case "--interval": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !double.TryParse(text, out var seconds) || seconds <= 0)
            return options with { Error = "--interval needs a positive number of seconds" };

          options = options with { Interval = TimeSpan.FromSeconds(seconds) };
          break;
        }
        case "--user":
          options = options with { AllUsers = false };
          break;
        case "--ascii":
          options = options with { AsciiOnly = true };
          break;
        case "--no-helper":
          options = options with { UseHelper = false };
          break;
        case "--probe-root": {
          if (!TryValue(args, ref i, inlineValue, out var root))
            return options with { Error = "--probe-root needs a directory" };

          options = options with { ProbeRoot = root };
          break;
        }
        case "--capture-frame": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--capture-frame needs a file" };

          options = options with { Mode = RunMode.Terminal, CaptureFramePath = path };
          explicitMode = true;
          break;
        }
        case "--capture-samples": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !int.TryParse(text, out var samples) || samples < 2)
            return options with { Error = "--capture-samples needs a number of at least 2" };

          options = options with { CaptureSamples = samples };
          break;
        }
        case "--shoot-hold": {
          if (!TryValue(args, ref i, inlineValue, out var text) || !double.TryParse(text, out var seconds) || seconds < 0)
            return options with { Error = "--shoot-hold needs a number of seconds" };

          options = options with { ShootHoldSeconds = seconds };
          break;
        }
        case "--capture-svg": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--capture-svg needs a file" };

          options = options with { Mode = RunMode.Terminal, CaptureSvgPath = path };
          explicitMode = true;
          break;
        }
        case "--compare-golden": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--compare-golden needs a file" };

          options = options with { GoldenFramePath = path };
          break;
        }
        case "--shoot": {
          if (!TryValue(args, ref i, inlineValue, out var path))
            return options with { Error = "--shoot needs a directory" };

          options = options with { Mode = RunMode.Desktop, ShootPath = path };
          explicitMode = true;
          break;
        }
        case "--self-test":
          options = options with { Mode = RunMode.SelfTest };
          explicitMode = true;
          break;
        case "--helper-check":
          options = options with { Mode = RunMode.HelperCheck };
          explicitMode = true;
          break;
        case "--help-fields":
          return options with { Mode = RunMode.HelpFields };

        case "--help" or "-h" or "-?":
          return options with { Mode = RunMode.Help };
        case "--version" or "-V":
          return options with { Mode = RunMode.Version };
        default:
          return options with { Error = $"unknown option '{argument}'" };
      }
    }

    // With no display there is nothing for the desktop front-end to open. Falling back to the
    // terminal is what makes `procman` over SSH do the useful thing instead of the failing one.
    if (!explicitMode && options.Mode == RunMode.Desktop && !HasDisplay())
      options = options with { Mode = RunMode.Terminal };

    return options;
  }

  private static bool HasDisplay()
    => OperatingSystem.IsWindows()
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

  private static (string Name, string? Value) Split(string argument) {
    var equals = argument.IndexOf('=', StringComparison.Ordinal);
    return equals < 0 ? (argument, null) : (argument[..equals], argument[(equals + 1)..]);
  }

  private static bool TryValue(string[] args, ref int index, string? inlineValue, out string value) {
    if (inlineValue is not null) {
      value = inlineValue;
      return true;
    }

    if (index + 1 >= args.Length) {
      value = string.Empty;
      return false;
    }

    value = args[++index];
    return true;
  }

  public const string HelpText = """
    procman — a process manager for Windows and Linux

    Usage:
      procman                        the desktop UI (falls back to the terminal with no display)
      procman --tui                  the terminal UI
      procman --list [--json]        one snapshot to stdout, then exit
      procman --find <pattern>       which processes match, by name, command line or open file
      procman --help-fields          every field that can be sorted, filtered or shown
      procman --kill <pid> [--tree]  end a process, optionally with its descendants

    Options:
      --sort <field>     any field key; see --help-fields for the list
      --filter <query>   show only matching processes: 'cpu:>50', 'user:alice AND memory:>1GiB'
      --format <fmt>     text (default), csv, tsv, json, jsonl, markdown
      --columns <a,b,c>  which fields to write; see --help-fields
      --tree             show the process tree (with --kill: the whole subtree)
      --flat             start with a flat list sorted by CPU rather than a tree
      --user             only this user's processes
      --interval <s>     seconds between samples (default 1)
      --json             the same as --format=json
      --probe-root <d>   read a recorded /proc tree instead of the live one
      --ascii            draw the terminal's history columns with ASCII rather than block characters
      --no-helper        never start the privileged helper, even for an action that needs it
      --self-test        check the probe against the runtime's own view of this process
      --helper-check     talk to the privileged helper over its pipe, unelevated, and check it
      --help, --version

    Exit codes: 0 success · 1 error · 2 nothing matched
    """;

}
