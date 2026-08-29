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

  /// <summary>
  /// The same notation as a mask, for the platform calls that want one (PRD §66).
  /// </summary>
  /// <remarks>
  /// <b>Strict where <see cref="Parse"/> is forgiving</b>, and the difference is which side the text
  /// came from. <see cref="Parse"/> reads kernel files, where a field this program has not caught up
  /// with should cost a heat map rather than the whole view. This reads what a person typed into a
  /// rule, and a mistyped range must be refused: the alternative is an affinity that quietly means
  /// something other than what they wrote, applied to a running program.
  /// </remarks>
  /// <returns>False for anything malformed, out of range, or empty.</returns>
  public static bool TryParseMask(string? list, out ulong mask) {
    mask = 0;
    if (list is not { Length: > 0 })
      return false;

    foreach (var part in list.Split(',')) {
      var piece = part.Trim();
      if (piece.Length == 0)
        return false;

      var dash = piece.IndexOf('-');
      if (dash < 0) {
        if (!TryBit(piece, ref mask))
          return false;

        continue;
      }

      if (!int.TryParse(piece.AsSpan(0, dash), out var first)
          || !int.TryParse(piece.AsSpan(dash + 1), out var last)
          || first < 0
          || last < first
          // Sixty-four is what a mask holds. A machine with more processors than that needs the
          // platform's group form, and dropping the processors above 63 would hand back a mask
          // saying something the person did not.
          || last > 63)
        return false;

      for (var i = first; i <= last; ++i)
        mask |= 1ul << i;
    }

    // An empty mask is not "every processor" and not "none": it is a line nobody finished. No
    // scheduler will take it, so refusing here is the difference between a rule that says why and a
    // call that fails somewhere else.
    return mask != 0;
  }

  private static bool TryBit(string piece, ref ulong mask) {
    if (!int.TryParse(piece, out var cpu) || cpu is < 0 or > 63)
      return false;

    mask |= 1ul << cpu;
    return true;
  }

  /// <summary>And back, so a mask can be shown the way the kernel would have written it.</summary>
  public static string Describe(ulong mask) {
    if (mask == 0)
      return "—";

    var text = new System.Text.StringBuilder();
    var cpu = 0;
    while (cpu < 64) {
      if ((mask & (1ul << cpu)) == 0) {
        ++cpu;
        continue;
      }

      var first = cpu;
      while (cpu < 64 && (mask & (1ul << cpu)) != 0)
        ++cpu;

      if (text.Length > 0)
        text.Append(',');

      text.Append(first);
      if (cpu - 1 > first)
        text.Append('-').Append(cpu - 1);
    }

    return text.ToString();
  }

}
