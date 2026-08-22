using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

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
internal sealed class ShellViews(ISystemProbe probe, Sampling.Sampler? sampler = null) {

  private readonly ISystemProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));

  /// <summary>
  /// The window's own sampler, for the views that need what a process is costing as well as what it
  /// is (PRD §43).
  /// </summary>
  /// <remarks>
  /// Optional, because most of these views need nothing but the probe and a test that builds one
  /// should not have to raise a sampler to check a column of unit names. A view that has none says
  /// so in its cells rather than showing nought.
  /// </remarks>
  private readonly Sampling.Sampler? _sampler = sampler;

  #region what starts when you log in (PRD §42)

  // Ordered by what a reader came for, and everything a narrow window cuts off is reachable by
  // scrolling sideways rather than lost. The last column is widened to whatever is actually there, so
  // a wider window spends the room on the widest value instead of on empty page (PRD §11).
  private readonly RecordTable _startup = new(
    "Startup entries",
    // A unit's own name is the long case here: `drkonqi-coredump-cleanup.service` is thirty-two
    // characters, and at 258 the capture still lost its last letter against the column beside it.
    ("Name", 276),
    ("Enabled", 76),
    // Why, when it will not run. Its own column now rather than sharing the one above: "hidden by a
    // user override" and "not for this desktop" are different problems with different fixes, and
    // both of them are longer than the word they used to replace.
    ("Status", 210),
    // Which mechanism will start it, which is also what turning it off means. A desktop file and a
    // user unit are switched in completely different ways (PRD §42).
    ("Started by", 150),
    ("Scope", 70),
    // "KDE, GNOME, Unity, XFCE" is a real value on this machine and is twenty-three characters; at
    // 168 it lost the last desktop in the list, which is the one thing this column is read for.
    ("Shown in", 196),
    // What it would cost at login. Empty of numbers on purpose: nothing on this machine measures it,
    // and a made-up "Medium" is the one answer §42 forbids.
    ("Impact", 132),
    ("Program", 230),
    ("Arguments", 230),
    ("Description", 240),
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
      if (this._startup.Selected is not { Length: > _StartupPathColumn } cells)
        return null;

      foreach (var entry in this._startupEntries)
        if (string.Equals(entry.Path, cells[_StartupPathColumn], StringComparison.Ordinal))
          return entry;

      return null;
    }
  }

  /// <summary>Where the file path is. A column index in one place rather than in three.</summary>
  private const int _StartupPathColumn = 10;

  /// <summary>Every entry as text, in the order the table shows them (PRD §42, §95).</summary>
  public string DescribeStartup() => this._startup.Describe();

  /// <summary>The selected entry as text, headers included.</summary>
  public string DescribeSelectedStartup() => this._startup.DescribeSelected();

  /// <summary>
  /// Says what a right-click offers here, which depends on whether anything can write the switch.
  /// </summary>
  /// <remarks>
  /// Two sentences and not one-or-nothing. A machine that cannot switch an entry can still open its
  /// file, reveal its program and copy the row, and a heading that went silent about the menu would
  /// hide six commands because of three (PRD §7).
  /// </remarks>
  public void StartupIsSwitchable(bool switchable)
    => this._startupHint = switchable
      ? "  Right-click an entry to turn it on or off."
      : "  Right-click an entry to open or copy it; nothing here can write the switch.";

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
        // The impact column is named here rather than explained in every cell of it, and the whole
        // sentence is in an entry's properties box. Short enough to fit the heading band at the
        // window's default width: the first draft of it was cut off with an ellipsis three words
        // before the end, which is a clause nobody ever reads (PRD §11, §42).
        : $"{entries.Count} entries, {enabled} of which will run. Impact is not measured.  {AsOf()}{this._startupHint}",
      entries.Count,
      i => [
        entries[i].Name,
        entries[i].Enabled ? "yes" : "no",
        // The reason beside the answer, not instead of it. "Hidden by a user override" and "not for
        // this desktop" are different problems with different fixes, and a column of "no" tells the
        // reader neither.
        entries[i].Enabled ? "it will run at your next login" : entries[i].DisabledReason ?? "switched off",
        entries[i].Mechanism switch {
          StartupMechanism.SystemdUserUnit => "systemd user unit",
          _ => "XDG autostart",
        },
        entries[i].Scope == StartupScope.System ? "machine" : "user",
        Desktops(entries[i].OnlyShowIn),
        // No category and no invented number. Working out what an entry costs at login means timing
        // the login, and nothing here does — a "Medium" derived from the size of the binary would be
        // a guess wearing a measurement's clothes (PRD §42).
        _NoImpactMeasurement,
        entries[i].Executable ?? "—",
        entries[i].Arguments ?? "—",
        entries[i].Description ?? "—",
        entries[i].Path.Length > 0 ? entries[i].Path : "no file — only the enablement",
      ]
    );
  }

  /// <summary>
  /// What the impact column says on a machine that measures nothing (PRD §42).
  /// </summary>
  /// <remarks>
  /// Short, because it is on every row; the heading carries the sentence. What it must not be is a
  /// category: "Medium" next to a program nobody has timed is the invented answer §42 exists to
  /// forbid, and it is the one a reader would act on.
  /// </remarks>
  private const string _NoImpactMeasurement = "not measured";

  /// <summary>
  /// The desktops an entry is limited to, as a list a person reads rather than as the file's own.
  /// </summary>
  /// <remarks>
  /// The specification's separator is a semicolon and its lists end with one, so the raw value is
  /// <c>KDE;GNOME;Unity;</c> — which reads as a fourth, empty desktop. The value is not changed
  /// anywhere it matters: the entry's own file still says what it says, and this is a cell.
  /// </remarks>
  private static string Desktops(string? onlyShowIn)
    => onlyShowIn is { Length: > 0 } list
      ? string.Join(", ", list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      : "any desktop";

  #endregion

  #region what each program has cost, and what has happened (PRD §44, §63)

  private readonly RecordTable _usage = new(
    "What each program has cost",
    ("Program", 300),
    ("Processor time", 120),
    ("Read", 100),
    ("Written", 100),
    ("Peak memory", 110),
    ("Average memory", 120),
    ("Times run", 90),
    ("Running for", 110),
    ("Last seen", 160)
  );

  public Control UsageControl => this._usage.Control;

  public string UsageText => this._usage.Description;

  public int UsageRows => this._usage.RowCount;

  /// <summary>
  /// What each program has cost this machine, across every time it has been run (PRD §44).
  /// </summary>
  /// <remarks>
  /// The record is kept only when somebody asked for it, so this page says which of the two states
  /// it is in rather than showing an empty table. "Nothing yet" and "nobody is keeping this" are
  /// different answers and only one of them is a reason to wait.
  /// </remarks>
  public void RefreshUsage(UsageHistory? history) {
    if (history is null) {
      this._usage.Fill(
        "Nothing is being recorded. Put history.usage=true in the settings file to keep a record of "
        + "what each program costs this machine, across sessions.",
        0,
        _ => []
      );

      return;
    }

    var records = new List<UsageRecord>(history.Records);
    // Dearest first, which is the order somebody opening this is asking about.
    records.Sort(static (left, right) => right.CpuTimeNs.CompareTo(left.CpuTimeNs));

    this._usage.Fill(
      records.Count == 0
        ? $"Recording, and nothing has been seen twice yet.  {AsOf()}"
        : $"{records.Count} programs.  {AsOf()}",
      records.Count,
      i => [
        records[i].Application,
        Humanize.Duration(Counter.Of(records[i].CpuTimeNs)),
        Humanize.Bytes(Counter.Of(records[i].ReadBytes)),
        Humanize.Bytes(Counter.Of(records[i].WrittenBytes)),
        Humanize.Bytes(Counter.Of(records[i].PeakWorkingSetBytes)),
        Humanize.Bytes(Counter.Of((ulong)Math.Max(0, records[i].AverageWorkingSetBytes))),
        records[i].Launches.ToString(CultureInfo.InvariantCulture),
        Humanize.Duration(Counter.Of((ulong)Math.Max(0, records[i].RuntimeSeconds) * 1_000_000_000ul)),
        Humanize.Timestamp(records[i].LastSeenUtcTicks),
      ]
    );
  }

  private readonly RecordTable _timeline = new(
    "What has happened",
    ("When", 170),
    ("Kind", 150),
    ("PID", 80),
    ("What happened", 520)
  );

  public Control TimelineControl => this._timeline.Control;

  public string TimelineText => this._timeline.Description;

  public int TimelineRows => this._timeline.RowCount;

  /// <summary>
  /// What has happened while this has been running (PRD §63).
  /// </summary>
  /// <remarks>
  /// Newest first, the same way the terminal's overlay shows it: somebody opening this has just
  /// noticed something and wants the most recent thing rather than to scroll past an hour to reach
  /// it. The heading counts what is shown against what there has been, because a ring that dropped
  /// the older ones and says only its own size reads as though that was all there was.
  /// </remarks>
  public void RefreshTimeline(EventLog log) {
    ArgumentNullException.ThrowIfNull(log);
    var entries = log.Entries;
    var now = DateTime.UtcNow.Ticks;

    this._timeline.Fill(
      entries.Count == 0
        ? "Nothing has happened yet that this was watching for."
        : entries.Count == log.Total
          ? $"{entries.Count} things have happened.  {AsOf()}"
          : $"The last {entries.Count} of {log.Total}.  {AsOf()}",
      entries.Count,
      i => {
        var entry = entries[entries.Count - 1 - i];
        return [
          Humanize.When(entry.UtcTicks, now),
          EventLog.Describe(entry.Category),
          entry.Pid > 0 ? entry.Pid.ToString(CultureInfo.InvariantCulture) : "—",
          entry.Text,
        ];
      }
    );
  }

  #endregion

  #region who is logged in (PRD §43)  #region who is logged in (PRD §43)

  private readonly RecordTable _sessions = new(
    "Sessions",
    ("User", 140),
    // The account's own description, where the password file has one. Most system accounts have
    // none, so the column is narrow and mostly empty — which is what "this machine does not know"
    // looks like, and is better than a made-up name.
    ("Full name", 170),
    ("Terminal", 100),
    ("Session", 74),
    ("Type", 96),
    ("State", 74),
    ("Idle", 70),
    ("Kind", 110),
    ("Logged in", 170),
    ("PID", 70),
    ("From", 200),
    // What the login is costing. These were on the command line and nowhere in the window, so the
    // view answered who was logged in and nothing about what that cost — which is the half of the
    // page anybody opens it for (PRD §58).
    ("Processes", 84),
    ("CPU %", 74),
    ("Memory", 90),
    ("Disk", 90),
    ("GPU %", 74)
  );

  public Control SessionsControl => this._sessions.Control;

  public string SessionsText => this._sessions.Description;

  public int SessionsRows => this._sessions.RowCount;

  /// <summary>
  /// The records behind the rows, kept because a menu item needs more than a cell holds.
  /// </summary>
  /// <remarks>
  /// The session id especially: it is the only thing <c>loginctl</c> will accept, it is a dash on
  /// every row that has none, and reading it back out of a cell would turn that dash into an id.
  /// </remarks>
  private IReadOnlyList<SessionRecord> _sessionRows = [];

  /// <summary>The record behind the selected row, or null when the cursor is on nothing.</summary>
  /// <remarks>
  /// Matched on the pid, which is the one cell that is unique across the file: two of a person's
  /// logins share a name, a kind and often a terminal, and matching on the name would act on
  /// whichever of them came first.
  /// </remarks>
  public SessionRecord? SelectedSession {
    get {
      if (this._sessions.Selected is not { Length: > 10 } cells
          || !int.TryParse(cells[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        return null;

      foreach (var session in this._sessionRows)
        if (session.Pid == pid)
          return session;

      return null;
    }
  }

  /// <summary>Every session as text, in the order the table shows them (PRD §43, §95).</summary>
  public string DescribeSessions() => this._sessions.Describe();

  /// <summary>The selected session as text, headers included.</summary>
  public string DescribeSelectedSession() => this._sessions.DescribeSelected();

  /// <summary>
  /// What may be done to the session under the pointer, hung on the sessions table.
  /// </summary>
  /// <remarks>
  /// Built by the window for the same reason the services menu is: ending somebody's session means
  /// asking a person first and then showing them what the login manager said, and neither is
  /// something a class that owns tables can do.
  /// </remarks>
  public ContextMenuStrip? SessionsMenu {
    get => this._sessions.ContextMenuStrip;
    set => this._sessions.ContextMenuStrip = value;
  }

  public void RefreshSessions() {
    var sessions = this._probe.GetSessions();
    this._sessionRows = sessions;
    var people = 0;
    foreach (var session in sessions)
      if (session.Kind == SessionKind.User)
        ++people;

    // The same sums the command line's report makes, from the same function in Core: two additions
    // of the same column that could disagree are two answers to one question (PRD §5.1).
    var totals = this._sampler is { } sampler
      ? SessionTotals.Of(sampler.Current, sampler.Delta)
      : [];

    var now = DateTime.UtcNow;
    this._sessions.Fill(
      sessions.Count == 0
        ? "No sessions came back — the login database is empty or unreadable from here."
        : $"{sessions.Count} records, {people} of them somebody logged in.  {AsOf()}",
      sessions.Count,
      i => {
        var session = sessions[i];
        var total = totals.TryGetValue(session.UserName, out var found) ? found : UserTotals.None;

        // Only a login has processes to total. A boot record and a dead slot have an account name in
        // them and no account behind them, and putting a user's figures on those rows would count the
        // same processes three times down the page.
        var person = session.Kind == SessionKind.User;
        return [
          session.UserName,
          session.FullName ?? "—",
          session.Terminal,
          session.SessionId ?? "—",
          SessionFacts.Describe(session.Type),
          SessionFacts.Describe(session.State),
          SessionFacts.DescribeIdle(SessionFacts.IdleFor(session.LastInputUtcTicks, now)),
          session.Kind.ToString(),
          Humanize.Timestamp(session.LoginTimeUtcTicks),
          session.Pid > 0 ? session.Pid.ToString(CultureInfo.InvariantCulture) : "—",
          // Null and empty are different: null is a login at the machine's own keyboard, and an empty
          // string is a remote host the file did not name.
          session.RemoteHost ?? "this machine",
          person ? total.Processes.ToString(CultureInfo.InvariantCulture) : "—",
          person ? Humanize.Percent(total.CpuPercent) : "—",
          person ? Humanize.Bytes(total.PrivateBytes) : "—",
          person ? Humanize.BytesPerSecond(total.DiskBytesPerSecond) : "—",
          person ? Humanize.Percent(total.GpuPercent) : "—",
        ];
      }
    );
  }

  #endregion

  #region what the machine runs in the background (PRD §41)

  // Ordered by what a reader came for rather than by what a unit file happens to say first. The list
  // scrolls sideways, so the columns past the right-hand edge are reachable rather than lost — which
  // is what makes fourteen of them defensible where six used to be the ceiling (PRD §11, §41).
  private readonly RecordTable _services = new(
    "Services",
    // Unit names run long — `NetworkManager-wait-online-initrd.service` is not unusual, and it is
    // forty characters. Measured off a capture rather than guessed: at 320 that unit's name still lost its last letter against
    // the state beside it with nothing between them.
    ("Unit", 348),
    // State and sub-state in one cell. "active · exited" is one answer at two levels of detail, and
    // two columns would put "active" beside "exited" as though they disagreed.
    ("State", 120),
    ("At boot", 84),
    ("Main PID", 72),
    // When the manager's current invocation of the unit began, from its own runtime directory. As
    // wide as a whole timestamp: at 140 the capture showed "2026-08-18 18:41:4(", which is a minute
    // that could be any of sixty.
    ("Started", 158),
    // "notify-reload" is the longest of systemd's type names and is thirteen characters; at 88 it
    // photographed as "notify-reloa" running into the account beside it.
    ("Type", 112),
    // The account, and whether the unit says so or is taking the manager's default. Two different
    // statements about a unit, and one column that collapses them would be wrong about both.
    ("Runs as", 118),
    ("Load", 84),
    // "on-failure" and "on-abnormal" are the two long ones; at 82 the first lost its last letter.
    ("Restart", 96),
    // How many units this one is tied to, and how many are tied to it. The counts rather than the
    // lists: the lists are what "Inspect dependencies…" opens, and one of them is forty units long.
    ("Needs", 56),
    ("Needed by", 78),
    ("Command", 280),
    ("Unit file", 250),
    ("Description", 260)
  );

  /// <summary>
  /// What the services heading says about commanding a unit, which depends on whether anything here
  /// can. Set by the window once, because only it knows whether a control was found.
  /// </summary>
  private string _servicesHint = "  Starting and stopping them needs a service manager this build cannot reach.";

  /// <summary>
  /// Says what a right-click offers here, which depends on whether there is a manager to ask.
  /// </summary>
  /// <remarks>
  /// The second sentence is not a consolation prize. Opening a unit file, going to its main process
  /// and reading its dependencies are the things somebody diagnosing a machine actually needs, and
  /// none of them asks a manager for anything (PRD §7).
  /// </remarks>
  public void ServicesAreCommandable(bool commandable)
    => this._servicesHint = commandable
      ? "  Right-click a unit to start, stop or enable it."
      : "  Right-click a unit to inspect it; starting and stopping needs a manager this build cannot reach.";

  public Control ServicesControl => this._services.Control;

  public string ServicesText => this._services.Description;

  public int ServicesRows => this._services.RowCount;

  public void RefreshServices() {
    var services = this._probe.GetServices();
    this._serviceRows = services;
    var running = 0;
    var active = 0;
    foreach (var service in services)
      switch (service.State) {
        case ServiceState.Running: ++running; break;
        case ServiceState.Active: ++active; break;
        default: break;
      }

    this._services.Fill(
      services.Count == 0
        ? "No services came back. On Windows the service control manager is not read yet; on Linux this needs systemd's unit files."
        // Active and running counted apart, because they are different answers: a unit that set
        // something up and finished is still doing its job and has nothing in a cgroup. Kept short
        // enough to fit the heading band at the window's default width — a sentence the label has to
        // cut off with an ellipsis is one whose last clause nobody reads.
        : $"{services.Count} units, {running} running and {active} more active with no processes.  {AsOf()}{this._servicesHint}",
      services.Count,
      i => [
        services[i].Name,
        DescribeState(services[i]),
        // Masked first, because a masked unit can never run whatever else is configured, and it is
        // the state people forget they set. Null is neither enabled nor disabled — a unit started by
        // a socket or a timer is genuinely neither.
        services[i].Masked ? "masked" : services[i].Enabled switch {
          true => "enabled",
          false => "disabled",
          null => "—",
        },
        services[i].MainPid > 0 ? services[i].MainPid.ToString(CultureInfo.InvariantCulture) : "—",
        Started(services[i].ActivatedUtcTicks),
        services[i].Type ?? "—",
        // The default is stated as a default. "It says root" and "it says nothing, and the system
        // manager's default is root" are different facts about a unit (PRD §5.3).
        services[i].Account ?? (services[i].LoadState == ServiceLoadState.Loaded ? "root (default)" : "—"),
        services[i].LoadState switch {
          ServiceLoadState.Loaded => "loaded",
          ServiceLoadState.Masked => "masked",
          ServiceLoadState.Transient => "transient",
          _ => "—",
        },
        services[i].RestartPolicy ?? "—",
        services[i].Dependencies.Count.ToString(CultureInfo.InvariantCulture),
        services[i].Dependents.Count.ToString(CultureInfo.InvariantCulture),
        services[i].Command ?? "—",
        services[i].Path.Length > 0 ? services[i].Path : "none on disk",
        // The column somebody reads to find out what a unit is for, and last so it takes the width
        // the window has spare.
        services[i].Description ?? "—",
      ]
    );
  }

  /// <summary>
  /// The state and the sub-state in one cell (PRD §41).
  /// </summary>
  /// <remarks>
  /// "active · exited" is one answer at two levels of detail: the unit is doing its job and there is
  /// nothing of it in a cgroup, which is what a <c>oneshot</c> that set something up looks like. Two
  /// columns would print those side by side as though they were two claims that disagreed.
  /// </remarks>
  private static string DescribeState(ServiceRecord service) => service.State switch {
    ServiceState.Running => "running",
    ServiceState.Active => "active · exited",
    ServiceState.Inactive => "inactive",
    _ => "—",
  };

  /// <summary>
  /// When the unit's current invocation began, or which of the two reasons there is no answer.
  /// </summary>
  /// <remarks>
  /// A unit the manager holds no invocation of has not started, and a machine whose manager writes no
  /// runtime directory cannot say whether anything has. Both are placeholders and they are not the
  /// same placeholder (PRD §72.3).
  /// </remarks>
  private static string Started(Counter activated)
    => activated.TryGetValue(out var ticks)
      ? Humanize.Timestamp((long)ticks)
      : activated.Reason == UnknownReason.SourceGone ? "—" : Humanize.Placeholder(activated.Reason);

  /// <summary>
  /// The units as they were last read, so an action works from the record rather than from the text
  /// of a row.
  /// </summary>
  /// <remarks>
  /// A dependency list, a unit file path and an executable are all things a menu item needs and none
  /// of them survives a trip through a table cell intact — the command cell holds one line, and the
  /// dependency cells hold counts.
  /// </remarks>
  private IReadOnlyList<ServiceRecord> _serviceRows = [];

  /// <summary>The record behind the selected unit, or null when the cursor is on nothing.</summary>
  public ServiceRecord? SelectedServiceRecord {
    get {
      if (this.SelectedService is not { Length: > 0 } name)
        return null;

      foreach (var service in this._serviceRows)
        if (string.Equals(service.Name, name, StringComparison.Ordinal))
          return service;

      return null;
    }
  }

  /// <summary>Every unit as text, in the order the table shows them (PRD §41, §95).</summary>
  public string DescribeServices() => this._services.Describe();

  /// <summary>The selected unit as text, headers included.</summary>
  public string DescribeSelectedService() => this._services.DescribeSelected();

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
    "Connections",
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

    // Named ports, as --connections names them and as ss does by default. The same table the lower
    // pane's network tab and the terminal both ask the probe for, so all three say https where this
    // one used to say 443 (PRD §40, §58).
    var services = this._probe.DescribePortNames();

    this._network.Fill(
      connections.Count == 0
        ? "No sockets came back — /proc/net is empty, or this build does not read the connection tables here."
        : $"{connections.Count} sockets, {attributed} of them traceable to a process from this account.  {AsOf()}",
      connections.Count,
      i => [
        connections[i].Protocol.ToString(),
        Humanize.LocalEndpoint(connections[i], services, null),
        Humanize.RemoteEndpoint(connections[i], services, null),
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

  /// <summary>The menu a right-click on a socket row opens (PRD §40).</summary>
  public ContextMenuStrip? NetworkMenu {
    get => this._network.ContextMenuStrip;
    set => this._network.ContextMenuStrip = value;
  }

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
