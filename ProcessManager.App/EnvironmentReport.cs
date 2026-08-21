using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// The environment a process was started with (PRD §31).
/// </summary>
/// <remarks>
/// <para>
/// Written because the window had a page for this and neither the terminal nor the command line had
/// anything: §102 requires that nothing be visible in the window and obtainable nowhere else, and
/// this was the one thing that was.
/// </para>
/// <para>
/// <b>It is the block the kernel laid down at <c>exec</c>, not the environment the process has now.</b>
/// A program that has since called <c>setenv</c> has a variable this cannot see, and one that called
/// <c>unsetenv</c> still shows the old value here. Nothing from outside can tell a stale block from a
/// current one, so the heading says so rather than the reader having to know it (PRD §5.3).
/// </para>
/// </remarks>
internal static class EnvironmentReport {

  public static int Run(Sampler sampler, ISystemProbe probe, int pid, ExportFormat format) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);
    sampler.Sample();

    var key = default(ProcessKey);
    var name = string.Empty;
    foreach (var process in sampler.Current.Processes)
      if (process.Key.Pid == pid) {
        key = process.Key;
        name = process.Name;
      }

    if (key.Pid == 0) {
      Console.Error.WriteLine($"procman: there is no process {pid.ToString(CultureInfo.InvariantCulture)}.");
      return 1;
    }

    var variables = probe.GetEnvironment(key);
    if (variables.Count == 0) {
      // Two different situations with one appearance from here, and neither is an error: a process
      // genuinely started with nothing, and a process belonging to somebody else whose block this
      // program may not read. Which one it is, is not knowable without the very permission that is
      // missing, so both are said rather than one being guessed at.
      Console.Error.WriteLine(
        $"procman: nothing came back for {name} ({pid.ToString(CultureInfo.InvariantCulture)}) — either it was started"
      );
      Console.Error.WriteLine("with an empty environment, or its block belongs to another user and may not be read.");
      return 2;
    }

    switch (format) {
      case ExportFormat.Json:
      case ExportFormat.JsonLines:
        Console.WriteLine("{");
        for (var i = 0; i < variables.Count; ++i)
          Console.WriteLine(
            $"  {Json(variables[i].Key)}: {Json(variables[i].Value)}{(i == variables.Count - 1 ? string.Empty : ",")}"
          );

        Console.WriteLine("}");
        break;

      // Deliberately not quoted or escaped for a shell. This is a report of what a process was
      // given, and dressing it up as something to paste into a terminal would invite somebody to
      // paste it — which would set their own shell's environment from another process's, quietly
      // and with whatever that process happened to be carrying.
      default:
        Console.WriteLine($"{name} ({pid.ToString(CultureInfo.InvariantCulture)}) — {variables.Count} variables, as they were at exec");
        Console.WriteLine();
        foreach (var (variable, value) in variables)
          Console.WriteLine($"{variable}={value}");

        break;
    }

    return 0;
  }

  private static string Json(string text) {
    var builder = new System.Text.StringBuilder(text.Length + 2);
    builder.Append('"');
    foreach (var character in text)
      switch (character) {
        case '"': builder.Append("\\\""); break;
        case '\\': builder.Append("\\\\"); break;
        case '\n': builder.Append("\\n"); break;
        case '\r': builder.Append("\\r"); break;
        case '\t': builder.Append("\\t"); break;
        default:
          // A value can carry anything the parent put in it, control characters included.
          if (char.IsControl(character))
            builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
          else
            builder.Append(character);

          break;
      }

    builder.Append('"');
    return builder.ToString();
  }

}
