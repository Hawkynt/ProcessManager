namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// One entry from the kernel's table of file locks (PRD §33, §91).
/// </summary>
/// <param name="Id">
/// The kernel's number for the lock. A holder and everybody queued behind it share it, which is what
/// makes the chain readable at all.
/// </param>
/// <param name="Blocked">
/// Whether this entry is somebody <em>waiting</em> for the lock rather than holding it. The kernel
/// marks a waiter with an arrow and gives it the holder's id.
/// </param>
/// <param name="Kind">
/// <c>POSIX</c>, <c>FLOCK</c>, <c>OFDLCK</c> or one of the lease kinds. Kept as the kernel's own
/// word rather than mapped onto a common vocabulary: a POSIX lock is owned by a process and an
/// open-file-description lock by a descriptor, and those behave differently across <c>fork</c> and
/// across a second <c>open</c> of the same file (PRD §5.3).
/// </param>
/// <param name="Exclusive">A write lock, which is the one that makes anybody else wait.</param>
/// <param name="Pid">Whose lock it is, or who is waiting for it.</param>
/// <param name="Device">The <c>major:minor</c> of the filesystem, as written.</param>
/// <param name="Inode">Which file, on that filesystem.</param>
public readonly record struct FileLock(
  int Id,
  bool Blocked,
  string Kind,
  bool Exclusive,
  int Pid,
  string Device,
  ulong Inode
);

/// <summary>
/// Who is waiting for a file lock, and who is holding it (PRD §33, §91).
/// </summary>
/// <remarks>
/// <para>
/// The one wait chain Linux states outright. A thread's wait channel says <em>what</em> it is
/// blocked in — <c>futex_wait_queue_me</c>, <c>flock_lock_inode_wait</c> — and stops there; the
/// kernel does not publish who holds a futex, and working it out from outside is not possible
/// without the debugger interface §4 rules out. File locks are different: <c>/proc/locks</c> lists
/// every waiter beside the holder it is queued behind, both by pid.
/// </para>
/// <para>
/// So this answers "why is this hanging" for the case it can answer it for, and says nothing about
/// the cases it cannot, rather than offering a general "wait chain" that is really one special case
/// wearing a general name (PRD §5.3).
/// </para>
/// <para>
/// In Core with no platform attribute, so the parse replays against a fixture on every CI leg. The
/// file is read in <c>ProcessManager.Platform.Linux</c>.
/// </para>
/// </remarks>
public static class FileLockParser {

  /// <summary>
  /// Reads the table.
  /// </summary>
  /// <remarks>
  /// A line the kernel writes in a shape this does not know is skipped rather than failing the read:
  /// the format has gained columns before — open-file-description locks arrived in 3.15 — and a
  /// parser that gives up on the whole file when one line surprises it turns a new kernel into a
  /// missing feature.
  /// </remarks>
  public static IReadOnlyList<FileLock> Parse(string contents) {
    var locks = new List<FileLock>();
    if (contents is not { Length: > 0 })
      return locks;

    foreach (var line in contents.Split('\n')) {
      if (Parse(line.AsSpan()) is { } entry)
        locks.Add(entry);
    }

    return locks;
  }

  private static FileLock? Parse(ReadOnlySpan<char> line) {
    line = line.Trim();
    if (line.IsEmpty)
      return null;

    var colon = line.IndexOf(':');
    if (colon <= 0 || !int.TryParse(line[..colon], out var id))
      return null;

    var rest = line[(colon + 1)..].TrimStart();

    // "-> " in place of the id's usual whitespace is how the kernel says this one is waiting. It is
    // the whole feature: the entry carries the id of the lock it is queued behind, so the holder is
    // the entry with the same id and no arrow.
    var blocked = rest.StartsWith("->", StringComparison.Ordinal);
    if (blocked)
      rest = rest[2..].TrimStart();

    // kind, mandatory-or-advisory, read-or-write, pid, device, start, end.
    Span<Range> fields = stackalloc Range[8];
    var count = Split(rest, fields);
    if (count < 5)
      return null;

    var kind = rest[fields[0]];
    var access = rest[fields[2]];
    if (!int.TryParse(rest[fields[3]], out var pid))
      return null;

    // major:minor:inode, and only the inode is a number this cares about. A device written in hex
    // stays as it was written — it is an identifier here, not an arithmetic quantity.
    var locator = rest[fields[4]];
    var lastColon = locator.LastIndexOf(':');
    if (lastColon <= 0 || !ulong.TryParse(locator[(lastColon + 1)..], out var inode))
      return null;

    return new(
      id,
      blocked,
      kind.ToString(),
      access.Equals("WRITE", StringComparison.Ordinal),
      pid,
      locator[..lastColon].ToString(),
      inode
    );
  }

  private static int Split(ReadOnlySpan<char> text, Span<Range> fields) {
    var count = 0;
    var start = 0;
    for (var i = 0; i <= text.Length && count < fields.Length; ++i) {
      if (i < text.Length && text[i] != ' ' && text[i] != '\t')
        continue;

      if (i > start)
        fields[count++] = new(start, i);

      start = i + 1;
    }

    return count;
  }

  /// <summary>
  /// Which process is blocking each waiting one, from a table that holds both.
  /// </summary>
  /// <remarks>
  /// <para>
  /// A pid may appear once as a waiter and the answer is one holder, because a lock has exactly one.
  /// It may also be waiting on several — a process queued behind two different files — and the first
  /// found is kept rather than the pair being reported as a set: what a reader wants is somebody to
  /// look at next, and a list of two is the same instruction twice.
  /// </para>
  /// <para>
  /// A waiter whose holder is not in the table is left out entirely rather than reported as blocked
  /// by nobody. That happens when the holder exits between the kernel writing the two lines, and
  /// "blocked by pid 0" would be a fact about a process that does not exist.
  /// </para>
  /// </remarks>
  public static IReadOnlyDictionary<int, int> BlockedBy(IReadOnlyList<FileLock> locks) {
    ArgumentNullException.ThrowIfNull(locks);

    var holders = new Dictionary<int, int>();
    foreach (var entry in locks)
      if (!entry.Blocked)
        holders.TryAdd(entry.Id, entry.Pid);

    var blocked = new Dictionary<int, int>();
    foreach (var entry in locks) {
      if (!entry.Blocked || !holders.TryGetValue(entry.Id, out var holder))
        continue;

      // A process cannot be waiting for a lock it is itself holding, and a table saying so would
      // send a reader to look at the process they were already looking at.
      if (holder != entry.Pid)
        blocked.TryAdd(entry.Pid, holder);
    }

    return blocked;
  }

}
