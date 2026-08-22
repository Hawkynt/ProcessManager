using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What a notification is about (PRD §64).</summary>
/// <remarks>
/// <see cref="Unclassified"/> is nought so that a default-constructed notification is not one of the
/// real kinds. The same rule the rest of the model follows: the value nobody filled in must never
/// turn out to be a real answer (PRD §72.3).
/// </remarks>
public enum NotificationKind : byte {

  Unclassified = 0,

  /// <summary>Any process appeared.</summary>
  ProcessStarted,

  /// <summary>Any process went.</summary>
  ProcessEnded,

  /// <summary>A process whose name a rule names appeared.</summary>
  NamedProcessStarted,

  /// <summary>A process crossed the CPU threshold, measured the way §23 measures it: per core.</summary>
  CpuAboveThreshold,

  /// <summary>A process crossed the share-of-machine memory threshold.</summary>
  MemoryAboveThreshold,

  /// <summary>A process crossed the read-plus-write throughput threshold.</summary>
  DiskAboveThreshold,

  /// <summary>A unit a rule names stopped having processes.</summary>
  ServiceStopped,

  /// <summary>A rule somebody wrote themselves fired (PRD §84).</summary>
  RuleFired,

}

/// <summary>One thing that happened, in the words it should be shown in.</summary>
/// <param name="Text">A whole sentence. It is the same string in the window and in the terminal.</param>
public readonly record struct Notification(NotificationKind Kind, string Text);

/// <summary>
/// What somebody asked to be told about (PRD §64, §67).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule is explicit and every rule is off until it is written down.</b> There is no
/// heuristic here and nothing fires that nobody asked for: an empty record produces no notifications
/// at all, which is what an unconfigured program should do. A monitor that decided for itself what
/// was worth interrupting somebody about would be interrupting them during the one hour they were
/// using it to diagnose something.
/// </para>
/// <para>
/// The thresholds are nullable rather than nought, because nought is a threshold — "tell me about
/// every process that uses any CPU at all" is a sentence somebody could mean, and a record that
/// could not tell that from "no rule" would either fire constantly or refuse a legitimate rule.
/// </para>
/// </remarks>
public sealed record NotificationRules {

  /// <summary>Tell me whenever anything starts.</summary>
  public bool ProcessStarted { get; init; }

  /// <summary>Tell me whenever anything ends.</summary>
  public bool ProcessEnded { get; init; }

  /// <summary>
  /// Tell me when one of these starts, by name.
  /// </summary>
  /// <remarks>
  /// The name rather than the path, and matched without regard to case, because this is the rule
  /// somebody writes to catch a program they are hunting — and they know what it is called, not
  /// where this copy of it was installed.
  /// </remarks>
  public IReadOnlyList<string> Names { get; init; } = [];

  /// <summary>Per core, the way §23's own bands are: "it is eating a core" is the unit people think in.</summary>
  public double? CpuPercent { get; init; }

  /// <summary>A share of the machine's memory, because a gigabyte means nothing until you know how many there are.</summary>
  public double? MemoryPercent { get; init; }

  /// <summary>Read plus write, in bytes a second.</summary>
  public double? DiskBytesPerSecond { get; init; }

  /// <summary>Tell me when one of these units stops having processes.</summary>
  public IReadOnlyList<string> Services { get; init; } = [];

  /// <summary>
  /// The rules somebody wrote themselves, in the language of PRD §84.
  /// </summary>
  /// <remarks>
  /// The six above are the ready-made rules §64 names, each of them one line and one number; these
  /// are the ones nobody could have anticipated — a named service over a threshold for half a minute,
  /// a process changing state, a daemon going. They arrive parsed rather than as text, so that a
  /// settings file with a broken rule in it reports the rule and starts the program, instead of
  /// discovering the problem the first time the rule would have fired.
  /// </remarks>
  public IReadOnlyList<AlertRule> Alerts { get; init; } = [];

  /// <summary>Whether anything at all was asked for. Nothing is polled and nothing is compared when not.</summary>
  public bool Any
    => this.ProcessStarted
    || this.ProcessEnded
    || this.Names.Count > 0
    || this.CpuPercent.HasValue
    || this.MemoryPercent.HasValue
    || this.DiskBytesPerSecond.HasValue
    || this.Services.Count > 0
    || this.Alerts.Count > 0;

  /// <summary>Whether anything here needs the service list, which is the one dear thing on the list.</summary>
  public bool NeedsServices => this.Services.Count > 0;

}

