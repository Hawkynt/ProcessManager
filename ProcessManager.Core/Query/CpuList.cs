namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The kernel's list-of-CPUs notation: <c>0-7,16-23</c> (PRD §46).
/// </summary>
/// <remarks>
/// Every file in <c>/sys</c> that names a set of processors uses it — <c>cpu_core/cpus</c>,
/// <c>cpu_atom/cpus</c>, <c>online</c>, <c>possible</c>, a cgroup's <c>cpuset.cpus</c>. Ranges are
/// inclusive at both ends, single numbers stand alone, and an empty file means an empty set rather
/// than an error.
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class CpuList {

  /// <summary>
  /// Every processor a list names, ascending and without duplicates.
  /// </summary>
  /// <remarks>
  /// Anything malformed is skipped rather than throwing: this parses kernel files, and a build that
  /// refuses to show a heat map because one <c>/sys</c> file gained a field is worse than one that
  /// shows the cores it understood (PRD §73).
  /// </remarks>
  public static IReadOnlyList<int> Parse(ReadOnlySpan<char> text) {
    var found = new List<int>();
    foreach (var range in text.Trim().Split(',')) {
      var part = text[range].Trim();
      if (part.IsEmpty)
        continue;

      var dash = part.IndexOf('-');
      if (dash < 0) {
        if (int.TryParse(part, out var single) && single >= 0 && !found.Contains(single))
          found.Add(single);

        continue;
      }

      if (!int.TryParse(part[..dash], out var first) || !int.TryParse(part[(dash + 1)..], out var last))
        continue;

      // A backwards range is nonsense rather than an empty one, and a range covering a whole
      // machine's worth of imaginary processors is a corrupted file — neither is worth expanding.
      if (first < 0 || last < first || last - first > 65535)
        continue;

      for (var cpu = first; cpu <= last; ++cpu)
        if (!found.Contains(cpu))
          found.Add(cpu);
    }

    found.Sort();
    return found;
  }

}
