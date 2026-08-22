using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Which container runtime a process is under, and what its container is called (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// From the cgroup path and from nothing else, for the same reason
/// <see cref="RuntimeDetector"/> works from the module list: a process called <c>containerd-shim</c>
/// is not a container, and a container's own processes are named after whatever they run. What
/// cannot be argued with is which cgroup the kernel put them in, because every one of these runtimes
/// creates a cgroup for a container and puts its processes there. That is a fact about the machine
/// rather than a guess about a name.
/// </para>
/// <para>
/// <b>The name is only reported where the machine itself knows it.</b> LXC and <c>machined</c> put
/// the container's name in the path, so the path is the answer. Docker, Podman, containerd and CRI-O
/// put an id there and keep the name in their own daemon, which is reachable only over that daemon's
/// socket — membership of which is, on every distribution that ships one, equivalent to root. So the
/// id is reported and the name says why it is missing, rather than the program acquiring a
/// privileged dependency to fill in a column (PRD §5.4, §32).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class ContainerDetector {

  /// <summary>
  /// Reads a cgroup path as it appears in <c>/proc/[pid]/cgroup</c>.
  /// </summary>
  /// <remarks>
  /// Innermost segment first, because that is where the container is: a container under Kubernetes
  /// sits several slices deep and every level above it belongs to the orchestrator rather than to the
  /// container. The same rule <see cref="CgroupUnit"/> follows, and for the same reason.
  /// </remarks>
  public static ContainerIdentity Of(string? cgroupPath) {
    if (cgroupPath is not { Length: > 0 })
      return ContainerIdentity.Unknown;

    // machined's layout is the one that cannot be told from an ordinary unit by its own name:
    // machine-web.scope is a container and machine-learning.service is a program somebody wrote.
    // What separates them is where they live, so the slice is looked for once rather than guessed
    // at per segment.
    var machined = cgroupPath.Contains("/machine.slice", StringComparison.Ordinal);

    var path = cgroupPath.AsSpan();
    while (!path.IsEmpty) {
      var slash = path.LastIndexOf('/');
      if (Segment(path[(slash + 1)..], machined) is { } found)
        return found;

      if (slash < 0)
        break;

      path = path[..slash];
    }

    // Two layouts put the id in a segment of its own under a runtime's name rather than in a
    // decorated scope: docker's own default is /docker/<id>, and an unqualified /lxc/<name> is how
    // an LXC container looks where nothing registered it with systemd.
    if (Bare(cgroupPath) is { } bare)
      return bare;

    // A path with a container id in it and no runtime naming it is still a container, and saying
    // "this is not in one" about it would be a positive false statement rather than an unknown.
    // Kubernetes with the cgroupfs driver writes exactly that — /kubepods/…/<64 hex> — and this is
    // also what keeps the answer in step with the process table's own container column, which finds
    // the id the same way and would otherwise have shown one where this said there was none (§5.1).
    return Humanize.ContainerId(cgroupPath) is { Length: > 0 } id
      ? new(ContainerRuntime.Container, id, null, UnknownReason.NotImplementedHere)
      : ContainerIdentity.None;
  }

  /// <summary>One path segment, where it names a container by itself.</summary>
  private static ContainerIdentity? Segment(ReadOnlySpan<char> segment, bool machined) {
    // Ordered longest prefix first: "cri-containerd-" would otherwise never be reached, because
    // nothing distinguishes it from a plain containerd id until the whole prefix has been compared.
    if (Prefixed(segment, "cri-containerd-", out var id))
      return Identified(ContainerRuntime.Containerd, id);

    if (Prefixed(segment, "containerd-", out id))
      return Identified(ContainerRuntime.Containerd, id);

    if (Prefixed(segment, "libpod-", out id))
      return Identified(ContainerRuntime.Podman, id);

    if (Prefixed(segment, "crio-", out id))
      return Identified(ContainerRuntime.CriO, id);

    if (Prefixed(segment, "docker-", out id))
      return Identified(ContainerRuntime.Docker, id);

    // LXC's systemd-managed layout: lxc.payload.<name>, where the name is the name and not an id.
    if (Prefixed(segment, "lxc.payload.", out var payload))
      return new(ContainerRuntime.Lxc, null, Trimmed(payload).ToString(), UnknownReason.None);

    // machined's, which nspawn and libvirt share. Only under machine.slice and only as a scope,
    // because this is the one arm with no id to check against: "machine-learning.service" is a
    // program somebody wrote and would otherwise be reported as a container called "learning". The
    // name is escaped the way systemd escapes every unit name, so it has to be unescaped before it
    // is shown — a container called "web-1" is "machine-web\x2d1.scope" on disk, and printing that
    // verbatim shows somebody a name nothing on their machine is called.
    if (machined
        && segment.EndsWith(".scope", StringComparison.Ordinal)
        && Prefixed(segment, "machine-", out var machine)) {
      var name = Unescape(Trimmed(machine));

      // libvirt names its QEMU guests "qemu-<id>-<name>". A guest is not a container: none of its
      // processes are on this machine's list, and calling it one would say the opposite (PRD §5.3).
      return name.StartsWith("qemu-", StringComparison.Ordinal)
        ? new(ContainerRuntime.VirtualMachine, null, name, UnknownReason.None)
        : new(ContainerRuntime.SystemdNspawn, null, name, UnknownReason.None);
    }

    return null;
  }

  /// <summary>
  /// The two layouts that name the runtime in one segment and the container in the next.
  /// </summary>
  /// <remarks>
  /// <c>/docker/&lt;id&gt;</c> is what a machine running dockerd without systemd's cgroup driver
  /// looks like, and <c>/lxc/&lt;name&gt;</c> is LXC's own. Both are matched at the front of the path
  /// rather than anywhere in it, so a user's own cgroup called <c>docker</c> deeper down is not read
  /// as one.
  /// </remarks>
  private static ContainerIdentity? Bare(string cgroupPath) {
    var path = cgroupPath.AsSpan().TrimStart('/');
    var slash = path.IndexOf('/');
    if (slash <= 0)
      return null;

    var head = path[..slash];
    var rest = path[(slash + 1)..];
    var next = rest.IndexOf('/');
    var body = next < 0 ? rest : rest[..next];
    if (body.IsEmpty)
      return null;

    if (head.SequenceEqual("docker"))
      return Identified(ContainerRuntime.Docker, body);

    // "lxc" is LXC's own; "lxc.payload" is LXD's, which puts the name in the next segment rather
    // than in the same one the way the systemd-managed layout does.
    return head.SequenceEqual("lxc") || head.SequenceEqual("lxc.payload")
      ? new(ContainerRuntime.Lxc, null, body.ToString(), UnknownReason.None)
      : null;
  }

  /// <summary>
  /// A container the machine knows only by its id.
  /// </summary>
  /// <remarks>
  /// The id is shortened to the twelve characters every one of these tools prints, which is what
  /// <see cref="Humanize.ContainerId"/> shows in the process table — the two must agree, or one
  /// window says a process is in <c>3f2a91c4e07b</c> and the next says it is in something else.
  /// </remarks>
  private static ContainerIdentity? Identified(ContainerRuntime runtime, ReadOnlySpan<char> id) {
    var trimmed = Trimmed(id);

    // A run of hex long enough to be an id. A systemd scope called "docker-cleanup.scope" is a unit
    // somebody wrote and not a container, and reporting it as one would put a container column on
    // half of system.slice.
    if (trimmed.Length < 32)
      return null;

    foreach (var character in trimmed)
      if (!Uri.IsHexDigit(character))
        return null;

    return new(runtime, trimmed[..12].ToString(), null, UnknownReason.NotImplementedHere);
  }

  /// <summary>The segment without whichever unit suffix it carries.</summary>
  private static ReadOnlySpan<char> Trimmed(ReadOnlySpan<char> segment) {
    foreach (var suffix in (ReadOnlySpan<string>)[".scope", ".service", ".slice"])
      if (segment.EndsWith(suffix, StringComparison.Ordinal))
        return segment[..^suffix.Length];

    return segment;
  }

  private static bool Prefixed(ReadOnlySpan<char> segment, ReadOnlySpan<char> prefix, out ReadOnlySpan<char> rest) {
    if (segment.Length > prefix.Length && segment[..prefix.Length].SequenceEqual(prefix)) {
      rest = segment[prefix.Length..];
      return true;
    }

    rest = default;
    return false;
  }

  /// <summary>
  /// systemd's unit-name escaping, undone.
  /// </summary>
  /// <remarks>
  /// A unit name may hold only a restricted alphabet, so everything else is written <c>\xNN</c> —
  /// a hyphen in a machine's name becomes <c>\x2d</c>, because a hyphen in a unit name already means
  /// a path separator. A sequence that is not a complete <c>\xNN</c> is left exactly as it was
  /// rather than dropped: it is then not an escape, and a name is not improved by silently losing
  /// characters out of it.
  /// </remarks>
  public static string Unescape(ReadOnlySpan<char> escaped) {
    if (escaped.IndexOf('\\') < 0)
      return escaped.ToString();

    var builder = new StringBuilder(escaped.Length);
    for (var i = 0; i < escaped.Length; ++i) {
      // AllowHexSpecifier and not HexNumber: the latter permits leading and trailing whitespace, so
      // "\x 5" would have parsed as five. And an escape that decodes to a control character is left
      // as written — this name reaches a report, a table cell and the clipboard, and a name with a
      // newline in the middle of it is a name that breaks whatever is showing it.
      if (escaped[i] == '\\'
          && i + 3 < escaped.Length
          && escaped[i + 1] is 'x' or 'X'
          && Uri.IsHexDigit(escaped[i + 2])
          && Uri.IsHexDigit(escaped[i + 3])
          && byte.TryParse(escaped.Slice(i + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
          && value is >= 0x20 and not 0x7F) {
        builder.Append((char)value);
        i += 3;
        continue;
      }

      builder.Append(escaped[i]);
    }

    return builder.ToString();
  }

}