/// <summary>
/// Turns two consecutive samples into the sentences §64 asks for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Edge-triggered, never level-triggered.</b> A process sitting above a threshold for a minute is
/// one thing that happened and not sixty, so the crossing is what fires and the process is
/// remembered until it drops back. Getting this wrong does not produce a slightly noisy program: it
/// produces one that scrolls its own status line so fast that nothing else on it can be read.
/// </para>
/// <para>
/// <b>A reading with no value is not a reading below the threshold.</b> An unpermitted or
/// not-yet-sampled rate neither fires the rule nor clears it — the process keeps whatever state it
/// had. Treating <c>default(Rate)</c> as nought here would fire "back below" for every process on
/// the first sample and again for every process the sampler could not read, which is the same
/// confident-zero defect §72.3 exists to prevent, arriving as an interruption instead of a cell.
/// </para>
/// <para>
/// Nothing here reads the machine. It is handed a snapshot and a delta that were taken anyway, which
/// is what makes the whole feature free until a rule names a service (PRD §5.4).
/// </para>
/// </remarks>
public sealed class NotificationWatch(NotificationRules rules) {

  private readonly NotificationRules _rules = rules ?? throw new ArgumentNullException(nameof(rules));
  private readonly Dictionary<ProcessKey, string> _names = [];
  private readonly HashSet<ProcessKey> _overCpu = [];
  private readonly HashSet<ProcessKey> _overMemory = [];
  private readonly HashSet<ProcessKey> _overDisk = [];
  private readonly Dictionary<string, ServiceState> _services = new(StringComparer.OrdinalIgnoreCase);
  private readonly AlertWatch _alerts = new(rules?.Alerts ?? []);

  /// <summary>The rules this watch was built with, for a front-end deciding whether to bother.</summary>
  public NotificationRules Rules => this._rules;

  /// <summary>
  /// What the user's own rules ask for between them (PRD §84).
  /// </summary>
  /// <remarks>
  /// A front-end asks this to decide whether to put a notice on the status line at all. The six
  /// ready-made rules of §64 always do both — they exist to interrupt somebody and to leave a record
  /// — so this is the union of the written rules' actions with what those imply, and it is
  /// <see cref="AlertAction.Both"/> for any file that used them.
  /// </remarks>
  public AlertAction Actions {
    get {
      var actions = this._alerts.Actions;
      return this._rules.ProcessStarted
        || this._rules.ProcessEnded
        || this._rules.Names.Count > 0
        || this._rules.CpuPercent.HasValue
        || this._rules.MemoryPercent.HasValue
        || this._rules.DiskBytesPerSecond.HasValue
        || this._rules.Services.Count > 0
        ? actions | AlertAction.Both
        : actions;
    }
  }

  /// <summary>
  /// Everything worth saying about the interval that just ended.
  /// </summary>
  /// <remarks>
  /// The first sample of a run says nothing whatever. Against no previous snapshot every process on
  /// the machine is "new" in the only sense available, and a program that announced three hundred
  /// process starts the moment it opened would have taught its reader to ignore it before they had
  /// finished reading the first one.
  /// </remarks>
  public IReadOnlyList<Notification> Examine(SystemSnapshot snapshot, SnapshotDelta delta) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    var found = new List<Notification>();
    if (!this._rules.Any)
      return found;

    var processes = snapshot.Processes;
    if (!delta.HasPrevious) {
      // The written rules see this sample too, so that a rule which needs a previous reading has one
      // next time round. Nothing of theirs fires on it either, for the same reason nothing here does.
      this._alerts.Examine(snapshot, delta);
      this.Remember(processes);
      return found;
    }

    foreach (var key in delta.Exited) {
      var name = this._names.TryGetValue(key, out var known) ? known : "a process";
      this._overCpu.Remove(key);
      this._overMemory.Remove(key);
      this._overDisk.Remove(key);
      if (this._rules.ProcessEnded)
        found.Add(new(NotificationKind.ProcessEnded, $"{name} (PID {key.Pid}) ended"));
    }

    for (var i = 0; i < processes.Length; ++i) {
      // Copied out rather than held by reference: what follows builds sentences in closures, and a
      // ref local cannot be captured by one.
      var key = processes[i].Key;
      var who = $"{processes[i].Name} (PID {key.Pid})";
      if (delta.IsNew(i)) {
        if (this._rules.ProcessStarted)
          found.Add(new(NotificationKind.ProcessStarted, $"{who} started"));

        if (this.IsNamed(processes[i].Name))
          found.Add(new(NotificationKind.NamedProcessStarted, $"{who} started, which is one you asked about"));
      }

      this.Cross(
        this._rules.CpuPercent, delta.CpuPercentPerCore(i), this._overCpu, key, found,
        NotificationKind.CpuAboveThreshold,
        value => $"{who} is using {value.ToString("0.#", CultureInfo.CurrentCulture)} % of a core"
      );

      this.Cross(
        this._rules.MemoryPercent, delta.MemoryPercent(i), this._overMemory, key, found,
        NotificationKind.MemoryAboveThreshold,
        value => $"{who} holds {value.ToString("0.#", CultureInfo.CurrentCulture)} % of this machine's memory"
      );

      this.Cross(
        this._rules.DiskBytesPerSecond, delta.IoTotalBytesPerSecond(i), this._overDisk, key, found,
        NotificationKind.DiskAboveThreshold,
        value => $"{who} is moving {Humanize.Bytes((ulong)Math.Max(0, value))} a second to and from disk"
      );
    }

