using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Drawing;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// The desktop's palette, for the code that is not inside a paint (PRD §74).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Hawkynt.NativeForms.OwnerDrawnControl"/> is handed a theme when it draws, and a
/// <see cref="Hawkynt.NativeForms.Form"/> is handed nothing at all — so a window that has to say
/// something about the scheme before it paints, such as the legend admitting that a high-contrast
/// desktop has turned its colours off, has no way to ask. This is that way.
/// </para>
/// <para>
/// Not cached. A theme is an immutable snapshot and the backend serves a fresh one after the desktop
/// changes, so holding on to the first answer would leave a window explaining a scheme the machine
/// stopped running an hour ago.
/// </para>
/// </remarks>
internal static class DesktopTheme {

  /// <summary>The running desktop's theme, or the fallback when there is no backend at all.</summary>
  /// <remarks>
  /// The fallback is a headless test or a run that never opened a window, not a failure — and it
  /// reports no high contrast, which is the right answer for a machine with no desktop to have set
  /// one on.
  /// </remarks>
  public static ITheme Current {
    get {
      try {
        return BackendRegistry.Resolve().Theme;
      } catch (PlatformNotSupportedException) {
        return DefaultTheme.Instance;
      }
    }
  }

}
