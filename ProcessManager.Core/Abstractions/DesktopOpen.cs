namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// Handing something to the desktop to open — a folder, a page (PRD §25.3).
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="LaunchRequest"/> rather than a call, so that opening a file manager goes through the
/// same code path, the same refusals and the same test coverage as starting any other program. There
/// is no second way to run something in this program and there should not be.
/// </para>
/// <para>
/// The desktop's own opener is used rather than a guess at which file manager or browser is
/// installed: <c>xdg-open</c>, <c>explorer</c> and <c>open</c> each ask the session what it prefers,
/// which is the only answer that is right on a machine somebody else configured.
/// </para>
/// </remarks>
public static class DesktopOpen {

  /// <summary>The session's own "open this the way I like it" program, or null where there is none.</summary>
  private static string? Opener {
    get {
      if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
        return "xdg-open";

      if (OperatingSystem.IsWindows())
        return "explorer";

      return OperatingSystem.IsMacOS() ? "open" : null;
    }
  }

  /// <summary>
  /// Opens the folder a file lives in (PRD §25.3).
  /// </summary>
  /// <remarks>
  /// The folder, not the file selected inside it. Selecting the file needs a switch each file
  /// manager spells differently — <c>--select</c> here, <c>/select,</c> there, a D-Bus call
  /// somewhere else — and guessing wrong opens the executable instead of showing it, which for a
  /// binary is the one outcome nobody wants.
  /// </remarks>
  public static LaunchRequest? Reveal(string? path) {
    if (Opener is not { } opener || string.IsNullOrWhiteSpace(path))
      return null;

    var folder = Path.GetDirectoryName(path);
    return string.IsNullOrEmpty(folder) ? null : new(opener, [folder]);
  }

  /// <summary>
  /// Opens a file itself, in whatever the session has chosen to open that kind of file (PRD §41, §42).
  /// </summary>
  /// <remarks>
  /// The file and not its folder, which is the difference from <see cref="Reveal"/> and the whole
  /// point of "open configuration": a unit file and a <c>.desktop</c> entry are text somebody wants to
  /// read, and stopping at the directory they are in leaves the reader to find them by eye among four
  /// hundred others.
  /// <para>
  /// Which program that is remains the session's business. A guess at an editor is wrong on every
  /// machine somebody else configured, and on a machine with no desktop at all there is no opener and
  /// this answers null rather than starting something nobody asked for.
  /// </para>
  /// </remarks>
  public static LaunchRequest? Open(string? path)
    => Opener is { } opener && !string.IsNullOrWhiteSpace(path) ? new(opener, [path]) : null;

  /// <summary>Opens a page in whatever the session calls its browser.</summary>
  public static LaunchRequest? Browse(string? url)
    => Opener is { } opener && Uri.TryCreate(url, UriKind.Absolute, out var parsed)
      && parsed.Scheme is "http" or "https"
        ? new(opener, [parsed.AbsoluteUri])
        : null;

  /// <summary>
  /// Where a web search for a term goes.
  /// </summary>
  /// <remarks>
  /// One engine, named here, rather than the session's default — because the session's default is
  /// not discoverable without opening a browser to ask it, and because a search that quietly went
  /// somewhere the user had not been told about is worse than one that says where it is going. The
  /// engine chosen keeps no search history against a profile; it is one string to change for anybody
  /// who prefers another (PRD §70).
  /// </remarks>
  public const string SearchEngine = "duckduckgo.com";

  /// <summary>
  /// A search for a process or file name.
  /// </summary>
  /// <remarks>
  /// Escaped, because a process may call itself anything at all — including something containing an
  /// ampersand, which unescaped would end the query and start a parameter of its own.
  /// </remarks>
  public static LaunchRequest? Search(string? term)
    => string.IsNullOrWhiteSpace(term)
      ? null
      : Browse($"https://{SearchEngine}/?q={Uri.EscapeDataString(term)}");

}
