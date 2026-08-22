using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--process</c>: one process in as much detail as the platform will give (PRD §59, §102).
/// </summary>
/// <remarks>
/// <para>
/// The five pages the window and the terminal both have — the summary, the threads, the mapped
/// modules, the open descriptors and the sockets — reachable from a script and over a connection with
/// no terminal to draw into. Until this existed they were the largest thing §102 forbids: visible in a
/// front-end and obtainable nowhere else.
/// </para>
/// <para>
/// The rows come from <see cref="ProcessDetailTables"/>, which is where the terminal's detail view
/// reads them too, so the two cannot show different columns (PRD §58).
/// </para>
/// <para>
/// Text only, and deliberately. Every cell here has already been through <see cref="Humanize"/>, and
/// §76 requires that a machine format carry the raw measurement rather than the rounded string a
/// screen shows — a CSV of <c>1.5G</c> cannot be summed. Emitting these strings under
/// <c>--format csv</c> would be a promise this page cannot keep, so it is not offered.
/// </para>
/// </remarks>
internal static class ProcessReport {

  public static int Run(Sampler sampler, ISystemProbe probe, int pid, ProcessDetailPage page) {
    ArgumentNullException.ThrowIfNull(sampler);
    ArgumentNullException.ThrowIfNull(probe);

    // Two samples, an interval apart, would buy this page nothing: every figure on it is a counter or
    // a string, and none of them is a rate. One sample is what pairs the pid with the start time the
    // probe re-checks before it reads anything (PRD §8.2).
    sampler.Sample();

    var found = false;
    var subject = default(ProcessRecord);
    foreach (var process in sampler.Current.Processes)
      if (process.Key.Pid == pid) {
        subject = process;
        found = true;
      }

    if (!found) {
      Console.Error.WriteLine($"procman: there is no process {pid.ToString(CultureInfo.InvariantCulture)}.");
      return 1;
    }

    // The rules file, so a note somebody wrote about this program shows here as well as in the window
    // — a rule reachable from only one front-end is the drift §58 forbids (PRD §66).
    var table = ProcessDetailTables.Build(page, probe, subject.Key, in subject, SettingsStore.LoadRules());
    if (table.Rows.Count == 0) {
      // Nothing on stdout, so a redirected run leaves an empty file rather than a heading over
      // nothing. An empty page and a page this user may not read look identical from here, and only
      // one of the two is worth acting on — so both are said rather than one guessed at (PRD §5.3).
      Console.Error.WriteLine(
        $"procman: no {table.Title.ToLowerInvariant()} came back for {subject.Name} "
        + $"({pid.ToString(CultureInfo.InvariantCulture)}) — it has none, or they belong to another "
        + "user and may not be read."
      );

      return 2;
    }

    Console.WriteLine(
      $"{subject.Name} ({pid.ToString(CultureInfo.InvariantCulture)}) — {table.Title.ToLowerInvariant()}"
    );

    Console.WriteLine();
    Write(table);
    return 0;
  }

  /// <summary>
  /// Writes the table, every column as wide as the widest thing actually in it.
  /// </summary>
  /// <remarks>
  /// Not the widths the table carries: those are for a cell grid that has to know before it draws, and
  /// a report that can measure first has no reason to pad a SONAME column to twenty-two characters on
  /// a process whose longest is nine. The overview is a two-column list and gets the same treatment,
  /// which is what keeps its values in one straight edge.
  /// </remarks>
  private static void Write(DetailTable table) {
    var columns = table.Headers.Count;
    var widths = new int[columns];
    for (var i = 0; i < columns; ++i)
      widths[i] = table.Headers[i].Length;

    foreach (var row in table.Rows)
      for (var i = 0; i < columns && i < row.Length; ++i)
        widths[i] = Math.Max(widths[i], row[i].Length);

    Console.WriteLine(Line(table.Headers, widths, columns));
    foreach (var row in table.Rows)
      Console.WriteLine(Line(row, widths, columns));
  }

  private static string Line(IReadOnlyList<string> cells, int[] widths, int columns) {
    var text = new System.Text.StringBuilder();
    for (var i = 0; i < columns; ++i) {
      if (i > 0)
        text.Append("  ");

      var cell = i < cells.Count ? cells[i] : string.Empty;
      // The last column is never padded: a trailing run of spaces on every line of a path column is
      // invisible on a screen and is not invisible to whatever the output is piped into.
      text.Append(i == columns - 1 ? cell : cell.PadRight(widths[i]));
    }

    return text.ToString().TrimEnd();
  }

}
