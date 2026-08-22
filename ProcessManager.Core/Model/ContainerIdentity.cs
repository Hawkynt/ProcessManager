namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Which container runtime a process is under, as far as its cgroup path says (PRD §38).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is nought so that a default-constructed identity is not one of the real
/// answers, and in particular is not <see cref="None"/>. "Nobody looked" and "this is not in a
/// container" are different statements, and the value nobody filled in must never turn out to be
/// the reassuring one (PRD §72.3).
/// </remarks>
public enum ContainerRuntime : byte {

  /// <summary>Nobody looked, or the path was not one this can read.</summary>
  Unknown = 0,

  /// <summary>
  /// The cgroup path names no container.
  /// </summary>
  /// <remarks>
  /// As far as the cgroup layout goes, and no further. A process isolated by namespaces alone — a
  /// <c>chroot</c>, an <c>unshare</c>, a sandbox that moves no cgroups — sits in an ordinary cgroup
  /// and reads as this. What it is confined by shows on the security page instead (PRD §36), and
  /// this is deliberately not the same claim as "not isolated".
  /// </remarks>
  None,

  Docker,

  Podman,

  /// <summary>containerd, whether under Kubernetes' CRI or <c>ctr</c> on its own.</summary>
  Containerd,

  CriO,

  Lxc,

  /// <summary>A container systemd itself registered — <c>systemd-nspawn</c>, or anything else that told <c>machined</c> about itself.</summary>
  SystemdNspawn,

  /// <summary>
  /// A virtual machine registered with <c>machined</c>, which shares the naming and is not a
  /// container at all.
  /// </summary>
  /// <remarks>
  /// libvirt registers a QEMU guest at <c>/machine.slice/machine-qemu\x2d1\x2dname.scope</c>, which
  /// is the same shape <c>systemd-nspawn</c> uses. Reporting a whole guest operating system as a
  /// container would be the false equivalence §5.3 forbids: the processes in it are the emulator's,
  /// not the guest's, and nothing inside the guest is on this machine's process list at all.
  /// </remarks>
  VirtualMachine,

}

/// <summary>
/// What is running a process, where something other than the machine itself is (PRD §38).
/// </summary>
/// <param name="Id">
/// The runtime's own id, shortened the way every one of those tools prints it. Null where the
/// layout carries a name instead of an id, which is what LXC and <c>machined</c> do.
/// </param>
/// <param name="Name">
/// What a person calls it, where the machine itself can say. Null when it cannot, in which case
/// <paramref name="NameReason"/> says why rather than leaving a blank cell (PRD §72.3).
/// </param>
/// <param name="NameReason">
/// Why there is no name. Only worth reading where <see cref="ContainerIdentity.IsContained"/> is
/// true: a process that is in no container has no container name, and that is not a hole in a
/// reading — it is the absence of the thing the reading would be about.
/// </param>
public readonly record struct ContainerIdentity(
  ContainerRuntime Runtime,
  string? Id,
  string? Name,
  UnknownReason NameReason
) {

  /// <summary>Nothing was looked at.</summary>
  public static ContainerIdentity Unknown { get; } = new(ContainerRuntime.Unknown, null, null, UnknownReason.NotImplementedHere);

  /// <summary>The path was read and names no container.</summary>
  public static ContainerIdentity None { get; } = new(ContainerRuntime.None, null, null, UnknownReason.NotSupportedOnPlatform);

  /// <summary>
  /// Whether the process is inside a container.
  /// </summary>
  /// <remarks>
  /// <see cref="ContainerRuntime.VirtualMachine"/> is deliberately not one. It is something, and it
  /// has a name, and it is not a container — a caller drawing a container column must not put a
  /// guest operating system in it (PRD §5.3). <see cref="IsIdentified"/> is the question "is this
  /// process somewhere other than the bare machine".
  /// </remarks>
  public bool IsContained
    => this.Runtime is not (ContainerRuntime.Unknown or ContainerRuntime.None or ContainerRuntime.VirtualMachine);

  /// <summary>Whether the path named anything at all — a container or a guest.</summary>
  public bool IsIdentified => this.Runtime is not (ContainerRuntime.Unknown or ContainerRuntime.None);

  /// <summary>What the runtime is called, in the words its own users use.</summary>
  public string RuntimeName => this.Runtime switch {
    ContainerRuntime.Docker => "Docker",
    ContainerRuntime.Podman => "Podman",
    ContainerRuntime.Containerd => "containerd",
    ContainerRuntime.CriO => "CRI-O",
    ContainerRuntime.Lxc => "LXC",
    ContainerRuntime.SystemdNspawn => "systemd-nspawn",
    ContainerRuntime.VirtualMachine => "a virtual machine, not a container",
    ContainerRuntime.None => "the machine itself",
    _ => "not known",
  };

}
