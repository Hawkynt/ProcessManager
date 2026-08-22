namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Splitting a Windows service's <c>ImagePath</c> into the program and its arguments (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// In Core and with no platform attribute, so it is tested on every CI leg (§9.2). It reads text and
/// opens nothing; the registry value it reads is handed to it.
/// </para>
/// <para>
/// <c>ImagePath</c> is a <b>command line</b> and not a path. It may be quoted, it may carry arguments
/// after the executable, and on Windows most program paths have a space in them — so splitting on the
/// first space would name the wrong program for the majority of services on the machine.
/// </para>
/// </remarks>
public static class ServiceImagePath {

  /// <summary>
  /// The program a service starts.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Quoted is the easy case and the correct one. Unquoted, this ends the program at the first
  /// <c>.exe</c>, which reads the common case — <c>C:\Program Files\Thing\thing.exe --serve</c> — and
  /// does not pretend to resolve the ambiguous one.
  /// </para>
  /// <para>
  /// <b>The ambiguity is real and is not this function's to settle.</b> Windows resolves an unquoted
  /// path by trying each prefix in turn, so <c>C:\Program Files\Thing\thing.exe</c> would also start
  /// <c>C:\Program.exe</c> if that existed — the unquoted-service-path weakness. Reporting what the
  /// registry says is a reading; deciding which program the loader would pick is a claim about a
  /// filesystem this has not looked at (§5.3).
  /// </para>
  /// </remarks>
  public static string? ExecutableOf(string? command) {
    if (command is not { Length: > 0 })
      return null;

    var text = command.Trim();
    if (text.Length == 0)
      return null;

    if (text[0] == '"') {
      var close = text.IndexOf('"', 1);
      return close > 1 ? text[1..close] : text[1..];
    }

    var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
    if (exe > 0)
      return text[..(exe + 4)];

    // A driver has no extension at all — \SystemRoot\System32\drivers\thing.sys, or a bare path with
    // no arguments — so the whole of it is the program.
    var space = text.IndexOf(' ');
    return space > 0 ? text[..space] : text;
  }

  /// <summary>
  /// Whatever follows the program, as the registry wrote it.
  /// </summary>
  /// <remarks>
  /// Left as one string rather than split into a vector. The registry holds a command line and the
  /// service control manager passes it to <c>CreateProcess</c>, which does its own splitting; a
  /// vector reconstructed here would be this program's guess at that, shown as though it were what
  /// the machine will do (§5.3).
  /// </remarks>
  public static string? ArgumentsOf(string? command) {
    if (command is not { Length: > 0 } || ExecutableOf(command) is not { Length: > 0 } executable)
      return null;

    var text = command.Trim();
    var after = text.IndexOf(executable, StringComparison.Ordinal);
    if (after < 0)
      return null;

    // The closing quote comes off with the leading space, because a quoted program is followed by
    // <quote><space> and neither belongs to the first argument.
    var rest = text[(after + executable.Length)..].TrimStart('"', ' ');
    return rest.Length > 0 ? rest : null;
  }

}
