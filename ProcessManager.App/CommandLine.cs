using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.App;

/// <summary>Which face of the program the arguments asked for.</summary>
internal enum RunMode : byte { Desktop, Terminal, List, Find, Kill, SelfTest, HelperCheck, Help, Version }

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
  public ProcessColumn SortColumn { get; init; } = ProcessColumn.CpuPercent;
  public bool SortDescending { get; init; } = true;
  public bool TreeMode { get; init; }
  public bool Json { get; init; }
  public bool AllUsers { get; init; } = true;
  public bool KillTree { get; init; }
  public int TargetPid { get; init; }
  public string? Pattern { get; init; }
  public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

  /// <summary>Read a recorded /proc tree instead of the live one (PRD §9.1).</summary>
  public string? ProbeRoot { get; init; }

  /// <summary>Compose one frame, write it here as text, and exit (PRD §9.6).</summary>
  public string? CaptureFramePath { get; init; }

  /// <summary>Compare the captured frame against this file and exit non-zero on a difference.</summary>
  public string? GoldenFramePath { get; init; }

  /// <summary>Bring the window up, photograph it and exit — the CI desktop smoke leg.</summary>
  public string? ShootPath { get; init; }

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
        case "--json":
          options = options with { Json = true };
          break;
        case "--sort": {
          if (!TryValue(args, ref i, inlineValue, out var column))
            return options with { Error = "--sort needs a column" };
          if (!ProcessColumnExtensions.TryParse(column, out var parsed))
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
      procman --kill <pid> [--tree]  end a process, optionally with its descendants

    Options:
      --sort <column>    cpu, mem, pid, name, user, threads, read, write, start, handles
      --tree             show the process tree (with --kill: the whole subtree)
      --user             only this user's processes
      --interval <s>     seconds between samples (default 1)
      --json             machine-readable output for --list and --find
      --probe-root <d>   read a recorded /proc tree instead of the live one
      --self-test        check the probe against the runtime's own view of this process
      --helper-check     talk to the privileged helper over its pipe, unelevated, and check it
      --help, --version

    Exit codes: 0 success · 1 error · 2 nothing matched
    """;

}
