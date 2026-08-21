namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What a running program actually is, beyond its name (PRD §14).
/// </summary>
/// <remarks>
/// A process's name is what it calls itself and can be anything it likes. This is what the kernel
/// and the file on disk say — which architecture the binary was built for, what loads it, where it
/// is running, and which namespaces it can see.
/// <para>
/// Read on demand for the process being looked at: opening and reading an executable's header for
/// four hundred processes a second is not defensible, and none of it changes while the process runs
/// (PRD §5.4).
/// </para>
/// </remarks>
/// <param name="Architecture">
/// What the binary was built for, which on a machine that runs more than one is not the machine's
/// answer — an x86-64 kernel runs 32-bit binaries, and reporting the machine's architecture for
/// every row describes the machine rather than the program.
/// </param>
/// <param name="HeaderRead">
/// Whether the executable's own bytes could be read at all.
/// </param>
/// <remarks>
/// Without this, "no interpreter" and "we were not allowed to look" are the same value, and the
/// report said *statically linked* about every process belonging to somebody else. Which is a
/// confident claim made out of an absence — the exact thing §5.3 exists to stop.
/// </remarks>
/// <param name="Interpreter">
/// The dynamic loader, or a script's shebang program. Null with <paramref name="HeaderRead"/> set
/// means statically linked, which is a real answer; null without it means nobody could look.
/// </param>
/// <param name="WorkingDirectory">Where it is running, which decides what every relative path means.</param>
/// <param name="Namespaces">
/// The namespaces it is in, by kind and inode. Two processes sharing an inode share that namespace —
/// which is how a container's members are actually identified, rather than by a cgroup path anyone
/// can write.
/// </param>
public sealed record ImageInfo(
  string? Path,
  string? Architecture,
  bool HeaderRead,
  int Bits,
  bool? IsPositionIndependent,
  string? Interpreter,
  Counter SizeBytes,
  DateTime? ModifiedUtc,
  string? WorkingDirectory,
  IReadOnlyList<KeyValuePair<string, string>> Namespaces
);
