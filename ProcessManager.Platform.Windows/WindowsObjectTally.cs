using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// How many objects of each kind one process holds a handle on (PRD §20).
/// </summary>
/// <remarks>
/// Five counts rather than one, because they answer different questions: a service holding ten
/// thousand registry keys and one holding ten thousand sections both show a large handle count and
/// have nothing else in common.
/// </remarks>
internal record struct ObjectTally(uint Events, uint Semaphores, uint Mutexes, uint Sections, uint Keys);

/// <summary>
/// Which type index the kernel has given each of the object types this program counts.
/// </summary>
/// <remarks>
/// The indices are the running kernel's and are <em>not</em> constants: they depend on the order the
/// object types were created in during boot, which depends on which drivers loaded. Anything that
/// hard-codes them is wrong on the next machine. They are therefore discovered by asking one handle
/// of each index what its type is called, and passed in here — which is also what lets the tally
/// itself be tested on a machine with no handle table at all (PRD §9.4).
/// </remarks>
/// <param name="Unknown">
/// The value that means "no index was discovered for this type", chosen so that no real index can
/// collide with it: the field in the table is sixteen bits, so every real index is below this.
/// </param>
internal readonly record struct ObjectTypeIndices(
  ushort Event,
  ushort Semaphore,
  ushort Mutant,
  ushort Section,
  ushort Key
) {

  public const ushort Unknown = ushort.MaxValue;

  public static readonly ObjectTypeIndices None = new(Unknown, Unknown, Unknown, Unknown, Unknown);

  /// <summary>The same set with one index filled in, by the kernel's own name for the type.</summary>
  /// <remarks>
  /// "Mutant" is the kernel's word for what user mode calls a mutex, and "Key" is a registry key.
  /// Both are the strings <c>NtQueryObject</c> returns, so they are matched rather than translated.
  /// </remarks>
  public ObjectTypeIndices With(string? typeName, ushort index) => typeName switch {
    "Event" => this with { Event = index },
    "Semaphore" => this with { Semaphore = index },
    "Mutant" => this with { Mutant = index },
    "Section" => this with { Section = index },
    "Key" => this with { Key = index },
    _ => this,
  };

  /// <summary>Whether anything at all was discovered, so a caller can tell an empty map from a full one.</summary>
  public bool IsEmpty => this.Event == Unknown
    && this.Semaphore == Unknown
    && this.Mutant == Unknown
    && this.Section == Unknown
    && this.Key == Unknown;

  /// <summary>
  /// Whether all five are known, so the discovery pass has nothing left to do.
  /// </summary>
  /// <remarks>
  /// A type is only discoverable while some process holds a handle of it, so the pass has to be
  /// repeated across samples until this is true rather than run once and trusted.
  /// </remarks>
  public bool IsComplete => this.Event != Unknown
    && this.Semaphore != Unknown
    && this.Mutant != Unknown
    && this.Section != Unknown
    && this.Key != Unknown;

}

/// <summary>
/// Walks the machine's handle table and tallies it per process (PRD §20).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not marked as Windows-only and deliberately taking a span: it walks bytes and calls
/// nothing, so a recorded buffer replays through it on the Linux and macOS legs, which is the only
/// way any of this is exercised before it reaches a Windows machine (PRD §9.4).
/// </para>
/// <para>
/// One pass over the whole machine's table rather than a query per process, because there is no
/// per-process handle query on Windows at all — the table arrives whole and the owner is a field in
/// each row. That makes this cheaper here than the equivalent scan is on Linux, and it is still not
/// cheap: the table is megabytes on a busy machine, so §20's rule stands and nothing runs it but
/// somebody naming a column (PRD §5.4).
/// </para>
/// </remarks>
internal static class WindowsObjectTally {

  /// <summary>The header at the front of the buffer, before the entries.</summary>
  private static readonly int _HeaderSize = Unsafe.SizeOf<NtStructures.SystemHandleInformationEx>();

  private static readonly int _EntrySize = Unsafe.SizeOf<NtStructures.SystemHandleTableEntryInfoEx>();

