using System.Text;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// Copying out of a terminal, which is the terminal's clipboard and not the machine's (PRD §11).
/// </summary>
/// <remarks>
/// <para>
/// OSC 52 is the only honest answer here. A process on the far end of an SSH session has no access to
/// the clipboard of the machine the human is sitting at — writing to an X selection would put the
/// text on the *server's* clipboard, where nobody can paste it. OSC 52 hands the text to the terminal
/// emulator itself, which is the program that owns the clipboard the user is about to paste into.
/// </para>
/// <para>
/// It is not universal, and cannot be detected: the terminal answers nothing, so a copy that was
/// dropped looks exactly like one that worked. Two consequences, both deliberate — the status line
/// says the text was *offered* to the terminal rather than claiming it was copied, and every copy is
/// also reachable through the export key, which writes a file no terminal setting can veto.
/// </para>
/// </remarks>
public static class Clipboard {

  /// <summary>
  /// How much text will be offered. Terminals cap what they will accept and drop the rest silently;
  /// a refusal that says so is better than half a table pasted with no sign of where it stopped.
  /// </summary>
  public const int SizeLimit = 96 * 1024;

  /// <summary>Builds the sequence that hands <paramref name="text"/> to the terminal.</summary>
  /// <returns>False when the text is over <see cref="SizeLimit"/> and nothing was built.</returns>
  public static bool TryEncode(string? text, out string sequence) {
    sequence = string.Empty;
    if (string.IsNullOrEmpty(text) || text.Length > SizeLimit)
      return false;

    // "c" is the clipboard proper; terminals that only implement the primary selection also accept it
    // and put the text there, which is still somewhere the user can paste from.
    var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    sequence = $"\u001b]52;c;{payload}\u0007";
    return true;
  }

  /// <summary>Offers the text to the terminal. Nothing answers, so nothing is returned.</summary>
  public static bool TryWrite(TextWriter writer, string? text) {
    ArgumentNullException.ThrowIfNull(writer);
    if (!TryEncode(text, out var sequence))
      return false;

    writer.Write(sequence);
    writer.Flush();
    return true;
  }

}
