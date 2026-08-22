namespace Hawkynt.ProcessManager.Model;

/// <summary>Whether a service is running right now.</summary>
public enum ServiceState : byte {

  /// <summary>Could not be determined.</summary>
  Unknown = 0,

  /// <summary>It has processes.</summary>
  Running,

  /// <summary>It has none, and the manager holds no invocation of it either.</summary>
  Inactive,

  /// <summary>
  /// The manager holds a current invocation of it and it has no processes.
  /// </summary>
  /// <remarks>
  /// What a finished <c>Type=oneshot</c> unit with <c>RemainAfterExit=yes</c> looks like: systemd
  /// counts it active because the thing it set up is still set up, and there is nothing in a cgroup to
  /// find. Reported as its own state rather than folded into <see cref="Inactive"/>, which is what
  /// this reader used to call it — a mount that succeeded and a mount that never ran are not the same
  /// answer.
  /// </remarks>
  Active,

}

/// <summary>
/// Whether the manager could make sense of the unit at all, which is systemd's own <c>LoadState</c>.
/// </summary>
/// <remarks>
/// The whole of it is decidable from disk, which is why it is here and the sub-state below is only
/// half here: a unit is loaded when there is a file, masked when that file is a symlink to
/// <c>/dev/null</c>, and transient when the cgroup tree names a unit that no file on this machine
/// describes.
/// </remarks>
public enum ServiceLoadState : byte {

  /// <summary>Not worked out.</summary>
  Unknown = 0,

  /// <summary>There is a unit file and it was read.</summary>
  Loaded,

  /// <summary>Its file is a symlink to <c>/dev/null</c>. It can never run.</summary>
  Masked,

  /// <summary>
  /// It is running and no file on disk describes it — a unit systemd made at runtime.
  /// </summary>
  Transient,

}

/// <summary>
/// The finer state under <see cref="ServiceState"/>, as far as the files answer it (PRD §41).
/// </summary>
/// <remarks>
/// Three of systemd's own sub-states and not all of them. <c>failed</c>, <c>auto-restart</c>,
/// <c>start-pre</c> and the rest are the manager's account of what it is doing, kept in its own
/// memory and written to no file — a unit in any of them looks like <see cref="Dead"/> from here, and
/// this enum says only what it can see rather than guessing which.
/// </remarks>
public enum ServiceSubState : byte {

  /// <summary>Not worked out.</summary>
  Unknown = 0,

  /// <summary>It has processes.</summary>
  Running,

  /// <summary>An invocation is current and there are no processes left in it.</summary>
  Exited,

  /// <summary>The manager holds no invocation of it.</summary>
  Dead,

}

/// <summary>How one unit is tied to another (PRD §41).</summary>
/// <remarks>
/// systemd's own words, not translations of them. <c>Wants</c> and <c>Requires</c> differ in what
/// happens when the other unit fails, and a single word like "depends on" would lose exactly that.
/// </remarks>
public enum UnitRelation : byte {
  Requires,
  Requisite,
  Wants,
  BindsTo,
  PartOf,
  Upholds,
  Conflicts,
  After,
  Before,
}

/// <summary>
/// One edge of the dependency graph: this unit, that unit, and in which sense (PRD §41).
/// </summary>
/// <param name="Source">
/// Where the edge was found — the unit file itself, or the <c>.wants</c> / <c>.requires</c> directory
/// a package or an administrator dropped a symlink into. Worth carrying because the two are changed
/// in completely different ways, and somebody wanting to remove a dependency has to know which.
/// </param>
public readonly record struct UnitDependency(UnitRelation Relation, string Unit, string Source);

/// <summary>
/// One service — a systemd unit on Linux, an SCM service on Windows (PRD §41).
/// </summary>
/// <param name="Description">The unit's own one-line description, which is its display name.</param>
/// <param name="Enabled">
/// Whether it starts at boot. <see langword="null"/> when that cannot be told — a unit started only
/// by a socket or a timer is neither enabled nor disabled in the sense the column means.
/// </param>
/// <param name="Masked">
/// Masked units can never run, whatever else is configured. A different state from disabled, and one
/// people forget they set.
/// </param>
/// <param name="MainPid">The first process in the unit's cgroup, or 0 when it has none.</param>
/// <param name="Path">The unit file this came from, which is what "open configuration" opens.</param>
/// <remarks>
/// The nine that identify a unit are constructor parameters and the rest are initialisers, which is
/// not tidiness: every one of the initialisers below starts at a value that means "not read", so a
/// record built by a probe that knows less than this one does not claim a blank as an answer
/// (PRD §72.3).
/// </remarks>
public readonly record struct ServiceRecord(
  string Name,
  string? Description,
  ServiceState State,
  bool? Enabled,
  bool Masked,
  int MainPid,
  string? Command,
  string Path,
  string? RestartPolicy
) {

  /// <summary>Whether the manager can make sense of the unit — systemd's <c>LoadState</c>.</summary>
  public ServiceLoadState LoadState { get; init; }

  /// <summary>The finer state, as far as the files answer it.</summary>
  public ServiceSubState SubState { get; init; }

  /// <summary>
  /// What kind of service it is: <c>simple</c>, <c>oneshot</c>, <c>notify</c> and the rest.
  /// </summary>
  /// <remarks>
  /// Never null for a unit that was read: where the file states no <c>Type=</c>, this carries the
  /// default systemd would apply rather than a blank, because the default is a fact about the unit and
  /// not an absence of one.
  /// </remarks>
  public string? Type { get; init; }

  /// <summary>
  /// The account the service runs as.
  /// </summary>
  /// <remarks>
  /// Null where the unit file names none, which for a system unit means <c>root</c>. Left as null
  /// rather than filled in with "root" so the difference between "it says root" and "it says nothing,
  /// and the manager's default is root" survives to whoever renders it (PRD §5.3).
  /// </remarks>
  public string? Account { get; init; }

  /// <summary>The program <see cref="Command"/> starts, without its arguments or its prefixes.</summary>
  public string? Executable { get; init; }

  /// <summary>What is passed to <see cref="Executable"/>, as the unit file wrote it.</summary>
  public string? Arguments { get; init; }

  /// <summary>
  /// The special characters in front of <c>ExecStart=</c>, if any — <c>-</c>, <c>@</c>, <c>+</c>,
  /// <c>!</c>, <c>:</c>.
  /// </summary>
  /// <remarks>
  /// Not decoration. A leading <c>-</c> means the manager ignores a non-zero exit, which is the
  /// difference between a unit that reports a failure and one that quietly does not.
  /// </remarks>
  public string? CommandPrefixes { get; init; }

  /// <summary>
  /// When the manager's current invocation of this unit began, in UTC ticks.
  /// </summary>
  /// <remarks>
  /// From systemd's own runtime directory — the <c>invocation:</c> symlink it writes under
  /// <c>/run/systemd/units</c> when a unit starts — and so from a file rather than from D-Bus. No
  /// value where the unit has no current invocation, and <see cref="UnknownReason.NotSupportedOnPlatform"/>
  /// where that directory is not there at all, which are two different facts and must not become one.
  /// </remarks>
  public Counter ActivatedUtcTicks { get; init; } = Counter.NotSupported;

  /// <summary>What this unit is tied to.</summary>
  public IReadOnlyList<UnitDependency> Dependencies { get; init; } = [];

  /// <summary>What is tied to this unit — the same edges, walked the other way.</summary>
  public IReadOnlyList<UnitDependency> Dependents { get; init; } = [];

}
