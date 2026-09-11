using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

public sealed partial class MainWindow {

  private bool _playbackEnabled;

  /// <summary>
  /// Makes the sampler retain playback and exposes it in both the command strip and the menu.
  /// </summary>
  /// <remarks>
  /// Kept out of the constructor so front-ends which instantiate the shared window for capture/tests
  /// can choose explicitly whether the recorder's memory belongs to that run. DesktopApp enables it
  /// for normal interactive use before the first sample is taken.
  /// </remarks>
  internal void EnablePlayback() {
    if (this._playbackEnabled)
      return;

    this._sampler.EnablePlayback();
    this._commands.Items.Add(Command("Playback", this.ShowPlayback));
    this._menu?.Items.Add(Item("Playback…", this.ShowPlayback));
    this._playbackEnabled = true;
  }

  /// <summary>Opens a playback investigation window over the sampler's retained history.</summary>
  public PlaybackWindow OpenPlayback() {
    var history = this._sampler.Playback ?? this._sampler.EnablePlayback();
    var window = new PlaybackWindow(history);
    window.Show();
    return window;
  }

  private void ShowPlayback() => this.OpenPlayback();

}