  /// <summary>
  /// Tallies every handle in <paramref name="buffer"/> into <paramref name="into"/>, keyed by the
  /// process that holds it.
  /// </summary>
  /// <remarks>
  /// The count in the header is a number the kernel wrote and the buffer is a length this program
  /// chose, and the two can disagree — a table that grew between the sizing call and the reading one
  /// does exactly that. So the walk is bounded by both, and the smaller wins.
  /// </remarks>
  public static void Tally(ReadOnlySpan<byte> buffer, in ObjectTypeIndices types, Dictionary<int, ObjectTally> into) {
    ArgumentNullException.ThrowIfNull(into);
    if (buffer.Length < _HeaderSize)
      return;

    var declared = (ulong)MemoryMarshal.Read<nuint>(buffer);
    var room = (ulong)((buffer.Length - _HeaderSize) / _EntrySize);
    var count = Math.Min(declared, room);

    for (ulong i = 0; i < count; ++i) {
      ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemHandleTableEntryInfoEx>(
        buffer.Slice(_HeaderSize + ((int)i * _EntrySize), _EntrySize)
      );

      var index = entry.ObjectTypeIndex;
      // The overwhelming majority of a machine's handles are of types nothing here counts, so the
      // cheap rejection comes first: five comparisons rather than a dictionary lookup per handle,
      // on a table with a million rows in it.
      if (index != types.Event && index != types.Semaphore && index != types.Mutant
          && index != types.Section && index != types.Key)
        continue;

      var pid = (int)entry.UniqueProcessId;
      into.TryGetValue(pid, out var tally);
      if (index == types.Event) ++tally.Events;
      else if (index == types.Semaphore) ++tally.Semaphores;
      else if (index == types.Mutant) ++tally.Mutexes;
      else if (index == types.Section) ++tally.Sections;
      else ++tally.Keys;

      into[pid] = tally;
    }
  }

  /// <summary>
  /// A few candidate handles of every distinct object type index in the table, with the process
  /// holding each — enough to go and ask what that type is called.
  /// </summary>
  /// <param name="preferredPid">
  /// A process whose handles should be offered first, which the caller sets to its own: a process
  /// can always duplicate out of itself, and no other candidate is guaranteed to work at all.
  /// </param>
  /// <param name="perType">How many candidates to keep for each index.</param>
  /// <remarks>
  /// <para>
  /// Bounded by the number of object types a kernel has, which is a few dozen, rather than by the
  /// number of handles, which can be a million.
  /// </para>
  /// <para>
  /// <strong>Why more than one candidate.</strong> The first version kept exactly one — the first
  /// handle of each index in the table — and learnt nothing at all on a real Windows while working
  /// perfectly under Wine. The table is ordered by process and the first process in it is the System
  /// process, so the first handle of very nearly every type belongs to something that will not open
  /// for <c>PROCESS_DUP_HANDLE</c> at any privilege. Preferring the caller's own handles fixes the
  /// common case outright, and keeping several of each fixes the rest.
  /// </para>
  /// </remarks>
  public static List<(ushort Index, int Pid, nint Handle)> SampleTypes(
    ReadOnlySpan<byte> buffer,
    int preferredPid = 0,
    int perType = 4
  ) {
    var result = new List<(ushort, int, nint)>();
    if (buffer.Length < _HeaderSize || perType < 1)
      return result;

    var kept = new Dictionary<ushort, List<(int Pid, nint Handle)>>();
    var declared = (ulong)MemoryMarshal.Read<nuint>(buffer);
    var room = (ulong)((buffer.Length - _HeaderSize) / _EntrySize);
    var count = Math.Min(declared, room);

    for (ulong i = 0; i < count; ++i) {
      ref readonly var entry = ref MemoryMarshal.AsRef<NtStructures.SystemHandleTableEntryInfoEx>(
        buffer.Slice(_HeaderSize + ((int)i * _EntrySize), _EntrySize)
      );

      var pid = (int)entry.UniqueProcessId;
      if (!kept.TryGetValue(entry.ObjectTypeIndex, out var candidates))
        kept[entry.ObjectTypeIndex] = candidates = new(perType);

      // The caller's own handles go to the front however late they appear, because they are the only
      // ones certain to duplicate; everything else fills the remaining room in table order.
      if (pid == preferredPid && preferredPid != 0)
        candidates.Insert(0, (pid, entry.HandleValue));
      else if (candidates.Count < perType)
        candidates.Add((pid, entry.HandleValue));
      else
        continue;

      if (candidates.Count > perType)
        candidates.RemoveAt(candidates.Count - 1);
    }

    foreach (var (index, candidates) in kept)
      foreach (var (pid, handle) in candidates)
        result.Add((index, pid, handle));

    return result;
  }

}
