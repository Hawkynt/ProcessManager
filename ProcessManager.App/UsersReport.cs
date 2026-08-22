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
/// <para>
/// Two questions in one table, which is what makes the tab worth having: the sessions come from the
/// login records, and the totals come from the process list. A user with a session and no processes
/// is a stale login; processes with no session belong to services and are shown separately.
/// </para>
/// <para>
/// The expansion is what turns a row of totals into something to act on. A hundred and eighty
/// megabytes against a name is a number; the eleven processes it is made of is an answer. It is
/// printed once per account rather than once per session, because two logins of the same person
/// share every process between them and printing the list twice would double every figure a reader
/// added up by eye.
/// </para>
/// </remarks>
internal static class UsersReport {

  /// <param name="expand">
  /// Whether each account's rows are opened to the processes behind them — <c>--users --tree</c>,
  /// which is the same gesture as opening a row in the window. Off by default because an account
  /// with four hundred processes is four hundred lines between one login and the next (PRD §5.2).
  /// </param>
  public static int Run(Sampler sampler, ISystemProbe probe, bool expand = false) {
    var sessions = probe.GetSessions();

    // Two samples, an interval apart: the CPU, disk and GPU columns are rates and would otherwise be
    // all placeholders.
    sampler.Sample();
    Thread.Sleep(250);
    sampler.Sample();

    var totals = SessionTotals.Of(sampler.Current, sampler.Delta);
    var now = DateTime.UtcNow;
    var width = 4;
    foreach (var session in sessions)
      if (session.Kind == SessionKind.User)
        width = Math.Max(width, session.UserName.Length);

    Console.WriteLine(
      $"{"USER".PadRight(width)}  {"TTY",-10} {"FROM",-16} {"LOGIN",-16} {"ID",-4} {"TYPE",-10} "
      + $"{"STATE",-6} {"IDLE",-6} {"PROCS",5} {"CPU%",6} {"MEMORY",9} {"DISK",10} {"GPU%",6}"
    );

    // Ordered so that every login of one account is together and the expansion follows the last of
    // them. Stable within an account, so two terminals stay in the order the file holds them.
    var people = new List<SessionRecord>();
    foreach (var session in sessions)
      if (session.Kind == SessionKind.User)
        people.Add(session);

    people.Sort(static (left, right) => string.CompareOrdinal(left.UserName, right.UserName));

    var listed = 0;
    for (var i = 0; i < people.Count; ++i) {
      var session = people[i];
      ++listed;
      var total = totals.TryGetValue(session.UserName, out var found) ? found : UserTotals.None;
      Console.WriteLine(
        $"{session.UserName.PadRight(width)}  {session.Terminal,-10} "
        + $"{session.RemoteHost ?? "local",-16} {Login(session.LoginTimeUtcTicks),-16} "
        + $"{session.SessionId ?? "—",-4} {SessionFacts.Describe(session.Type),-10} "
        + $"{SessionFacts.Describe(session.State),-6} "
        + $"{SessionFacts.DescribeIdle(SessionFacts.IdleFor(session.LastInputUtcTicks, now)),-6} "
        + $"{total.Processes,5} {Percent(total.CpuPercent),6} {Humanize.Bytes(total.PrivateBytes),9} "
        + $"{Throughput(total.DiskBytesPerSecond),10} {Percent(total.GpuPercent),6}"
      );

      if (session.FullName is { Length: > 0 } full)
        Console.WriteLine($"{new string(' ', width)}  {full}");

      // The last row this account has: everything below belongs to it and to nobody else.
      if (i + 1 < people.Count && string.Equals(people[i + 1].UserName, session.UserName, StringComparison.Ordinal))
        continue;

      if (expand)
        Expand(sampler, session.UserName);
    }

    if (listed == 0)
      Console.Error.WriteLine(
        OperatingSystem.IsWindows()
          ? "procman: sessions are not read on Windows yet — WTSEnumerateSessions is still to do."
          : "procman: nobody is logged in."
      );

    Services(sampler, people);

    // The boot record is in the same file and answers a question people ask of this tab.
    foreach (var session in sessions)
      if (session.Kind == SessionKind.Boot && session.LoginTimeUtcTicks > 0) {
        Console.WriteLine();
        Console.WriteLine($"Booted {Login(session.LoginTimeUtcTicks)}");
        break;
      }

    Console.WriteLine();
    Console.WriteLine("Network bytes are not summed here: there is no per-process byte counter with a portable");
    Console.WriteLine("source, so the total would be an invented number rather than a small one (PRD §18).");
    return 0;
  }

