using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Ui.Terminal;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// One binary, both front-ends, and the non-interactive modes that make it scriptable.
/// </summary>
internal static class Program {

  private const int _ExitOk = 0;
  private const int _ExitError = 1;
  private const int _ExitNoMatch = 2;

  private static int Main(string[] args) {
    // Settings first, so the command line is layered over them rather than the other way round.
    // A settings file that is missing or unreadable yields the defaults and never stops the program
    // starting (PRD §81).
    var settingsPath = SettingsPathFrom(args);
    var settings = Settings.SettingsStore.Load(settingsPath);
    var options = CommandLineOptions.Parse(args, settings);
    if (options.Error is { } error) {
      Console.Error.WriteLine($"procman: {error}");
      Console.Error.WriteLine("Try 'procman --help'.");
      return _ExitError;
    }

    if (options.SaveSettings && !SaveSettings(options, settings, settingsPath))
      Console.Error.WriteLine("procman: the settings could not be written; carrying on with them unsaved.");

    switch (options.Mode) {
      case RunMode.Help:
        Console.WriteLine(CommandLineOptions.HelpText);
        return _ExitOk;
      case RunMode.HelpFields:
        Console.Write(CommandLineOptions.FieldHelpText);
        return _ExitOk;
      case RunMode.Version:
        Console.WriteLine($"procman {typeof(Program).Assembly.GetName().Version}");
        return _ExitOk;
    }

    var probe = ProbeFactory.Create(
      options.ProbeRoot,
      options.UseHelper,
      options.WantsSecurityContext,
      options.WantsProportionalSetSize
    );
    if (probe is null) {
      Console.Error.WriteLine($"procman: there is no probe for this platform yet ({Environment.OSVersion.Platform}).");
      Console.Error.WriteLine("Linux and Windows are supported; macOS is PRD §10 M9.");
      return _ExitError;
    }

    using (probe)
    using (ProbeFactory.Elevated) {
      var actions = ProbeFactory.CreateActions(options.ProbeRoot);
      using var sampler = new Sampler(probe);

      return options.Mode switch {
        RunMode.List => RunList(sampler, options),
        RunMode.Find => RunFind(sampler, probe, options),
        RunMode.Kill => RunKill(sampler, actions, options),
        RunMode.Host => HostReport.Run(sampler, probe),
        RunMode.Startup => StartupReport.Run(probe, options),
        RunMode.Users => UsersReport.Run(sampler, probe),
        RunMode.Services => ServicesReport.Run(probe, options),
        RunMode.Connections => ConnectionsReport.Run(sampler, probe, options),
        RunMode.SelfTest => SelfTest.Run(sampler, probe.Description, probe),
        RunMode.HelperCheck => HelperCheck.Run(),
        RunMode.Terminal => RunTerminal(sampler, probe, actions, options),
        _ => RunDesktop(sampler, probe, actions, options, settings, settingsPath),
      };
    }
  }

  #region interactive

