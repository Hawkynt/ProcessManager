namespace Hawkynt.ProcessManager.Model;

/// <summary>Whether a service is running right now.</summary>
public enum ServiceState : byte {

  /// <summary>Could not be determined.</summary>
  Unknown = 0,

  /// <summary>It has processes.</summary>
  Running,

  /// <summary>It has none. Either it has not been started or it has finished.</summary>
  Inactive,

}

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
);
