namespace Hawkynt.ProcessManager.Settings;

/// <summary>
/// Writes the settings back out whenever they have actually changed (PRD §11, §67).
/// </summary>
/// <remarks>
/// <para>
/// The file used to be written only by <c>--save-settings</c>, which meant every preference somebody
/// set through the window — the sort, the columns, the interval, the size of the window itself — was
/// gone by the next start unless they knew to run the program again from a terminal with a flag.
/// </para>
/// <para>
/// Two things stop that becoming a write per second. <see cref="Flush"/> is called from the sample
/// tick rather than from each change, so a window being dragged is one write and not a hundred; and
/// the settings are rendered and compared with what was last written, so a tick that changed nothing
/// touches no disk at all. Comparing the text rather than the record is deliberate — the text is
/// what actually lands in the file, and a change that does not reach it is not a change.
/// </para>
/// <para>
/// A failed write is not reported. Somebody diagnosing a machine whose disk is full is exactly the
/// person who must not be interrupted by a dialog about a preferences file (PRD §81).
/// </para>
/// </remarks>
public sealed class SettingsAutoSaver {

  private readonly Func<UserSettings> _current;
  private readonly Func<UserSettings, bool> _save;
  private string? _lastWritten;

  /// <param name="current">Produces the settings as they stand; called once per flush.</param>
  /// <param name="save">Performs the write, returning whether it worked.</param>
  public SettingsAutoSaver(Func<UserSettings> current, Func<UserSettings, bool>? save = null) {
    ArgumentNullException.ThrowIfNull(current);

    this._current = current;
    this._save = save ?? (settings => SettingsStore.Save(settings));
  }

  /// <summary>How many writes have actually reached the file, which is what the tests count.</summary>
  public int Writes { get; private set; }

  /// <summary>
  /// Takes the settings as read and starts from there, so the first flush after a start does not
  /// rewrite a file that has not changed.
  /// </summary>
  public void Prime(UserSettings loaded) {
    ArgumentNullException.ThrowIfNull(loaded);
    this._lastWritten = loaded.Write();
  }

  /// <summary>Writes the settings if they differ from what is already in the file.</summary>
  /// <returns>Whether anything was written.</returns>
  public bool Flush() {
    UserSettings settings;
    string text;
    try {
      settings = this._current();
      text = settings.Write();
    } catch (InvalidOperationException) {
      // The window is mid-teardown and cannot describe itself. Nothing to save is not a failure.
      return false;
    }

    if (string.Equals(text, this._lastWritten, StringComparison.Ordinal))
      return false;

    if (!this._save(settings))
      return false;

    this._lastWritten = text;
    ++this.Writes;
    return true;
  }

}