  private static int RunTerminal(Sampler sampler, ISystemProbe probe, IProcessActions? actions, CommandLineOptions options) {
    if (options.CaptureFramePath is null && options.CaptureSvgPath is null) {
      using var host = new TerminalHost();
      host.Run(sampler, probe, actions, options.Interval);
      return _ExitOk;
    }

    // Headless: compose two frames (the second is the one with rates in it) and write the text.
    // This is the CI gate for the renderer — see PRD §9.6.
    // Pinned, both of them: a captured frame is compared byte for byte, so neither the colour depth
    // nor the block characters may come from whatever the capturing machine's environment happens to
    // say. --ascii overrides for a picture of the fallback.
    var ui = new TerminalUi(sampler, probe, actions, 120, 40, ColorDepth.None) {
      ShowTiming = false,
      UseBlockCharacters = !options.AsciiOnly,
    };
    ui.View.TreeMode = options.TreeMode;
    ui.View.SortColumn = options.SortColumn;
    ui.View.SortDescending = options.SortDescending;
    for (var i = 0; i < options.CaptureSamples; ++i) {
      ui.Update();
      // Only between samples, and only when more than the minimum were asked for: a golden frame is
      // taken with two and must not pay a second of wall-clock for it.
      if (options.CaptureSamples > 2 && i < options.CaptureSamples - 1)
        Thread.Sleep(options.Interval);
    }

    var frame = ui.Screen.Capture();
    if (options.CaptureFramePath is not null)
      File.WriteAllText(options.CaptureFramePath, frame);

    if (options.CaptureSvgPath is not null) {
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CaptureSvgPath))!);
      File.WriteAllText(
        options.CaptureSvgPath,
        FrameSvg.Render(frame, "procman --tui", ui.Screen.CaptureAttributes(), ui.Screen.Width)
      );
    }

    if (options.GoldenFramePath is null)
      return _ExitOk;

    var golden = File.ReadAllText(options.GoldenFramePath);
    if (string.Equals(Normalize(golden), Normalize(frame), StringComparison.Ordinal))
      return _ExitOk;

    Console.Error.WriteLine($"procman: the composed frame differs from {options.GoldenFramePath}");
    return _ExitError;

    static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
  }

  private static int RunDesktop(
    Sampler sampler,
    ISystemProbe probe,
    IProcessActions? actions,
    CommandLineOptions options,
    Settings.UserSettings settings,
    string? settingsPath
  ) {
    // The command line layered over the file, which is the order CommandLineOptions already parsed
    // them in — so the window opens as the file left it unless this run said otherwise. Until now
    // none of --sort, --interval or --columns reached the window at all; it used its own defaults
    // and the flags were quietly ignored outside --list and --tui.
    var effective = settings with {
      IntervalSeconds = options.Interval.TotalSeconds,
      SortField = options.SortColumn,
      SortDescending = options.SortDescending,
      TreeMode = options.TreeMode,
      CpuMode = options.CpuMode,
      DesktopColumns = options.Fields is { Length: > 0 } fields ? fields : settings.DesktopColumns,
    };

    var result = DesktopLauncher.TryRun(
      sampler,
      probe,
      actions,
      options.ShootPath,
      options.ShootHoldSeconds,
      options.FlatRequested,
      effective,
      settingsPath ?? options.SettingsPath
    );
    if (result is null)
      return _ExitOk;

    Console.Error.WriteLine($"procman: {result}");
    Console.Error.WriteLine("Falling back to the terminal UI. Use --tui to ask for it directly.");
    return RunTerminal(sampler, probe, actions, options with { Mode = RunMode.Terminal });
  }

  #endregion

  #region one-shot

  private static int RunList(Sampler sampler, CommandLineOptions options) {
    // Two samples, one interval apart: the first has no rates at all, and a list whose CPU column is
    // all dashes is not what anybody meant by --list (PRD §3.2).
    sampler.Sample();
    Thread.Sleep(options.Interval);
    sampler.Sample();

    var view = new ProcessView {
      SortColumn = options.SortColumn,
      SortDescending = options.SortDescending,
      TreeMode = options.TreeMode,
      TextFilter = options.Filter,
    };

    view.Rebuild(sampler.Current, sampler.Delta);
    var snapshot = sampler.Current;
    var delta = sampler.Delta;

    // One writer for every format, over any set of registry fields (PRD §61). --json is kept as a
    // spelling of --format=json because it is what every script already passes.
    var format = options.Json ? ExportFormat.Json : options.Format;
    Exporter.Write(Console.Out, format, snapshot, delta, view, options.Fields, options.TreeMode);
    return _ExitOk;
  }

  /// <summary>
  /// <c>--find</c>: which process is using this (PRD §33).
  /// </summary>
  private static int RunFind(Sampler sampler, ISystemProbe probe, CommandLineOptions options) {
    sampler.Sample();

    var pattern = options.Pattern ?? string.Empty;
    var matches = ResourceSearch.Find(probe, sampler.Current, pattern);
    foreach (var match in matches)
      Console.WriteLine(
        $"{match.Pid,7} {match.UserName ?? "?",-12} {match.ProcessName,-24} {Describe(match.Kind)}  {match.Detail}"
      );

    if (matches.Count == 0)
      Console.Error.WriteLine($"procman: nothing matched '{pattern}'");

    return matches.Count > 0 ? _ExitOk : _ExitNoMatch;
  }

  private static string Describe(ResourceKind kind) => kind switch {
    ResourceKind.Name => "name        ",
    ResourceKind.CommandLine => "command line",
    ResourceKind.ImagePath => "image       ",
    ResourceKind.OpenFile => "open file   ",
    ResourceKind.MappedModule => "mapped      ",
    ResourceKind.Socket => "socket      ",
    ResourceKind.Service => "service     ",
    _ => "            ",
  };

  private static int RunKill(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (actions is null) {
      Console.Error.WriteLine("procman: this platform has no actions yet.");
      return _ExitError;
    }

    sampler.Sample();
    var snapshot = sampler.Current;
    var target = ProcessTree.Find(snapshot, options.TargetPid);
    if (target.IsNone) {
      Console.Error.WriteLine($"procman: no process with pid {options.TargetPid}");
      return _ExitNoMatch;
    }

    var targets = options.KillTree
      ? ProcessTree.DescendantsFirst(snapshot, options.TargetPid)
      : [target];

    var failed = 0;
    foreach (var key in targets) {
      var result = actions.Terminate(key);
      if (result.Succeeded)
        Console.WriteLine($"sent SIGTERM to {key.Pid}");
      else {
        Console.Error.WriteLine($"procman: {key.Pid}: {result.Detail ?? result.Outcome.ToString()}");
        ++failed;
      }
    }

    return failed == 0 ? _ExitOk : _ExitError;
  }

  #endregion

  private static string Owner(in ProcessRecord process)
    => process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?");



  /// <summary>
  /// Finds --settings before the rest of the command line is parsed, because the rest of the command
  /// line is parsed over what that file says.
  /// </summary>
  private static string? SettingsPathFrom(string[] args) {
    for (var i = 0; i < args.Length; ++i) {
      if (args[i].StartsWith("--settings=", StringComparison.Ordinal))
        return args[i]["--settings=".Length..];

      if (args[i] == "--settings" && i + 1 < args.Length)
        return args[i + 1];
    }

    return null;
  }

  private static bool SaveSettings(CommandLineOptions options, Settings.UserSettings loaded, string? path) {
    // Everything the file already held is kept; only what this run could actually have changed is
    // overwritten. Saving must not quietly drop a column set somebody wrote by hand.
    var updated = loaded with {
      IntervalSeconds = options.Interval.TotalSeconds,
      SortField = options.SortColumn,
      SortDescending = options.SortDescending,
      TreeMode = options.TreeMode,
      CpuMode = options.CpuMode,
      BlockCharacters = !options.AsciiOnly,
    };

    if (options.Fields is { Length: > 0 } fields)
      updated = updated with { DesktopColumns = fields, TerminalColumns = fields };

    return Settings.SettingsStore.Save(updated, path ?? options.SettingsPath);
  }

}
