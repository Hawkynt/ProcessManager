using System.Globalization;
using System.Text;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a Linux capability mask actually grants, by name (PRD §21).
/// </summary>
/// <remarks>
/// <para>
/// <c>/proc/[pid]/status</c> writes five of these — inheritable, permitted, effective, bounding and
/// ambient — as sixteen hex digits each. A column showing <c>0x000001ffffffffff</c> says nothing a
/// reader can act on: the question a security field exists to answer is "may this process load a
/// kernel module", and the answer is a name, not a bit pattern. The raw mask stays available for the
/// reader who wants to paste it into <c>capsh</c>.
/// </para>
/// <para>
/// The bit numbers are the kernel's <c>CAP_*</c> constants from <c>uapi/linux/capability.h</c>. They
/// are an ABI — a numbered capability cannot be renumbered without breaking every setcap binary on
/// every disk — so the table can only ever gain entries, which is what makes vendoring the header
/// beside the tests worthwhile: the check is that every bit named here is the bit the kernel names,
/// not that the list is exhaustive.
/// </para>
/// <para>
/// In Core with no platform attribute, so the decoding runs on every CI leg against a recorded
/// <c>status</c> rather than only on Linux (PRD §9.2).
/// </para>
/// </remarks>
public static class LinuxCapabilities {

  /// <summary>
  /// The kernel's name for each bit, indexed by the bit itself, lowercased the way every userspace
  /// tool prints it.
  /// </summary>
  /// <remarks>
  /// Lowercase because that is the spelling of the whole ecosystem: <c>capsh --decode</c>,
  /// <c>getcap</c>, <c>setcap</c>, systemd's <c>CapabilityBoundingSet=</c> and the container runtimes
  /// all write <c>cap_net_admin</c>. Printing <c>CAP_NET_ADMIN</c> would be the header's spelling and
  /// nobody else's, and a reader copying a name out of this column into a unit file would have to
  /// translate it first.
  /// </remarks>
  private static readonly string[] _Names = [
    "cap_chown",
    "cap_dac_override",
    "cap_dac_read_search",
    "cap_fowner",
    "cap_fsetid",
    "cap_kill",
    "cap_setgid",
    "cap_setuid",
    "cap_setpcap",
    "cap_linux_immutable",
    "cap_net_bind_service",
    "cap_net_broadcast",
    "cap_net_admin",
    "cap_net_raw",
    "cap_ipc_lock",
    "cap_ipc_owner",
    "cap_sys_module",
    "cap_sys_rawio",
    "cap_sys_chroot",
    "cap_sys_ptrace",
    "cap_sys_pacct",
    "cap_sys_admin",
    "cap_sys_boot",
    "cap_sys_nice",
    "cap_sys_resource",
    "cap_sys_time",
    "cap_sys_tty_config",
    "cap_mknod",
    "cap_lease",
    "cap_audit_write",
    "cap_audit_control",
    "cap_setfcap",
    "cap_mac_override",
    "cap_mac_admin",
    "cap_syslog",
    "cap_wake_alarm",
    "cap_block_suspend",
    "cap_audit_read",
    "cap_perfmon",
    "cap_bpf",
    "cap_checkpoint_restore",
  ];

  /// <summary>The highest bit this table names — the kernel's <c>CAP_LAST_CAP</c> as of 6.x.</summary>
  public static int HighestNamedBit => _Names.Length - 1;

  /// <summary>
  /// Every bit this table names, which is what the kernel calls <c>cap_full_set</c>.
  /// </summary>
  /// <remarks>
  /// Derived from the table rather than written out, so adding the next capability cannot leave a
  /// constant behind claiming the old ceiling.
  /// </remarks>
  public static ulong FullSet { get; } = _Names.Length >= 64 ? ulong.MaxValue : (1ul << _Names.Length) - 1;

  /// <summary>
  /// The kernel's name for one bit, or <see langword="null"/> for a bit no released kernel had when
  /// this was written.
  /// </summary>
  public static string? Name(int bit) => (uint)bit < (uint)_Names.Length ? _Names[bit] : null;

  /// <summary>
  /// The same table the tests hold against the vendored header, so a transcription slip in a
  /// forty-one entry list fails the build rather than mislabelling a privilege.
  /// </summary>
  public static IReadOnlyList<string> KernelNames => _Names;

  /// <summary>Every capability the mask grants, in bit order.</summary>
  public static IReadOnlyList<string> Decode(ulong mask) {
    var names = new List<string>();
    for (var bit = 0; bit < 64; ++bit) {
      if ((mask & (1ul << bit)) == 0)
        continue;

      names.Add(Name(bit) ?? bit.ToString(CultureInfo.InvariantCulture));
    }

    return names;
  }

  /// <summary>
  /// The mask as one line for a column: <c>none</c>, <c>all</c>, or the names separated by commas.
  /// </summary>
  /// <remarks>
  /// <c>all</c> rather than forty-one names for a root process, because that is what most rows on
  /// most machines are and a column truncated at <c>cap_chown,cap_d…</c> would tell a reader less
  /// than nothing. It means exactly <see cref="FullSet"/> — a process holding every capability but
  /// one is listed in full, because that one is the interesting fact about it.
  /// <para>
  /// Filters go through <see cref="List"/> instead, which never abbreviates: searching for
  /// <c>cap_sys_module</c> must not miss the processes that hold it precisely because they hold
  /// everything.
  /// </para>
  /// </remarks>
  public static string Describe(ulong mask) => mask == FullSet ? "all" : List(mask);

  /// <summary>
  /// Every capability the mask grants, spelled out, however many there are.
  /// </summary>
  /// <remarks>
  /// A bit the table does not name is printed as its number, which is what <c>capsh --decode</c>
  /// does with the same input. Dropping it would quietly under-report a privilege on a kernel newer
  /// than this build, and downwards is the one direction a security field must never round.
  /// </remarks>
  public static string List(ulong mask) {
    if (mask == 0)
      return "none";

    var text = new StringBuilder();
    for (var bit = 0; bit < 64; ++bit) {
      if ((mask & (1ul << bit)) == 0)
        continue;

      if (text.Length > 0)
        text.Append(',');

      if (Name(bit) is { } name)
        text.Append(name);
      else
        text.Append(bit.ToString(CultureInfo.InvariantCulture));
    }

    return text.ToString();
  }

  /// <summary>The mask the way <c>capsh</c> and the kernel's own documentation write it.</summary>
  public static string Hex(ulong mask) => "0x" + mask.ToString("x16", CultureInfo.InvariantCulture);

}
