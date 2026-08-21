using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--connections</c>: every socket on the machine and who owns it (PRD §40, §59).
/// </summary>
/// <remarks>
/// What <c>ss -tanp</c> and <c>netstat -tulpn</c> are usually run for, with the same information and
/// none of the guessing: a value the kernel does not report is a placeholder rather than a zero, so
/// a column of <c>n/a</c> means "this protocol has no such thing" and a column of <c>—</c> means
/// "this user may not see it" (PRD §72.3).
/// </remarks>
internal static class ConnectionsReport {

  private static ServiceNames ServiceNames()
    => OperatingSystem.IsLinux() ? Platform.Linux.ServiceNameReader.Read() : Query.ServiceNames.Empty;

  public static int Run(Sampler sampler, ISystemProbe probe, CommandLineOptions options) {
    var connections = probe.GetConnections();
    if (connections.Count == 0) {
      Console.Error.WriteLine(
        OperatingSystem.IsWindows()
          ? "procman: no sockets came back from the connection tables."
          : "procman: no sockets were found — /proc/net is empty or unreadable."
      );

      return 0;
    }

    // One sample, only to put a name against each owning pid. The listing itself needs no rates, so
    // it does not pay the second sample --list does.
    sampler.Sample();
    var names = Names(sampler.Current);

    // Port names come from the machine's own file and cost one read; ss and netstat both name ports
    // by default and this does too. Addresses are another matter — resolving one asks somebody else
    // a question — so that happens only when it was asked for (PRD §40).
    var services = options.NumericEndpoints ? null : ServiceNames();
    using var hosts = options.ResolveHostnames ? new HostnameCache { Enabled = true } : null;
    if (hosts is not null)
      // Nothing is known on the first pass, so ask for every address and then give the lookups a
      // moment. In a one-shot listing there is no later frame for a name to appear in, which is the
      // whole reason the interactive views do not do this.
      foreach (var connection in connections) {
        hosts.Lookup(connection.LocalAddress);
        hosts.Lookup(connection.RemoteAddress);
      }

    // Bounded, and it gives up at the limit rather than at an answer: an unreachable resolver costs
    // two seconds once, not two seconds a line.
    hosts?.WaitForPending(TimeSpan.FromSeconds(2));

    var rows = new List<Row>(connections.Count);
    foreach (var connection in connections) {
      if (!Wanted(connection.Protocol, options.ConnectionScope))
        continue;

      rows.Add(new(
        connection.Protocol.ToString().ToLowerInvariant(),
        Humanize.SocketKindName(connection.Kind),
        connection.State,
        Truncate(Humanize.LocalEndpoint(connection, services, hosts)),
        Truncate(Humanize.RemoteEndpoint(connection, services, hosts)),
        Humanize.SocketUser(connection),
        connection.Interface ?? "—",
        Humanize.Bytes(connection.SendQueueBytes),
        Humanize.Bytes(connection.ReceiveQueueBytes),
        Humanize.Count(connection.Retransmits),
        connection.LocalPort,
        connection.Pid,
        // Not "unknown": on Linux this means no process we may look at holds a descriptor on it,
        // which is a statement about our privilege rather than about the socket (PRD §5.3).
        connection.Pid == 0 ? "—" : names.GetValueOrDefault(connection.Pid) ?? "?"
      ));
    }

    // Grouped by protocol and then by port, because "what is listening on this machine" is read down
    // the port column, and the kernel hands these tables over in hash order, which reads as nothing.
    rows.Sort(static (left, right) => {
      var byProtocol = string.CompareOrdinal(left.Protocol, right.Protocol);
      if (byProtocol != 0)
        return byProtocol;

      var byPort = left.Port.CompareTo(right.Port);
      return byPort != 0 ? byPort : string.CompareOrdinal(left.Local, right.Local);
    });

    Write(rows);
    var attributed = 0;
    foreach (var row in rows)
      if (row.Pid != 0)
        ++attributed;

    Console.Error.WriteLine(
      $"\n{rows.Count} sockets, {attributed} attributed to a process. "
      + "Sockets held only by other users' processes cannot be attributed without the helper."
    );

    return 0;
  }

