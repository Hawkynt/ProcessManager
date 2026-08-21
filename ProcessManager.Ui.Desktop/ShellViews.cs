using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One entry in the navigation rail and the thing it shows (PRD §9, §10).
/// </summary>
/// <param name="Title">What the rail calls it.</param>
/// <param name="Content">
/// What goes in the content region, or null for an entry that opens a window of its own instead —
/// the performance page is a modeless window with its own lifetime and its own timer, and pretending
/// otherwise would mean two of it.
/// </param>
/// <param name="Show">
/// Run when the entry is chosen. For a view that is what collects its rows: none of these is
/// refreshed on the sample tick, because enumerating every unit on the machine once a second would
/// cost more than the thing being measured (PRD §5.4).
/// </param>
/// <param name="Describe">What the view currently holds, for a test and for the capture log.</param>
/// <param name="Rows">
/// How many rows it holds. Asked of the view rather than counted off <paramref name="Describe"/>:
/// the process tree describes itself in one line, and counting that line's newlines reported it as
/// holding nothing at all.
/// </param>
internal sealed record ShellView(
  string Title,
  Control? Content,
  Action Show,
  Func<string> Describe,
  Func<int> Rows
);

/// <summary>
/// The secondary views: what is configured to start, who is logged in, what services exist and what
/// is on the network (PRD §9).
/// </summary>
/// <remarks>
/// Every one of these was already collected and already printable from the command line — §9's
/// complaint was that there was no view. This is the view: the same probe calls the reports make,
/// in a table, behind the rail.
/// </remarks>
internal sealed class ShellViews(ISystemProbe probe) {

  private readonly ISystemProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

  #region what starts when you log in (PRD §42)

  // Every one of these tables is laid out to fit the content region at the window's default width.
  // That is a constraint rather than a preference: the toolkit's table scrolls vertically and not
  // horizontally, so a column past the right-hand edge is not a column somebody can scroll to, it is
  // a column that does not exist. The last one is widened to whatever is actually there, so a wider
  // window spends the room on the widest value instead of on empty page (PRD §11).
  private readonly RecordTable _startup = new(
    ("Name", 220),
    ("Enabled", 150),
    ("Scope", 70),
    ("Shown in", 110),
    ("Command", 230),
    // Last, so it is the one that grows with the window. These paths share a long prefix and differ
    // at the end, so a truncated one loses precisely the part that says which file it is.
    ("Configured by", 250)
  );

  /// <summary>
  /// The entries as they were last read, so the menu acts on the one the reader is looking at rather
  /// than on a row index into a list that has since been read again.
  /// </summary>
  private IReadOnlyList<StartupEntry> _startupEntries = [];

  /// <summary>What may be done to the entry under the pointer, hung on the startup table.</summary>
  public ContextMenuStrip? StartupMenu {
    get => this._startup.ContextMenuStrip;
    set => this._startup.ContextMenuStrip = value;
  }

  /// <summary>The entry the cursor is on, or null when it is on nothing.</summary>
  /// <remarks>
  /// Matched by the file it came from rather than by its name: two entries may share a name — a
  /// system one and the user override that replaced it are the obvious pair — and the path is what
  /// the switch actually needs.
  /// </remarks>
  public StartupEntry? SelectedStartup {
    get {
      if (this._startup.Selected is not { Length: > 5 } cells)
        return null;

      foreach (var entry in this._startupEntries)
        if (string.Equals(entry.Path, cells[5], StringComparison.Ordinal))
          return entry;

      return null;
    }
  }

  /// <summary>Says the switch is there, and how to get at it.</summary>
  public void StartupIsSwitchable() => this._startupHint = "  Right-click an entry to turn it on or off.";

  private string _startupHint = string.Empty;

  public Control StartupControl => this._startup.Control;

  public string StartupText => this._startup.Description;

  public int StartupRows => this._startup.RowCount;

  public void RefreshStartup() {
    var entries = this._probe.GetStartupEntries();
    this._startupEntries = entries;
    var enabled = 0;
    foreach (var entry in entries)
      if (entry.Enabled)
        ++enabled;

    this._startup.Fill(
      entries.Count == 0
        ? "Nothing is configured to start at login — or nothing this build knows how to read is."
        : $"{entries.Count} entries, {enabled} of which will run.  {AsOf()}{this._startupHint}",
      entries.Count,
      i => [
        entries[i].Name,
        // The reason, not the boolean. "Hidden by a user override" and "not for this desktop" are
        // different problems with different fixes, and a column of "no" tells the reader neither.
        entries[i].Enabled ? "yes" : entries[i].DisabledReason ?? "no",
        entries[i].Scope == StartupScope.System ? "machine" : "user",
        entries[i].OnlyShowIn ?? "any desktop",
        entries[i].Command,
        entries[i].Path,
      ]
    );
  }

