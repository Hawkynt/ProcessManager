using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// The two reads that answer a page rather than a column: a process's address space (PRD §34) and
/// what confines it (PRD §36).
/// </summary>
/// <remarks>
/// Apart from the rest of the probe because they are the opposite discipline. Everything in
/// <c>LinuxProbe.cs</c> runs for six hundred processes a second and is written to allocate nothing;
/// these two run for the one process somebody has opened a window on, are allowed to allocate, and
/// would be indefensible on a tick — a page table walk and two extra files each (PRD §5.4).
/// </remarks>
public sealed partial class LinuxProbe {

  /// <summary>gid → name, for the security page. Loaded on the first group anybody asks about.</summary>
  private GroupNameResolver? _groups;

  /// <summary>
  /// Every mapping in a process's address space (PRD §34).
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>smaps</c> first and <c>maps</c> as the fallback, which is the same order the module list uses
  /// and for the same reason: the two files have the same header line, and only the first carries the
  /// per-mapping counters. The fallback is not a preference — it is what happens when the kernel
  /// refuses the expensive one, and the reason it refused becomes the reason each counter carries, so
  /// that a page can say why a column is empty instead of showing a column of noughts (PRD §3.4).
  /// </para>
  /// <para>
  /// Both files look world-readable and are not. <c>/proc/[pid]/maps</c> is mode 0444, and reading
  /// another user's still fails with <c>EPERM</c>, because the check the kernel runs is
  /// <c>ptrace_may_access</c> at <c>read</c> rather than the mode bits at <c>open</c> — which is why
  /// the refusal has to be reported as a refusal and not as a process with no memory.
  /// </para>
  /// </remarks>
  public MemoryMapReading GetMemoryRegions(ProcessKey key) {
    if (this._reader.TryReadWhole($"{this._procRoot}/{key.Pid}/smaps", out var content, out var smapsErrno))
      // A header line with no counter block behind it does not happen on a kernel that produced the
      // header at all, so this reason is a statement about a kernel we have never met rather than
      // about this one.
      return new(MemoryMapState.Available, true, MemoryMap.Collect(content, Counter.NotSupported));

    var refused = smapsErrno is Native.EACCES or Native.EPERM;
    if (this._reader.TryReadWhole($"{this._procRoot}/{key.Pid}/maps", out content, out var mapsErrno))
      // The addresses without the counters. Which happens two ways round: refused the page-table walk
      // but allowed the addresses — a process that has just been entered by a setuid program is one —
      // or a kernel built without CONFIG_PROC_PAGE_MONITOR, which has no smaps at all.
      return new(
        MemoryMapState.Available,
        false,
        MemoryMap.Collect(content, refused ? Counter.NotPermitted : Counter.NotSupported)
      );

    return new(
      mapsErrno is Native.EACCES or Native.EPERM ? MemoryMapState.NotPermitted : MemoryMapState.Gone,
      false,
      []
    );
  }

  /// <summary>
  /// What confines a process, beyond the identity every sample already carries (PRD §36).
  /// </summary>
  /// <remarks>
  /// The two halves are read from two files and fail independently, which is why they carry a reason
  /// each: <c>status</c> is readable for anybody's process and <c>attr/current</c> is not necessarily,
  /// so a machine can perfectly well answer the group list and refuse the label.
  /// </remarks>
  public ProcessSecurity? DescribeSecurity(ProcessKey key) {
    var label = this.ReadLabel(key, out var labelReason);

    // Second, because the label read invalidates the reader's buffer and this is the one whose result
    // has to survive the call.
    if (!this._reader.TryRead($"{this._procRoot}/{key.Pid}/status", out var content, out var errno))
      // Not a security answer at all: status is readable for every live process on the machine, so
      // failing it means the process is no longer there.
      return errno is Native.EACCES or Native.EPERM
        ? new(label, labelReason, [], UnknownReason.NotPermitted)
        : null;

    var groups = new List<GroupIdentity>();
    var found = false;
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty) {
      var line = scanner.NextLine();
      if (!AsciiScanner.StartsWith(line, "Groups:"u8))
        continue;

      found = true;
      var numbers = new AsciiScanner(line["Groups:"u8.Length..]);
      while (!numbers.IsEmpty) {
        var field = numbers.NextField();
        if (field.IsEmpty)
          break;

        var gid = (int)AsciiScanner.ParseUInt64(field);
        groups.Add(new(gid, (this._groups ??= new(this._options.GroupPath)).Resolve(gid)));
      }

      break;
    }

    // An empty list with a reason of None is a process in no supplementary group, which every kernel
    // thread is; an empty list without the line is a kernel that did not write one, and this build
    // has never met one. The two say different things and are not merged (PRD §3.4).
    return new(label, labelReason, groups, found ? UnknownReason.None : UnknownReason.NotSupportedOnPlatform);
  }

  /// <summary>
  /// The LSM label from <c>attr/current</c> — an SELinux context, an AppArmor profile, or nothing.
  /// </summary>
  /// <remarks>
  /// A kernel with no security module loaded fails this read with <c>EINVAL</c> rather than producing
  /// an empty file, so "this machine confines nothing" arrives as an error and must not be reported
  /// as a refusal (PRD §72.3).
  /// </remarks>
  private string? ReadLabel(ProcessKey key, out UnknownReason reason) {
    if (!this._reader.TryRead($"{this._procRoot}/{key.Pid}/attr/current", out var content, out var errno)) {
      reason = errno is Native.EACCES or Native.EPERM
        ? UnknownReason.NotPermitted
        : UnknownReason.NotSupportedOnPlatform;

      return null;
    }

    // The file is NUL-terminated and often has a trailing newline; both would end up on the page.
    var end = content.IndexOf((byte)0);
    if (end >= 0)
      content = content[..end];

    content = content.TrimEnd((byte)'\n');
    if (content.IsEmpty) {
      reason = UnknownReason.NotSupportedOnPlatform;
      return null;
    }

    reason = UnknownReason.None;
    // "unconfined" is what AppArmor says when no profile applies. It is a real answer, not a missing
    // one, so it is kept rather than blanked.
    return Encoding.UTF8.GetString(content);
  }

}
