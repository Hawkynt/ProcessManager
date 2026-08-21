namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The service a cgroup path belongs to (PRD §40, §41).
/// </summary>
/// <remarks>
/// <para>
/// The kernel charges a socket to no service, so "owning service" is always the service of whoever
/// holds a descriptor on it — and on Linux that is read off the holder's cgroup, because a systemd
/// unit <em>is</em> a cgroup. <c>/system.slice/sshd.service</c> is sshd's, and nothing else needs
/// asking.
/// </para>
/// <para>
/// The innermost unit wins, and that is the whole subtlety. A desktop application sits at
/// <c>/user.slice/user-1000.slice/user@1000.service/app.slice/app-firefox.scope</c>, which contains
/// two units; naming the outer one would report every program a user has started as belonging to
/// their session manager.
/// </para>
/// <para>
/// A path with no unit in it answers null rather than guessing. The root cgroup, a v1 controller
/// path and a container runtime's own layout are all like that: they are real cgroups that are not
/// services, and the cgroup itself is reported separately for exactly that reason.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class CgroupUnit {

  /// <summary>
  /// The unit name in a cgroup path, or null when there is none.
  /// </summary>
  /// <remarks>
  /// A slice is deliberately not a unit for this purpose. It is one to systemd, but a slice holds no
  /// processes of its own — it only groups other units — so reporting <c>system.slice</c> as the
  /// owner of a socket would name a container rather than an owner.
  /// </remarks>
  public static string? Of(string? cgroupPath) {
    if (string.IsNullOrEmpty(cgroupPath))
      return null;

    var path = cgroupPath.AsSpan();
    while (!path.IsEmpty) {
      var slash = path.LastIndexOf('/');
      var segment = path[(slash + 1)..];
      if (IsUnit(segment))
        return new(segment);

      if (slash < 0)
        break;

      path = path[..slash];
    }

    return null;
  }

  /// <summary>
  /// The three suffixes that name something processes actually live in: a unit systemd started, a
  /// group of processes it adopted, and a socket-activated listener.
  /// </summary>
  private static bool IsUnit(ReadOnlySpan<char> segment)
    => segment.EndsWith(".service", StringComparison.Ordinal)
    || segment.EndsWith(".scope", StringComparison.Ordinal)
    || segment.EndsWith(".socket", StringComparison.Ordinal);

}