  private readonly record struct Row(
    string Protocol,
    string Kind,
    string State,
    string Local,
    string Remote,
    string User,
    string Interface,
    string SendQueue,
    string ReceiveQueue,
    string Retransmits,
    int Port,
    int Pid,
    string Process
  );

  /// <summary>
  /// Writes the table, sized to what is in it.
  /// </summary>
  /// <remarks>
  /// Every column is as wide as its widest value rather than as wide as its worst case: a machine
  /// with no IPv6 would otherwise get thirty-nine columns of blank for every address it has.
  /// </remarks>
  private static void Write(List<Row> rows) {
    var protocol = Width("PROTO", rows, static row => row.Protocol);
    var kind = Width("TYPE", rows, static row => row.Kind);
    var state = Width("STATE", rows, static row => row.State);
    var local = Width("LOCAL", rows, static row => row.Local);
    var remote = Width("REMOTE", rows, static row => row.Remote);
    var user = Width("USER", rows, static row => row.User);
    var iface = Width("IF", rows, static row => row.Interface);
    var send = Width("SEND-Q", rows, static row => row.SendQueue);
    var receive = Width("RECV-Q", rows, static row => row.ReceiveQueue);
    var retransmits = Width("RETX", rows, static row => row.Retransmits);

    Console.WriteLine(
      $"{"PROTO".PadRight(protocol)} {"TYPE".PadRight(kind)} {"STATE".PadRight(state)} "
      + $"{"LOCAL".PadRight(local)} {"REMOTE".PadRight(remote)} {"USER".PadRight(user)} "
      + $"{"IF".PadRight(iface)} {"SEND-Q".PadLeft(send)} {"RECV-Q".PadLeft(receive)} "
      + $"{"RETX".PadLeft(retransmits)} {"PID",7}  PROCESS"
    );

    foreach (var row in rows)
      Console.WriteLine(
        $"{row.Protocol.PadRight(protocol)} {row.Kind.PadRight(kind)} {row.State.PadRight(state)} "
        + $"{row.Local.PadRight(local)} {row.Remote.PadRight(remote)} {row.User.PadRight(user)} "
        + $"{row.Interface.PadRight(iface)} {row.SendQueue.PadLeft(send)} {row.ReceiveQueue.PadLeft(receive)} "
        + $"{row.Retransmits.PadLeft(retransmits)} {Pid(row.Pid),7}  {row.Process}"
      );
  }

  private static string Pid(int pid) => pid == 0 ? "—" : pid.ToString(CultureInfo.InvariantCulture);

  private static int Width(string header, List<Row> rows, Func<Row, string> cell) {
    var width = header.Length;
    foreach (var row in rows)
      width = Math.Max(width, cell(row).Length);

    return width;
  }

  /// <summary>
  /// A Unix socket's path can be longer than a terminal is wide, and one of them would then set the
  /// width of the column for every other row.
  /// </summary>
  private static string Truncate(string value) => value.Length <= _MaximumEndpoint
    ? value
    : string.Concat(value.AsSpan(0, _MaximumEndpoint - 1), "…");

  private const int _MaximumEndpoint = 45;

  private static bool Wanted(ConnectionProtocol protocol, ConnectionScope scope) => scope switch {
    ConnectionScope.All => true,
    ConnectionScope.Unix => protocol == ConnectionProtocol.Unix,
    _ => protocol != ConnectionProtocol.Unix,
  };

  private static Dictionary<int, string> Names(SystemSnapshot snapshot) {
    var names = new Dictionary<int, string>();
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i)
      names[processes[i].Pid] = processes[i].Name;

    return names;
  }

}
