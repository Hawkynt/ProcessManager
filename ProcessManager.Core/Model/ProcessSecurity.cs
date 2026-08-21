namespace Hawkynt.ProcessManager.Model;

/// <summary>A group a process is in, by number and — where the machine's own file says so — by name.</summary>
/// <param name="Name">
/// Null where the number is in no local group file. Which is a real situation rather than a failure:
/// a group that comes from LDAP or SSSD is not in <c>/etc/group</c>, and a container's process is in
/// groups that belong to the container's own file and not to this machine's.
/// </param>
public readonly record struct GroupIdentity(int Id, string? Name);

/// <summary>
/// What confines a process, beyond the identity every sample already carries (PRD §36).
/// </summary>
/// <remarks>
/// <para>
/// The uids, the gids, the five capability sets, the seccomp mode and the no-new-privileges flag are
/// all in <c>/proc/[pid]/status</c>, which the sampler reads for every process every second — so they
/// are fields of the process record and are not repeated here. This is the remainder: the two things
/// that cost an extra read per process and are therefore collected for the one process somebody is
/// looking at (PRD §5.4).
/// </para>
/// <para>
/// Null from a probe means the platform has no such notion or the process has gone. It never means
/// "nothing confines this process" — an unconfined process is a <see cref="Label"/> saying so.
/// </para>
/// </remarks>
/// <param name="Label">
/// The LSM label: an SELinux context on one machine, an AppArmor profile on another. <c>unconfined</c>
/// is what AppArmor writes when no profile applies and is kept verbatim, because it is an answer.
/// </param>
/// <param name="LabelReason">
/// Why there is no label. A kernel with no security module loaded fails the read outright rather than
/// leaving the file empty, so "this machine confines nothing" and "we were not allowed to look" are
/// different answers and are reported as different answers (PRD §3.4).
/// </param>
/// <param name="SupplementaryGroups">
/// The groups beyond the process's own gid, which is where most of what a process may open actually
/// comes from. Empty is a real answer — a kernel thread is in none.
/// </param>
public sealed record ProcessSecurity(
  string? Label,
  UnknownReason LabelReason,
  IReadOnlyList<GroupIdentity> SupplementaryGroups,
  UnknownReason GroupsReason
);
