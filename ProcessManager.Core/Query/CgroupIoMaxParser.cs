using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// What a cgroup's <c>io.max</c> allows it, per block device (PRD §38).
/// </summary>
/// <remarks>
/// <para>
/// The fourth kind of ceiling, and the one that is hardest to see from a process table: a group
/// throttled to a megabyte a second is not short of processor or memory and is not waiting on
/// anything a process table shows — it is being held at the block layer, and the only sign of it is
/// that everything takes longer than it should.
/// </para>
/// <para>
/// The file is a line per device and a key per direction, and <b>a key that is not written is not a
/// limit of nought</b>. The kernel omits nothing in practice — it prints all four keys with the word
/// <c>max</c> where there is no ceiling — but a caller must not depend on that, so an absent key is
/// <see cref="UnknownReason.NoLimit"/> for the same reason a literal <c>max</c> is, and never a
/// zero. A zero here would read as "this device is forbidden to it entirely", which is the opposite
/// of what it means (PRD §72.3).
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class CgroupIoMaxParser {

  /// <summary>
  /// Reads every device line, in the order the kernel wrote them.
  /// </summary>
  /// <remarks>
  /// An empty file is an empty list and is a real answer: the controller is enabled here and nothing
  /// is capped. "No such file" is the other answer and is not this function's to give — the reader
  /// distinguishes them, because from a span of text the two are indistinguishable.
  /// </remarks>
  public static IReadOnlyList<CgroupIoLimit> Parse(ReadOnlySpan<char> text) {
    var limits = new List<CgroupIoLimit>();
    while (!text.IsEmpty) {
      var newline = text.IndexOf('\n');
      var line = newline < 0 ? text : text[..newline];
      text = newline < 0 ? default : text[(newline + 1)..];

      // The kernel writes no \r, but a fixture edited on Windows might.
      if (line.EndsWith("\r"))
        line = line[..^1];

      if (Line(line) is { } limit)
        limits.Add(limit);
    }

    return limits;
  }

  /// <summary>
  /// One line: <c>259:0 rbps=2097152 wbps=max riops=max wiops=max</c>.
  /// </summary>
  /// <remarks>
  /// A line whose device is not two numbers separated by a colon is skipped rather than guessed at.
  /// Nothing else has ever been written there, and a line this cannot read is better dropped than
  /// reported against device nought — which is a real device on every machine.
  /// </remarks>
  private static CgroupIoLimit? Line(ReadOnlySpan<char> line) {
    line = line.Trim();
    if (line.IsEmpty)
      return null;

    var space = line.IndexOf(' ');
    var device = space < 0 ? line : line[..space];
    var colon = device.IndexOf(':');
    if (colon <= 0
        || !int.TryParse(device[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
        || !int.TryParse(device[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
      return null;

    var rest = space < 0 ? default : line[(space + 1)..];
    return new(
      major,
      minor,
      null,
      Key(rest, "rbps"),
      Key(rest, "wbps"),
      Key(rest, "riops"),
      Key(rest, "wiops")
    );
  }

  /// <summary>
  /// One <c>name=value</c> pair out of the rest of a line.
  /// </summary>
  /// <remarks>
  /// The name is matched whole, with its <c>=</c>, and only at the start of a word: <c>rbps</c> is a
  /// suffix of nothing here but <c>iops</c> is a suffix of both <c>riops</c> and <c>wiops</c>, and a
  /// search for a substring would answer the read limit when asked for the write one.
  /// </remarks>
  private static Counter Key(ReadOnlySpan<char> rest, ReadOnlySpan<char> name) {
    while (!rest.IsEmpty) {
      var space = rest.IndexOf(' ');
      var word = space < 0 ? rest : rest[..space];
      rest = space < 0 ? default : rest[(space + 1)..];

      var equals = word.IndexOf('=');
      if (equals < 0 || !word[..equals].SequenceEqual(name))
        continue;

      var value = word[(equals + 1)..];

      // The file says so outright, and it is an answer rather than a hole: this device is not
      // throttled in this direction.
      if (value.SequenceEqual("max"))
        return Counter.Unknown(UnknownReason.NoLimit);

      return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? Counter.Of(parsed)
        : Counter.Unknown(UnknownReason.CounterInvalid);
    }

    // The key is not on the line. Every kernel that writes this file writes all four, so this is a
    // shape nobody has seen — and the safe reading of it is the one that says nothing rather than
    // the one that says "nothing may pass".
    return Counter.Unknown(UnknownReason.NoLimit);
  }

}
