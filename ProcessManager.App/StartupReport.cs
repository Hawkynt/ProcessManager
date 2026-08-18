using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--startup</c>: what will run when this user logs in (PRD §42).
/// </summary>
internal static class StartupReport {

  public static int Run(ISystemProbe probe, CommandLineOptions options) {
    var entries = probe.GetStartupEntries();
    if (entries.Count == 0) {
      // Empty means "nothing here" on Linux and "not read yet" on Windows, and the reader deserves
      // to be told which (PRD §7).
      Console.Error.WriteLine(
        OperatingSystem.IsWindows()
          ? "procman: startup entries are not read on Windows yet — the Run keys, the Startup folders\n"
            + "         and the task scheduler are all still to do."
          : "procman: nothing is configured to start at login."
      );

      return 0;
    }

    if (options.Format is ExportFormat.Json or ExportFormat.JsonLines) {
      WriteJson(entries);
      return 0;
    }

    var width = 4;
    foreach (var entry in entries)
      width = Math.Max(width, entry.Name.Length);

    foreach (var entry in entries) {
      // The state first, because scanning a column of "off" is how somebody finds what is not
      // running; the reason after the command, because it explains the state.
      var state = entry.Enabled ? "on " : "off";
      var scope = entry.Scope == StartupScope.User ? "user  " : "system";
      var reason = entry.DisabledReason is { } why ? $"   ({why})" : string.Empty;
      Console.WriteLine($"{state}  {scope}  {entry.Name.PadRight(width)}  {entry.Command}{reason}");
    }

    return 0;
  }

  private static void WriteJson(IReadOnlyList<StartupEntry> entries) {
    var builder = new System.Text.StringBuilder();
    builder.Append("{\"startup\":[");
    for (var i = 0; i < entries.Count; ++i) {
      if (i > 0)
        builder.Append(',');

      var entry = entries[i];
      builder.Append("{\"name\":").Append(Json(entry.Name))
        .Append(",\"command\":").Append(Json(entry.Command))
        .Append(",\"path\":").Append(Json(entry.Path))
        .Append(",\"enabled\":").Append(entry.Enabled ? "true" : "false")
        .Append(",\"reason\":").Append(entry.DisabledReason is null ? "null" : Json(entry.DisabledReason))
        .Append(",\"scope\":").Append(Json(entry.Scope.ToString().ToLowerInvariant()))
        .Append('}');
    }

    builder.Append("]}");
    Console.WriteLine(builder.ToString());
  }

  private static string Json(string value) {
    var builder = new System.Text.StringBuilder(value.Length + 2).Append('"');
    foreach (var character in value)
      switch (character) {
        case '"': builder.Append("\\\""); break;
        case '\\': builder.Append("\\\\"); break;
        case '\n': builder.Append("\\n"); break;
        default:
          if (character < ' ')
            builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
          else
            builder.Append(character);

          break;
      }

    return builder.Append('"').ToString();
  }

}