  #endregion

  #region who is logged in (PRD §43)

  private readonly RecordTable _sessions = new(
    ("User", 140),
    ("Terminal", 100),
    ("Kind", 110),
    ("Logged in", 170),
    ("PID", 70),
    ("From", 200)
  );

  public Control SessionsControl => this._sessions.Control;

  public string SessionsText => this._sessions.Description;

  public int SessionsRows => this._sessions.RowCount;

  public void RefreshSessions() {
    var sessions = this._probe.GetSessions();
    var people = 0;
    foreach (var session in sessions)
      if (session.Kind == SessionKind.User)
        ++people;

    this._sessions.Fill(
      sessions.Count == 0
        ? "No sessions came back — the login database is empty or unreadable from here."
        : $"{sessions.Count} records, {people} of them somebody logged in.  {AsOf()}",
      sessions.Count,
      i => [
        sessions[i].UserName,
        sessions[i].Terminal,
        sessions[i].Kind.ToString(),
        Humanize.Timestamp(sessions[i].LoginTimeUtcTicks),
        sessions[i].Pid > 0 ? sessions[i].Pid.ToString(CultureInfo.InvariantCulture) : "—",
        // Null and empty are different: null is a login at the machine's own keyboard, and an empty
        // string is a remote host the file did not name.
        sessions[i].RemoteHost ?? "this machine",
      ]
    );
  }

  #endregion

  #region what the machine runs in the background (PRD §41)

  private readonly RecordTable _services = new(
    // Unit names run long — `NetworkManager-wait-online-initrd.service` is not unusual.
    ("Unit", 300),
    ("State", 78),
    ("At boot", 88),
    ("Main PID", 72),
    ("Unit file", 260),
    ("Description", 260)
  );

  /// <summary>
  /// What the services heading says about commanding a unit, which depends on whether anything here
  /// can. Set by the window once, because only it knows whether a control was found.
  /// </summary>
  private string _servicesHint = "  Starting and stopping them needs a service manager this build cannot reach.";

  /// <summary>Says the commands are there, and how to get at them.</summary>
  public void ServicesAreCommandable() => this._servicesHint = "  Right-click a unit to start, stop or enable it.";

  public Control ServicesControl => this._services.Control;

  public string ServicesText => this._services.Description;

  public int ServicesRows => this._services.RowCount;

  public void RefreshServices() {
    var services = this._probe.GetServices();
    var running = 0;
    foreach (var service in services)
      if (service.State == ServiceState.Running)
        ++running;

    this._services.Fill(
      services.Count == 0
        ? "No services came back. On Windows the service control manager is not read yet; on Linux this needs systemd's unit files."
        : $"{services.Count} units, {running} running.  {AsOf()}{this._servicesHint}",
      services.Count,
      i => [
        services[i].Name,
        services[i].State switch {
          ServiceState.Running => "running",
          ServiceState.Inactive => "inactive",
          _ => "—",
        },
        // Masked first, because a masked unit can never run whatever else is configured, and it is
        // the state people forget they set. Null is neither enabled nor disabled — a unit started by
        // a socket or a timer is genuinely neither.
        services[i].Masked ? "masked" : services[i].Enabled switch {
          true => "enabled",
          false => "disabled",
          null => "—",
        },
        services[i].MainPid > 0 ? services[i].MainPid.ToString(CultureInfo.InvariantCulture) : "—",
        services[i].Path,
        // Last, and it is the column somebody reads to find out what a unit is for. The restart
        // policy is not here: seven columns do not fit the content region, the table does not
        // scroll sideways, and a column past the edge is not one somebody can scroll to.
        services[i].Description ?? "—",
      ]
    );
  }

  /// <summary>
  /// Puts the cursor on one unit, for a navigation that came from a process (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// By name, because the name is what the cgroup gives up and a row number would be this list's
  /// collection order — a second place that had to agree with it. False when the machine's unit files
  /// hold no such unit, which is a real outcome rather than a fault: a transient scope systemd made at
  /// runtime is in the cgroup tree and on no disk.
  /// </remarks>
  public bool SelectService(string unit) => this._services.Select(unit);

