using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--users</c>: who is logged in and what their processes are costing (PRD §43).
/// </summary>
/// <remarks>
/// Two questions in one table, which is what makes the tab worth having: the sessions come from the
/// login records, and the totals come from the process list. A user with a session and no processes
/// is a stale login; processes with no session belong to services and are shown separately.
/// </remarks>
internal static class UsersReport {

  public static int Run(Sampler sampler, ISystemProbe probe) {
    var sessions = probe.GetSessions();

    // Two samples, an interval apart: the CPU column is a rate and would otherwise be all dashes.
    sampler.Sample();
    Thread.Sleep(250);
    sampler.Sample();

    var totals = Totals(sampler.Current, sampler.Delta);
    var width = 4;
    foreach (var session in sessions)
      if (session.Kind == SessionKind.User)
        width = Math.Max(width, session.UserName.Length);

    Console.WriteLine(
      $"{"USER".PadRight(width)}  {"TTY",-10} {"FROM",-16} {"LOGIN",-16} {"PROCS",5} {"CPU%",6} {"MEMORY",9}"
    );

    var listed = 0;
    foreach (var session in sessions) {
      if (session.Kind != SessionKind.User)
        continue;

      ++listed;
      var total = totals.TryGetValue(session.UserName, out var found) ? found : default;
      Console.WriteLine(
        $"{session.UserName.PadRight(width)}  {session.Terminal,-10} "
        + $"{session.RemoteHost ?? "local",-16} {Login(session.LoginTimeUtcTicks),-16} "
        + $"{total.Processes,5} {total.CpuPercent,6:0.0} {Humanize.Bytes(Counter.Of(total.PrivateBytes)),9}"
      );
    }

    if (listed == 0)
      Console.Error.WriteLine(
        OperatingSystem.IsWindows()
          ? "procman: sessions are not read on Windows yet — WTSEnumerateSessions is still to do."
          : "procman: nobody is logged in."
      );

    // The boot record is in the same file and answers a question people ask of this tab.
    foreach (var session in sessions)
      if (session.Kind == SessionKind.Boot && session.LoginTimeUtcTicks > 0) {
        Console.WriteLine();
        Console.WriteLine($"Booted {Login(session.LoginTimeUtcTicks)}");
        break;
      }

    return 0;
  }

  private readonly record struct Total(int Processes, double CpuPercent, ulong PrivateBytes);

  /// <summary>
  /// Sums each user's processes.
  /// </summary>
  /// <remarks>
  /// By name rather than by id, because that is what the session records carry — and a process whose
  /// owner could not be resolved is counted under no user at all rather than under a made-up one.
  /// </remarks>
  private static Dictionary<string, Total> Totals(SystemSnapshot snapshot, SnapshotDelta delta) {
    var totals = new Dictionary<string, Total>(StringComparer.Ordinal);
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      if (processes[i].UserName is not { Length: > 0 } user)
        continue;

      totals.TryGetValue(user, out var running);
      var cpu = delta.CpuPercent(i);
      totals[user] = new(
        running.Processes + 1,
        running.CpuPercent + (cpu.HasValue ? cpu.Value : 0),
        running.PrivateBytes + processes[i].PrivateBytes.GetValueOrDefault()
      );
    }

    return totals;
  }

  private static string Login(long ticks) => ticks > 0
    ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
    : "—";

}