    // Last, so that the six ready-made rules of §64 come out in front of the written ones: those are
    // one line and one number each and read at a glance, and §84's own are the sentences somebody
    // will want to stop and read.
    found.AddRange(this._alerts.Examine(snapshot, delta));

    this.Remember(processes);
    return found;
  }

  /// <summary>
  /// A unit a rule names that has stopped having processes (PRD §41, §64).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Handed the list rather than reading it, because reading it is a walk of two unit directories and
  /// the cgroup tree and is far too dear to do at the sample rate. A front-end calls this at whatever
  /// cadence it can afford, and only when a rule names a unit at all — naming one is the opt-in that
  /// pays for the walk (PRD §5.4).
  /// </para>
  /// <para>
  /// A unit whose state could not be determined is neither running nor stopped, and passing through
  /// <see cref="ServiceState.Unknown"/> fires nothing in either direction. Only a transition from
  /// <see cref="ServiceState.Running"/> to <see cref="ServiceState.Inactive"/> is a service having
  /// stopped; everything else is the reader being told something that did not happen.
  /// </para>
  /// </remarks>
  public IReadOnlyList<Notification> ExamineServices(IReadOnlyList<ServiceRecord> services) {
    ArgumentNullException.ThrowIfNull(services);

    var found = new List<Notification>();
    if (!this._rules.NeedsServices)
      return found;

    foreach (var service in services) {
      if (!this.IsWatched(service.Name))
        continue;

      var was = this._services.TryGetValue(service.Name, out var previous) ? previous : ServiceState.Unknown;
      this._services[service.Name] = service.State;
      if (was == ServiceState.Running && service.State == ServiceState.Inactive)
        found.Add(new(NotificationKind.ServiceStopped, $"{service.Name} has stopped"));
    }

    return found;
  }

  private void Cross(
    double? threshold,
    Rate reading,
    HashSet<ProcessKey> over,
    ProcessKey key,
    List<Notification> found,
    NotificationKind kind,
    Func<double, string> say
  ) {
    // Unknown leaves the state exactly as it was: it is neither a crossing nor a return.
    if (threshold is not { } limit || !reading.HasValue)
      return;

    if (reading.Value < limit) {
      over.Remove(key);
      return;
    }

    if (over.Add(key))
      found.Add(new(kind, say(reading.Value)));
  }

  private bool IsNamed(string name) {
    foreach (var wanted in this._rules.Names)
      if (string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase))
        return true;

    return false;
  }

  private bool IsWatched(string name) {
    foreach (var wanted in this._rules.Services)
      if (string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase))
        return true;

    return false;
  }

  /// <summary>
  /// The names of what is running now, so that what ends next time can be named rather than numbered.
  /// </summary>
  /// <remarks>
  /// A pid is not a name, and a process that has ended is the one case where the name cannot be
  /// looked up afterwards: it is gone, and the delta carries only its identity. Kept for exactly one
  /// interval and rebuilt each time, so a machine that has started and ended ten thousand processes
  /// holds ten thousand entries less than it would with a cache that only grew.
  /// </remarks>
  private void Remember(ReadOnlySpan<ProcessRecord> processes) {
    this._names.Clear();
    foreach (ref readonly var process in processes)
      this._names[process.Key] = process.Name;
  }

  /// <summary>Joins what happened into one line, for a front-end that has one line to put it on.</summary>
  /// <remarks>
  /// The count rather than the rest of the sentences when there are several, because a status line
  /// truncated mid-word says less than a status line that says how much it is not showing.
  /// </remarks>
  public static string Summarise(IReadOnlyList<Notification> notifications) {
    ArgumentNullException.ThrowIfNull(notifications);

    return notifications.Count switch {
      0 => string.Empty,
      1 => notifications[0].Text,
      _ => $"{notifications[0].Text} (and {(notifications.Count - 1).ToString(CultureInfo.InvariantCulture)} more)",
    };
  }

}