  /// <summary>
  /// What may be done to the unit under the pointer, hung on the services table.
  /// </summary>
  /// <remarks>
  /// Built by the window rather than here, because doing something to a unit means asking a person
  /// first and then showing them what the manager said, and this class has no way to do either. It
  /// owns the tables; the window owns the conversation.
  /// </remarks>
  public ContextMenuStrip? ServicesMenu {
    get => this._services.ContextMenuStrip;
    set => this._services.ContextMenuStrip = value;
  }

  /// <summary>The unit the cursor is on, or null when it is on nothing.</summary>
  public string? SelectedService
    => this._services.Selected is { Length: > 0 } cells && cells[0].Length > 0 ? cells[0] : null;

  #endregion

  #region what is on the network (PRD §40)

  private readonly RecordTable _network = new(
    ("Protocol", 70),
    ("Local", 175),
    ("Remote", 175),
    ("State", 100),
    ("PID", 70),
    ("Process", 150),
    ("User", 85),
    // The pair that says which end of a stalled connection is the slow one: what the peer has not
    // acknowledged, and what this process has not read.
    ("Send-Q", 70),
    ("Recv-Q", 70),
    ("Interface", 90)
  );

  public Control NetworkControl => this._network.Control;

  public string NetworkText => this._network.Description;

  public int NetworkRows => this._network.RowCount;

  /// <param name="names">
  /// Process names by pid, from the sample the window already has. Asked for rather than read here:
  /// a socket's owning pid is a number, and a table of numbers is a table nobody can read.
  /// </param>
  public void RefreshNetwork(IReadOnlyDictionary<int, string> names) {
    ArgumentNullException.ThrowIfNull(names);

    var connections = this._probe.GetConnections();
    var attributed = 0;
    foreach (var connection in connections)
      if (connection.Pid > 0)
        ++attributed;

    this._network.Fill(
      connections.Count == 0
        ? "No sockets came back — /proc/net is empty, or this build does not read the connection tables here."
        : $"{connections.Count} sockets, {attributed} of them traceable to a process from this account.  {AsOf()}",
      connections.Count,
      i => [
        connections[i].Protocol.ToString(),
        Humanize.LocalEndpoint(connections[i]),
        Humanize.RemoteEndpoint(connections[i]),
        connections[i].State,
        // A socket whose owner this account may not see is not a socket belonging to pid 0. Saying
        // "—" is the difference between "nobody owns it" and "you may not ask" (PRD §72.3).
        connections[i].Pid > 0 ? connections[i].Pid.ToString(CultureInfo.InvariantCulture) : "—",
        connections[i].Pid > 0 && names.TryGetValue(connections[i].Pid, out var name) ? name : "—",
        Humanize.SocketUser(connections[i]),
        Humanize.Bytes(connections[i].SendQueueBytes),
        Humanize.Bytes(connections[i].ReceiveQueueBytes),
        connections[i].Interface ?? "—",
      ]
    );
  }

  /// <summary>The pid in the selected socket row, or -1 when there is none to go to.</summary>
  public int SelectedNetworkPid
    => this._network.Selected is { Length: > 4 } cells
      && int.TryParse(cells[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
        ? pid
        : -1;

  /// <summary>Raised when a socket row is opened, which is the gesture for "show me who owns this".</summary>
  public event EventHandler<MouseEventArgs>? NetworkRowOpened {
    add => this._network.RowOpened += value;
    remove => this._network.RowOpened -= value;
  }

  #endregion

  /// <summary>
  /// Widens every table's last column to whatever the content region currently is.
  /// </summary>
  /// <remarks>
  /// Run from the window's layout pass. A control outside the toolkit's own assembly cannot observe
  /// its own resize, so this is the only way any of these hears that the window changed size.
  /// </remarks>
  public void Stretch() {
    this._startup.Stretch();
    this._sessions.Stretch();
    this._services.Stretch();
    this._network.Stretch();
  }

  /// <summary>
  /// When the rows were collected.
  /// </summary>
  /// <remarks>
  /// On the heading of every one of these, because none of them follows the sample tick and a table
  /// that silently stopped being true is worse than one that admits its age.
  /// </remarks>
  private static string AsOf()
    => "As of " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) + ".";

}
