namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// One desktop window and the process behind it (PRD §39).
/// </summary>
/// <param name="Handle">
/// The window's native identifier — an <c>HWND</c> on Windows, an X11 window id on a Unix desktop.
/// Kept as a 64-bit number because the two are different widths and neither fits the other.
/// </param>
/// <param name="Pid">
/// Which process owns it, or -1 where the window server will not say. A window whose owner is
/// unknown is still worth listing: it is evidence that something is on screen that the process list
/// cannot account for.
/// </param>
/// <param name="Title">The caption, which is what a person recognises a window by.</param>
/// <param name="Class">
/// The application class — <c>WM_CLASS</c> on X11, the window class on Windows. Survives a title
/// changing every second, which is what makes it the useful thing to match on.
/// </param>
/// <param name="Bounds">Where it is, in screen coordinates: x, y, width, height.</param>
/// <param name="IsVisible">Whether it is mapped. An unmapped window is real but not on screen.</param>
public readonly record struct WindowRecord(
  ulong Handle,
  int Pid,
  string Title,
  string? Class,
  (int X, int Y, int Width, int Height) Bounds,
  bool IsVisible
);

/// <summary>Why a desktop cannot be asked about its windows (PRD §39).</summary>
public enum WindowSourceState : byte {

  /// <summary>Windows can be enumerated.</summary>
  Available,

  /// <summary>There is no graphical session at all — a server, or a program run over ssh.</summary>
  NoSession,

  /// <summary>
  /// A Wayland session, where a client cannot enumerate other clients' surfaces by design.
  /// </summary>
  /// <remarks>
  /// Not a gap to be filled in later, and the distinction matters: this is the protocol refusing,
  /// not the program failing to ask. Anything XWayland is hosting still appears, which is why a
  /// Wayland desktop shows some windows rather than none and needs saying so even more.
  /// </remarks>
  WaylandRefuses,

  /// <summary>The platform's window enumeration is not implemented here yet.</summary>
  NotImplemented,

}

/// <summary>What the desktop said, and whether it was willing to say anything.</summary>
public sealed record WindowList(WindowSourceState State, IReadOnlyList<WindowRecord> Windows) {

  public static readonly WindowList NotImplemented = new(WindowSourceState.NotImplemented, []);

  /// <summary>One sentence a page can put where the list would be.</summary>
  public string Explain() => this.State switch {
    WindowSourceState.Available => string.Empty,
    WindowSourceState.NoSession => "There is no graphical session to ask.",
    WindowSourceState.WaylandRefuses =>
      "This is a Wayland session, where a program cannot be told about other programs' windows. "
      + "Only windows hosted by XWayland can be listed.",
    _ => "Listing windows is not implemented on this platform yet.",
  };

}
