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
      case RunMode.Version:
        Console.WriteLine($"procman {typeof(Program).Assembly.GetName().Version}");
        return _ExitOk;
    }

    var probe = ProbeFactory.Create(options.ProbeRoot);
    if (probe is null) {
      Console.Error.WriteLine($"procman: there is no probe for this platform yet ({Environment.OSVersion.Platform}).");
      Console.Error.WriteLine("Linux and Windows are supported; macOS is PRD §10 M9.");
      return _ExitError;
    }

    using (probe) {
      var actions = ProbeFactory.CreateActions(options.ProbeRoot);
      using var sampler = new Sampler(probe);

      return options.Mode switch {
        RunMode.List => RunList(sampler, options),
        RunMode.Find => RunFind(sampler, probe, options),
        RunMode.Kill => RunKill(sampler, actions, options),
        RunMode.SelfTest => SelfTest.Run(sampler, probe.Description),
        RunMode.Terminal => RunTerminal(sampler, probe, actions, options),
        _ => RunDesktop(sampler, probe, actions, options),
      };
    }
  }

  #region interactive

  private static int RunTerminal(Sampler sampler, ISystemProbe probe, IProcessActions? actions, CommandLineOptions options) {
    if (options.CaptureFramePath is null) {
      using var host = new TerminalHost();
      host.Run(sampler, probe, actions, options.Interval);
      return _ExitOk;
    }

    // Headless: compose two frames (the second is the one with rates in it) and write the text.
    // This is the CI gate for the renderer — see PRD §9.6.
    var ui = new TerminalUi(sampler, probe, actions, 120, 40, ColorDepth.None) { ShowTiming = false };
    ui.View.TreeMode = options.TreeMode;
    ui.View.SortColumn = options.SortColumn;
    ui.View.SortDescending = options.SortDescending;
    ui.Update();
    ui.Update();

    var frame = ui.Screen.Capture();
    File.WriteAllText(options.CaptureFramePath, frame);
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
    var result = DesktopLauncher.TryRun(sampler, probe, actions, options.ShootPath);
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
    };

    view.Rebuild(sampler.Current, sampler.Delta);
    var snapshot = sampler.Current;
    var delta = sampler.Delta;

    if (options.Json) {
      WriteJson(snapshot, delta, view);
      return _ExitOk;
    }

    Console.WriteLine($"{"PID",7} {"USER",-12} {"S",-1} {"CPU%",6} {"PRIVATE",8} {"RSS",8} {"THR",4} NAME");
    foreach (var row in view.Rows) {
      ref readonly var process = ref snapshot.Processes[row.Index];
      var indent = options.TreeMode ? new string(' ', Math.Min(row.Depth * 2, 40)) : string.Empty;
      Console.WriteLine(
        $"{process.Pid,7} {Owner(process),-12} {Humanize.State(process.State)[..1],-1} "
        + $"{Humanize.Percent(delta.CpuPercent(row.Index)),6} {Humanize.Bytes(process.PrivateBytes),8} "
        + $"{Humanize.Bytes(process.WorkingSetBytes),8} {process.ThreadCount,4} {indent}{process.Name}"
      );
    }

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
    var target = ProcessKey.None;
    foreach (var process in snapshot.Processes)
      if (process.Pid == options.TargetPid) {
        target = process.Key;
        break;
      }

    if (target.IsNone) {
      Console.Error.WriteLine($"procman: no process with pid {options.TargetPid}");
      return _ExitNoMatch;
    }

    var targets = new List<ProcessKey>();
    Collect(options.TargetPid);
    if (options.KillTree)
      // Deepest first: ending the parent first can reparent its children to init, and they are then
      // no longer findable as its descendants.
      targets.Reverse();
    else
      targets.RemoveRange(1, targets.Count - 1);

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

    void Collect(int pid) {
      var processes = snapshot.Processes;
      for (var i = 0; i < processes.Length; ++i)
        if (processes[i].Pid == pid) {
          targets.Add(processes[i].Key);
          break;
        }

      if (!options.KillTree)
        return;

      for (var i = 0; i < processes.Length; ++i)
        if (processes[i].ParentPid == pid && processes[i].Pid != pid)
          Collect(processes[i].Pid);
    }
  }

  #endregion

  private static string Owner(in ProcessRecord process)
    => process.UserName ?? (process.UserId >= 0 ? process.UserId.ToString(CultureInfo.InvariantCulture) : "?");

  private static void WriteJson(SystemSnapshot snapshot, SnapshotDelta delta, ProcessView view) {
    // Written by hand rather than through a serializer: the output shape is a documented contract
    // (PRD §11), and System.Text.Json's reflection-free path would need a source-generated context
    // for a shape this small.
    var builder = new StringBuilder(64 * 1024);
    builder.Append("{\"processes\":[");
    var first = true;
    foreach (var row in view.Rows) {
      ref readonly var process = ref snapshot.Processes[row.Index];
      if (!first)
        builder.Append(',');

      first = false;
      builder.Append("{\"pid\":").Append(process.Pid)
        .Append(",\"ppid\":").Append(process.ParentPid)
        .Append(",\"name\":").Append(JsonString(process.Name))
        .Append(",\"user\":").Append(JsonString(process.UserName))
        .Append(",\"state\":").Append(JsonString(Humanize.State(process.State)))
        .Append(",\"cpuPercent\":").Append(JsonNumber(delta.CpuPercent(row.Index)))
        .Append(",\"privateBytes\":").Append(JsonNumber(process.PrivateBytes))
        .Append(",\"workingSetBytes\":").Append(JsonNumber(process.WorkingSetBytes))
        .Append(",\"threads\":").Append(process.ThreadCount)
        .Append(",\"commandLine\":").Append(JsonString(process.CommandLine))
        .Append('}');
    }

    builder.Append("]}");
    Console.WriteLine(builder.ToString());
  }

  // A value that is not there is null, never 0: the whole point of §3.4 is that a consumer can tell
  // "no I/O happened" from "you were not allowed to look".
  private static string JsonNumber(Counter counter)
    => counter.HasValue ? counter.Value.ToString(CultureInfo.InvariantCulture) : "null";

  private static string JsonNumber(Rate rate)
    => rate.HasValue ? rate.Value.ToString("0.###", CultureInfo.InvariantCulture) : "null";

  private static string JsonString(string? value) {
    if (value is null)
      return "null";

    var builder = new StringBuilder(value.Length + 2);
    builder.Append('"');
    foreach (var c in value)
      switch (c) {
        case '"': builder.Append("\\\""); break;
        case '\\': builder.Append("\\\\"); break;
        case '\n': builder.Append("\\n"); break;
        case '\r': builder.Append("\\r"); break;
        case '\t': builder.Append("\\t"); break;
        default:
          if (char.IsControl(c))
            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
          else
            builder.Append(c);

          break;
      }

    return builder.Append('"').ToString();
  }

}
