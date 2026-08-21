using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// <c>/proc/[pid]/task/[tid]/stack</c> — the frames the kernel is holding on a thread's behalf
/// (PRD §30).
/// </summary>
/// <remarks>
/// <para>
/// One frame per line, as <c>[&lt;address&gt;] symbol+0xoffset/0xsize</c>, with the defining module in
/// trailing brackets when the symbol came from one. The address is written as <c>0</c> on any machine
/// with <c>kernel.kptr_restrict</c> set, which is the default nearly everywhere — so the symbol is
/// usually the whole of what a frame says, and the address column is a hole with a reason in it
/// rather than a row of <c>0x0</c>.
/// </para>
/// <para>
/// This is the kernel stack and not the thread's own. There is no relationship between the two
/// beyond the boundary they meet at: these frames are the kernel's path from the system-call entry
/// down to whatever the thread is blocked in, and the code the program itself was running is below
/// the last of them and is not here. §30 says so on the screen, because a list that stopped without
/// explaining would read as the whole stack.
/// </para>
/// <para>
/// No platform attribute and no file access, so it is tested on every CI leg (PRD §9.2).
/// </para>
/// </remarks>
public static class KernelStackParser {

  /// <summary>A stack deeper than this is a runaway; the kernel's own limit is far below it.</summary>
  private const int _MaxFrames = 256;

  /// <summary>
  /// Parses the whole file into frames, outermost first — the order the kernel writes them, which is
  /// innermost first and is what every other stack viewer shows at the top.
  /// </summary>
  public static List<StackFrame> Parse(ReadOnlySpan<byte> content, int firstIndex = 0) {
    var frames = new List<StackFrame>();
    var scanner = new AsciiScanner(content);
    while (!scanner.IsEmpty && frames.Count < _MaxFrames) {
      var line = Trim(scanner.NextLine());
      if (line.IsEmpty)
        continue;

      if (TryParseFrame(line, firstIndex + frames.Count, out var frame))
        frames.Add(frame);
    }

    return frames;
  }

  /// <summary>Parses one line, or refuses one that is not a frame.</summary>
  public static bool TryParseFrame(ReadOnlySpan<byte> line, int index, out StackFrame frame) {
    frame = default;
    line = Trim(line);
    if (line.IsEmpty)
      return false;

    // The bracketed address, when the machine is willing to give one. kptr_restrict turns it into a
    // literal zero for every frame, and zero is an address — so it becomes the reason it is missing.
    var address = Counter.Unknown(UnknownReason.NotSupportedOnPlatform);
    if (line[0] == (byte)'[') {
      var close = line.IndexOf((byte)']');
      if (close < 0)
        return false;

      var inside = Trim(line[1..close]);
      if (!inside.IsEmpty && inside[0] == (byte)'<')
        inside = inside[1..];
      if (!inside.IsEmpty && inside[^1] == (byte)'>')
        inside = inside[..^1];

      var value = AsciiScanner.ParseHex(inside);
      address = value == 0 ? Counter.NotPermitted : Counter.Of(value);
      line = Trim(line[(close + 1)..]);
      if (line.IsEmpty)
        return false;
    }

    // "symbol+0x12/0x34 [module]" — the module is the last bracketed word, and only kallsyms writes
    // one, for a symbol that came out of a loadable module rather than out of the kernel image.
    string? module = null;
    if (line[^1] == (byte)']') {
      var open = line.LastIndexOf((byte)'[');
      if (open > 0) {
        module = Decode(Trim(line[(open + 1)..^1]));
        line = Trim(line[..open]);
      }
    }

    var displacement = Counter.NotSupported;
    var plus = line.LastIndexOf((byte)'+');
    if (plus > 0) {
      var offset = line[(plus + 1)..];
      var slash = offset.IndexOf((byte)'/');
      if (slash >= 0)
        offset = offset[..slash];

      if (offset.Length > 2 && offset[0] == (byte)'0' && (offset[1] | 0x20) == (byte)'x')
        offset = offset[2..];

      displacement = Counter.Of(AsciiScanner.ParseHex(offset));
      line = Trim(line[..plus]);
    }

    var symbol = Decode(line);
    frame = new(
      index,
      FrameKind.Kernel,
      address,
      symbol,
      displacement,
      module,
      // DWARF, which this program does not read. §30 keeps the columns and leaves them empty rather
      // than dropping them, so that the viewer says what it does not know.
      SourceFile: null,
      SourceLine: 0
    );

    return symbol is not null;
  }

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> text) {
    var start = 0;
    var end = text.Length;
    while (start < end && AsciiScanner.IsSpace(text[start]))
      ++start;
    while (end > start && AsciiScanner.IsSpace(text[end - 1]))
      --end;

    return text[start..end];
  }

  private static string? Decode(ReadOnlySpan<byte> text)
    => text.IsEmpty ? null : Encoding.UTF8.GetString(text);

}
