using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One process, in a window of its own (PRD §26).
/// </summary>
/// <remarks>
/// <para>
/// The detail pane at the foot of the main window follows the selection, so it can only ever show
/// the process that is selected now. This is the same pane pinned to one process, which is what
/// makes it possible to have two of them open and compare them — the thing Process Explorer is good
/// at and the reason §26 asks for a window rather than a pane.
/// </para>
/// <para>
/// Refreshed from the main window's sample tick. When the process ends the window stays, says so in
/// its title, and stops asking about a pid that now belongs to somebody else (PRD §86).
/// </para>
/// </remarks>
public sealed class ProcessPropertiesWindow : Form {

  private readonly DetailPane _pane;
  private readonly string _name;

  public ProcessPropertiesWindow(ISystemProbe probe, ProcessKey key, string name) {
    ArgumentNullException.ThrowIfNull(probe);

    this.Key = key;
    this._name = name;
    this._pane = new(probe);

    this.Text = $"{name} ({key.Pid})";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 900, 560);

    this._pane.Control.Dock = DockStyle.Fill;
    this.Controls.Add(this._pane.Control);
    this._pane.Select(key);
  }

  /// <summary>Which process this window is about. It never changes — that is the point of it.</summary>
  public ProcessKey Key { get; }

  /// <summary>True once the process has ended and the window has stopped following it.</summary>
  public bool Ended { get; private set; }

  /// <summary>
  /// Refreshes from the latest sample.
  /// </summary>
  /// <remarks>
  /// Identity, not pid: a window left open while its process exits must not start describing
  /// whatever the kernel handed that number to next (PRD §72.2).
  /// </remarks>
  public void UpdateFromSample(SystemSnapshot snapshot, ProcessRow? row) {
    if (this.Ended)
      return;

    if (row is null || !snapshot.TryGetProcess(this.Key, out var process)) {
      this.Ended = true;
      // Kept open rather than closed from under somebody who is reading it. The lists keep whatever
      // they last held, which is usually why the window was open in the first place.
      this.Text = $"{this._name} ({this.Key.Pid}) — ended";
      return;
    }

    this._pane.UpdateOverview(in process, row);
    this._pane.Refresh();
  }

}
