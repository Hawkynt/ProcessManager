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
  /// <summary>
  /// The user id this program is running as, or -1 where it cannot be read.
  /// </summary>
  /// <remarks>
  /// Minus one classifies nothing as "yours", which is the right way to be wrong: claiming a process
  /// belongs to whoever happens to be looking is the one mistake here that would matter.
  /// </remarks>
  private static int CurrentUserId() {
    if (!OperatingSystem.IsLinux())
      return -1;

    try {
      foreach (var line in File.ReadLines("/proc/self/status")) {
        if (!line.StartsWith("Uid:", StringComparison.Ordinal))
          continue;

        var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 0 && int.TryParse(fields[0].Trim(), out var uid) ? uid : -1;
      }
    } catch (IOException) {
    } catch (UnauthorizedAccessException) {
    }

    return -1;
  }

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
      // Before the probe is built, because none of these ever look at a process: asking where the
      // settings file is must not need a /proc tree, and it must work on a machine where the probe
      // cannot start at all (PRD §81).
      case RunMode.Settings:
        return SettingsCommand.Run(options.SettingsAction, options.SettingsTransferPath, settingsPath ?? options.SettingsPath);
    }

    var probe = ProbeFactory.Create(
      options.ProbeRoot,
      options.UseHelper,
      options.WantsSecurityContext,
      options.WantsProportionalSetSize,
      options.WantsSupplementaryGroups,
      options.WantsGpuUsage,
      options.WantsHandleCount,
      options.WantsCpuAffinity,
      options.WantsCpuThrottling,
      options.WantsDescriptorKinds,
      options.WantsImageHashes,
      options.WantsSocketCounts,
      options.WantsPackageIdentity,
      options.WantsPackageVerification,
      options.WantsApplicationName,
      options.WantsRuntime,
      options.WantsImageCreationTime,
      options.WantsSecurityStatus,
      options.WantsIoPriority,
      options.WantsWindowsMitigations,
      options.WantsObjectCounts,
      options.WantsGuiObjectCounts,
      options.WantsImageVersions,
      options.WantsImageSignatures,
      options.WantsPowerThrottling
    );
    if (probe is null) {
      Console.Error.WriteLine($"procman: there is no probe for this platform yet ({Environment.OSVersion.Platform}).");
      Console.Error.WriteLine("Linux and Windows are supported; macOS is PRD §10 M9.");
      return _ExitError;
    }

    // Who is running this, once, for the classifier behind the Kind column. Every front-end reaches
    // that through the shared accessor, which takes a process and no caller identity — so it is set
    // here, where all three of them pass through, rather than three times over.
    Query.ProcessCategories.CurrentUserId = CurrentUserId();

    using (probe)
    using (ProbeFactory.Elevated) {
      var actions = ProbeFactory.CreateActions(options.ProbeRoot);
      using var sampler = new Sampler(probe);

      return options.Mode switch {
        RunMode.List => RunList(sampler, options),
        RunMode.Find => RunFind(sampler, probe, options),
        RunMode.Kill => RunKill(sampler, actions, options),
        RunMode.EndTask => RunEndTask(sampler, actions, options),
        RunMode.Restart => RunRestart(sampler, actions, options),
        RunMode.Scheduling => RunScheduling(sampler, actions, options),
        RunMode.Signal => RunSignal(sampler, actions, options),
        RunMode.ResourceLimit => RunResourceLimit(sampler, actions, options),
        RunMode.OutOfMemory => RunOutOfMemory(sampler, actions, options),
        RunMode.Freezer => RunFreezer(sampler, actions, options),
        RunMode.Host => HostReport.Run(sampler, probe),
        RunMode.Limits => LimitsReport.Run(sampler, probe, options.TargetPid),
        RunMode.Run => LaunchCommand.Run(actions, options),
        RunMode.Startup => StartupReport.Run(probe, options),
        RunMode.Users => UsersReport.Run(sampler, probe),
        RunMode.Services => ServicesReport.Run(probe, options),
        RunMode.ServiceControl => ServiceControlCommand.Run(options.ServiceVerb, options.ServiceUnit),
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
      using var host = new TerminalHost { UseMouse = options.UseMouse };
      host.Run(sampler, probe, actions, options.Interval, new() {
        SortColumn = options.SortColumn,
        SortDescending = options.SortDescending,
        Grouping = options.Grouping,
        Columns = options.TerminalColumns,
        PinnedColumns = options.PinnedTerminalColumns,
        ManualRefresh = options.ManualRefresh,
        // Only when somebody said so: otherwise the terminal decides from the locale, which is the
        // one thing a capture may not do and a person watching wants. Saying so includes saying so
        // in the settings file, which is what tui.graphs is for — before it existed, a preference
        // for braille had to be retyped every run.
        Graphs = options.AsciiOnly ? GraphStyle.Ascii
          : options.GraphStyleWasStated || options.GraphStyle != GraphStyle.Blocks ? options.GraphStyle
          : null,
      }, ProbeFactory.CreateServiceControl());

      return _ExitOk;
    }

    // Headless: compose two frames (the second is the one with rates in it) and write the text.
    // This is the CI gate for the renderer — see PRD §9.6.
    // Pinned, both of them: a captured frame is compared byte for byte, so neither the colour depth
    // nor the block characters may come from whatever the capturing machine's environment happens to
    // say. --ascii overrides for a picture of the fallback.
    var ui = new TerminalUi(sampler, probe, actions, options.CaptureWidth, options.CaptureHeight, ColorDepth.None) {
      ShowTiming = false,
      GraphStyle = options.AsciiOnly ? GraphStyle.Ascii : options.GraphStyle,
    };
    ui.View.Grouping = options.Grouping;
    ui.View.SortColumn = options.SortColumn;
    ui.View.SortDescending = options.SortDescending;
    // The same columns the interactive terminal would have opened with. Without this a capture could
    // only ever photograph the set the width picked, which makes --columns untestable by the one
    // test that looks at a whole frame (PRD §9.6).
    if (options.TerminalColumns is { Length: > 0 } columns)
      ui.Columns.Apply(columns);

    // After the columns, because Apply resets the pinned run to the one column a fresh set opens
    // with, and a capture has to photograph the layout the settings file actually describes.
    ui.Columns.SetFrozen(options.PinnedTerminalColumns);

    for (var i = 0; i < options.CaptureSamples; ++i) {
      ui.Update();
      // Only between samples, and only when more than the minimum were asked for: a golden frame is
      // taken with two and must not pay a second of wall-clock for it.
      if (options.CaptureSamples > 2 && i < options.CaptureSamples - 1)
        Thread.Sleep(options.Interval);
    }

    foreach (var key in ParseCaptureKeys(options.CaptureKeys))
      if (ui.HandleKey(key))
        ui.Refresh();

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

  /// <summary>
  /// The keys of <c>--capture-keys</c>, as the UI receives them.
  /// </summary>
  /// <remarks>
  /// Four of them have no printable form, so they are written the way a C programmer writes them.
  /// Everything else is the character itself: a capture of the action menu is <c>--capture-keys x</c>.
  /// </remarks>
  private static IEnumerable<ConsoleKeyInfo> ParseCaptureKeys(string? keys) {
    if (string.IsNullOrEmpty(keys))
      yield break;

    for (var i = 0; i < keys.Length; ++i) {
      if (keys[i] == '\\' && i + 1 < keys.Length) {
        var named = keys[++i] switch {
          't' => new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false),
          'n' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
          'e' => new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false),
          's' => new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false),
          var other => new ConsoleKeyInfo(other, default, false, false, false),
        };

        yield return named;
        continue;
      }

      yield return new(keys[i], default, false, false, false);
    }
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
      Grouping = options.Grouping,
      CpuMode = options.CpuMode,
      DesktopColumns = options.Fields is { Length: > 0 } fields ? fields : settings.DesktopColumns,
    };

    var result = DesktopLauncher.TryRun(
      sampler,
      probe,
      actions,
      ProbeFactory.CreateServiceControl(),
      ProbeFactory.CreateStartupControl(),
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

    // A third when the change in a CPU share was asked for, because that is the difference between
    // two intervals and two samples make only one of them. Waiting another interval for a column
    // nobody named would double the cost of every --list there is (PRD §5.4).
    if (options.WantsCpuPercentDelta) {
      Thread.Sleep(options.Interval);
      sampler.Sample();
    }

    var view = new ProcessView {
      SortColumn = options.SortColumn,
      SortDescending = options.SortDescending,
      Grouping = options.Grouping,
      TextFilter = options.Filter,
    };

    view.Rebuild(sampler.Current, sampler.Delta);
    var snapshot = sampler.Current;
    var delta = sampler.Delta;

    // One writer for every format, over any set of registry fields (PRD §61). --json is kept as a
    // spelling of --format=json because it is what every script already passes.
    var format = options.Json ? ExportFormat.Json : options.Format;
    Exporter.Write(Console.Out, format, snapshot, delta, view, options.Fields, options.TreeMode);

    // A filter that excluded everything is the "nothing matched" the exit codes promise, and it was
    // the one place that promised it and did not deliver: a script asking whether anything is over a
    // memory threshold got the same nought back whether the answer was no or yes. Only when a filter
    // was given — a plain --list on a machine with no processes is a different and impossible thing,
    // and reporting no-match for it would be answering a question nobody asked.
    return options.Filter is { Length: > 0 } && view.RowCount == 0 ? _ExitNoMatch : _ExitOk;
  }

  /// <summary>
  /// <c>--find</c>: which process is using this (PRD §33).
  /// </summary>
  private static int RunFind(Sampler sampler, ISystemProbe probe, CommandLineOptions options) {
    sampler.Sample();

    var pattern = options.Pattern ?? string.Empty;
    var matches = ResourceSearch.Find(probe, sampler.Current, pattern);
    foreach (var match in matches)
      // The access mode sits between the kind and the thing itself: for an open file it is what the
      // holder may do with it, which is the next question after "who has it" every time, and for a
      // mapping it is the permission characters. A dash where the question does not arise (PRD §33).
      Console.WriteLine(
        $"{match.Pid,7} {match.UserName ?? "?",-12} {match.ProcessName,-24} {Describe(match.Kind)}"
        + $"  {match.Access ?? "—",-4}  {match.Detail}"
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

  /// <summary>
  /// <c>--end-task</c>: asks the program to close rather than telling it to (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The detail is printed on success as well as on failure, because here it carries the whole
  /// answer: "its window was asked" and "it has no window, so SIGTERM was sent" are different things
  /// to have happened, and only one of them can still be refused by the program itself.
  /// </remarks>
  private static int RunEndTask(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.EndTask(target);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine($"asked {target.Pid} to end: {result.Detail ?? "done"}");
    return _ExitOk;
  }

  /// <summary>
  /// <c>--restart</c>: the same program, the same arguments, the same directory, a new process.
  /// </summary>
  private static int RunRestart(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.Restart(target);
    if (!result.Outcome.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Outcome.Detail ?? result.Outcome.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine($"restarted {target.Pid} as {result.Pid}");
    return _ExitOk;
  }

  /// <summary>
  /// <c>--scheduling</c>: which class the kernel runs the process under (PRD §25.2).
  /// </summary>
  private static int RunScheduling(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.SetSchedulingClass(target, options.SchedulingClass, options.SchedulingPriority);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine($"{target.Pid} now runs under {Humanize.SchedulingPolicy(options.SchedulingClass)}");
    return _ExitOk;
  }

  /// <summary>
  /// <c>--signal</c>: any signal the kernel has, not only the three with menu items (PRD §25.1).
  /// </summary>
  /// <remarks>
  /// The line printed on success names what was sent rather than saying "done", because the default
  /// action of most signals is to end the process and somebody who typed <c>USR1</c> to poke a
  /// program should see which signal actually went.
  /// </remarks>
  private static int RunSignal(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.SendSignal(target, options.Signal);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine($"sent {Signals.Describe(options.Signal)} to {target.Pid}");
    return _ExitOk;
  }

  /// <summary>
  /// <c>--rlimit</c>: one of the kernel's per-process ceilings (PRD §25.2).
  /// </summary>
  private static int RunResourceLimit(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.SetResourceLimit(target, options.LimitKind, options.LimitSoft, options.LimitHard);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    var unit = ResourceLimits.Of(options.LimitKind)?.Unit ?? ResourceLimitUnit.Count;
    Console.WriteLine(
      $"{target.Pid}: {ResourceLimits.Name(options.LimitKind)} is now "
      + $"{ResourceLimits.Format(unit, options.LimitSoft)} of {ResourceLimits.Format(unit, options.LimitHard)}"
    );

    return _ExitOk;
  }

  /// <summary>
  /// <c>--oom</c>: which process the kernel picks when the machine runs out of memory (PRD §25.5).
  /// </summary>
  /// <remarks>
  /// The line says what it does <em>not</em> do, because that is the part people get wrong: it
  /// reserves nothing and limits nothing, it only moves this process up or down a queue that
  /// somebody else is also in.
  /// </remarks>
  private static int RunOutOfMemory(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.SetOomScoreAdjustment(target, options.OomAdjustment);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine(
      $"{target.Pid}: out-of-memory adjustment is now {options.OomAdjustment.ToString(CultureInfo.InvariantCulture)}. "
      + "This changes which process is killed when memory runs out, not how much it may use."
    );

    return _ExitOk;
  }

  /// <summary>
  /// <c>--freeze</c> and <c>--thaw</c>: the whole cgroup, which is what stopping a unit means on
  /// Linux (PRD §25.1, §38).
  /// </summary>
  private static int RunFreezer(Sampler sampler, IProcessActions? actions, CommandLineOptions options) {
    if (!TryResolve(sampler, actions, options.TargetPid, out var target, out var exit))
      return exit;

    var result = actions!.FreezeCgroup(target, options.Freeze);
    if (!result.Succeeded) {
      Console.Error.WriteLine($"procman: {target.Pid}: {result.Detail ?? result.Outcome.ToString()}");
      return _ExitError;
    }

    Console.WriteLine(result.Detail ?? (options.Freeze ? "frozen" : "thawed"));
    return _ExitOk;
  }

  /// <summary>
  /// Turns a pid from a command line into the identity pair every action needs (PRD §8.2).
  /// </summary>
  /// <remarks>
  /// A pid is what somebody can type, and it is not an identity. Looking it up in a fresh snapshot is
  /// what pairs it with the start time the action will re-check, so a number recycled between the
  /// sample and the syscall is refused there rather than acted on here.
  /// </remarks>
  private static bool TryResolve(Sampler sampler, IProcessActions? actions, int pid, out ProcessKey key, out int exit) {
    key = ProcessKey.None;
    exit = _ExitOk;
    if (actions is null) {
      Console.Error.WriteLine("procman: this platform has no actions yet.");
      exit = _ExitError;
      return false;
    }

    sampler.Sample();
    key = ProcessTree.Find(sampler.Current, pid);
    if (!key.IsNone)
      return true;

    Console.Error.WriteLine($"procman: no process with pid {pid}");
    exit = _ExitNoMatch;
    return false;
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
      Grouping = options.Grouping,
      CpuMode = options.CpuMode,
      BlockCharacters = !options.AsciiOnly,
      TerminalMouse = options.UseMouse,
    };

    if (options.Fields is { Length: > 0 } fields)
      updated = updated with { DesktopColumns = fields, TerminalColumns = fields };

    return Settings.SettingsStore.Save(updated, path ?? options.SettingsPath);
  }

}
