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
    var options = CommandLineOptions.Parse(args);
    if (options.Error is { } error) {
      Console.Error.WriteLine($"procman: {error}");
      Console.Error.WriteLine("Try 'procman --help'.");
      return _ExitError;
    }

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

    var probe = ProbeFactory.Create(options.ProbeRoot, options.UseHelper);
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
        RunMode.SelfTest => SelfTest.Run(sampler, probe.Description, probe),
        RunMode.HelperCheck => HelperCheck.Run(),
        RunMode.Terminal => RunTerminal(sampler, probe, actions, options),
        _ => RunDesktop(sampler, probe, actions, options),
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

  private static int RunDesktop(Sampler sampler, ISystemProbe probe, IProcessActions? actions, CommandLineOptions options) {
    var result = DesktopLauncher.TryRun(
      sampler,
      probe,
      actions,
      options.ShootPath,
      options.ShootHoldSeconds,
      options.FlatRequested
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

  private static int RunFind(Sampler sampler, ISystemProbe probe, CommandLineOptions options) {
    sampler.Sample();
    var pattern = options.Pattern ?? string.Empty;
    var snapshot = sampler.Current;
    var matches = 0;

    foreach (var process in snapshot.Processes) {
      var reasons = new List<string>();
      if (process.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        reasons.Add("name");
      if (process.CommandLine?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true)
        reasons.Add("command line");

      // The expensive half — every open file and every mapping of every process — runs only when
      // the cheap half has not already answered, and reports as it goes rather than at the end
      // (PRD §6.5).
      if (reasons.Count == 0) {
        foreach (var handle in probe.GetHandles(process.Key))
          if (handle.Name?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true) {
            reasons.Add($"open file {handle.Name}");
            break;
          }

        if (reasons.Count == 0)
          foreach (var module in probe.GetModules(process.Key))
            if (module.Path.Contains(pattern, StringComparison.OrdinalIgnoreCase)) {
              reasons.Add($"mapped {module.Path}");
              break;
            }
      }

      if (reasons.Count == 0)
        continue;

      ++matches;
      Console.WriteLine($"{process.Pid,7} {Owner(process),-12} {process.Name,-24} {string.Join(", ", reasons)}");
    }

    if (matches == 0)
      Console.Error.WriteLine($"procman: nothing matched '{pattern}'");

    return matches > 0 ? _ExitOk : _ExitNoMatch;
  }

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


}
