using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Drawing;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// How wide a string will be drawn, outside a paint (PRD §11).
/// </summary>
/// <remarks>
/// <para>
/// Auto-sizing a column has to measure text at the moment somebody asks for it, and the only
/// measuring surface a control is handed arrives inside its paint. The backend measures with the
/// same native text engine the paint will use, so the two agree — which is the whole requirement: a
/// column fitted with an approximation is a column that clips the value it was fitted to.
/// </para>
/// <para>
/// Null where there is no backend at all, which is a headless test rather than a failure. Nothing
/// there has a width, because nothing there is on screen.
/// </para>
/// </remarks>
internal sealed class MeasureText {

  private readonly IPlatformBackend _backend;
  private readonly Font _font;

  private MeasureText(IPlatformBackend backend) {
    this._backend = backend;
    this._font = backend.Theme.DefaultFont;
  }

  /// <summary>The measurer for this process, or null when no backend is registered.</summary>
  public static MeasureText? Instance {
    get {
      if (_resolved)
        return _instance;

      _resolved = true;
      try {
        _instance = new(BackendRegistry.Resolve());
      } catch (PlatformNotSupportedException) {
        // No display and no backend: a test, or a run that never opened a window. There is nothing
        // to fit a column to, and saying so is better than fitting it to a guess.
        _instance = null;
      }

      return _instance;
    }
  }

  private static MeasureText? _instance;
  private static bool _resolved;

  public int WidthOf(string text) => text.Length == 0 ? 0 : this._backend.MeasureText(text, this._font).Width;

}