  /// <summary>
  /// The processes behind one account's totals (PRD §43).
  /// </summary>
  /// <remarks>
  /// Busiest first, because a row that is costing something is the reason somebody opened this. All
  /// of them rather than a head: this is a report and its whole job is to be complete, and an account
  /// with four hundred processes has four hundred processes whatever a listing chooses to show.
  /// </remarks>
  private static void Expand(Sampler sampler, string user) {
    var snapshot = sampler.Current;
    var delta = sampler.Delta;
    var mine = new List<int>();
    for (var i = 0; i < snapshot.Processes.Length; ++i)
      if (string.Equals(snapshot.Processes[i].UserName, user, StringComparison.Ordinal))
        mine.Add(i);

    if (mine.Count == 0) {
      Console.WriteLine("    no processes — this login is stale, or everything it started has ended");
      return;
    }

    mine.Sort((left, right) => delta.CpuPercent(right).GetValueOrDefault().CompareTo(delta.CpuPercent(left).GetValueOrDefault()));
    foreach (var index in mine)
      Console.WriteLine(
        $"    {snapshot.Processes[index].Pid,7} {Percent(delta.CpuPercent(index)),6} "
        + $"{Humanize.Bytes(snapshot.Processes[index].PrivateBytes),9}  {snapshot.Processes[index].Name}"
      );
  }

  /// <summary>
  /// The accounts with processes and no login (PRD §43).
  /// </summary>
  /// <remarks>
  /// The other half of the sentence this page has always made and never shown: a user with a session
  /// and no processes is a stale login, and processes with no session belong to services. The second
  /// half was in the prose and nowhere in the output, so a reader could see the first and had to take
  /// the second on trust.
  /// </remarks>
  private static void Services(Sampler sampler, List<SessionRecord> people) {
    var loggedIn = new HashSet<string>(StringComparer.Ordinal);
    foreach (var session in people)
      loggedIn.Add(session.UserName);

    var totals = SessionTotals.Of(sampler.Current, sampler.Delta);
    var accounts = new List<string>();
    foreach (var (user, _) in totals)
      if (!loggedIn.Contains(user))
        accounts.Add(user);

    if (accounts.Count == 0)
      return;

    accounts.Sort(StringComparer.Ordinal);
    var width = 4;
    foreach (var user in accounts)
      width = Math.Max(width, user.Length);

    Console.WriteLine();
    Console.WriteLine("accounts with processes and no login — these belong to services rather than to anybody");
    foreach (var user in accounts) {
      var total = totals[user];
      Console.WriteLine(
        $"  {user.PadRight(width)}  {total.Processes,5} {Percent(total.CpuPercent),6} "
        + $"{Humanize.Bytes(total.PrivateBytes),9} {Throughput(total.DiskBytesPerSecond),10}"
      );
    }
  }

  private static string Percent(Rate rate)
    => rate.HasValue ? rate.Value.ToString("0.0", CultureInfo.InvariantCulture) : Humanize.Placeholder(rate.Reason);

  private static string Throughput(Rate rate)
    => rate.HasValue ? Humanize.Bytes(Counter.Of((ulong)Math.Max(0, rate.Value))) + "/s" : Humanize.Placeholder(rate.Reason);

  private static string Login(long ticks) => ticks > 0
    ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
    : "—";

}
