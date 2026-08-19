namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Pulls a value out of a <c>uevent</c> file (PRD §50).
/// </summary>
/// <remarks>
/// <c>uevent</c> is the one file every device in <c>/sys</c> has, and the only place the kernel says
/// which driver is bound to a card and what its PCI identity is. It is <c>KEY=value</c> lines and
/// nothing else — no comments, no continuations, no quoting — so this is a line scan and not a
/// parser.
/// <para>
/// No platform attribute and no file access: the parser is tested on every CI leg, on machines that
/// have no <c>/sys</c> at all (PRD §9.2).
/// </para>
/// </remarks>
public static class UeventParser {

  /// <summary>The value of a key, or an empty span when the file does not carry it.</summary>
  public static ReadOnlySpan<char> Value(ReadOnlySpan<char> content, ReadOnlySpan<char> key) {
    while (!content.IsEmpty) {
      var newline = content.IndexOf('\n');
      var line = newline < 0 ? content : content[..newline];
      content = newline < 0 ? default : content[(newline + 1)..];

      // The kernel writes no \r, but a fixture edited on Windows might.
      if (line.EndsWith("\r"))
        line = line[..^1];

      var equals = line.IndexOf('=');
      if (equals <= 0)
        continue;

      if (line[..equals].SequenceEqual(key))
        return line[(equals + 1)..];
    }

    return default;
  }

}
