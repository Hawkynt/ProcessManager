using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Points at a window and says which process it belongs to (PRD §39).
/// </summary>
/// <remarks>
/// <para>
/// Process Explorer does this by having you drag a crosshair, which needs a pointer grab: the
/// pointer belongs to this program until the button comes up, and a grab left dangling by a crash
/// takes the desktop with it. A countdown gets the same answer — the pointer is read where it
/// already is — and never holds anything hostage.
/// </para>
/// <para>
/// It also works when the target is modal, dragging, or in a menu, none of which survive a grab.
/// </para>
/// </remarks>
public sealed class WindowPickerDialog : Form {

  private const int _Seconds = 4;

  private readonly ISystemProbe _probe;
  private readonly Label _message = new();
  private readonly Label _preview = new();
  private readonly Button _cancel = new() { Text = "Cancel" };
  private readonly NativeForms.Timer _tick = new() { Interval = 250 };

  private int _remaining = _Seconds * 4;

  public WindowPickerDialog(ISystemProbe probe) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this.Text = "Find window";
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 460, 170);

    this._message.Bounds = new(16, 16, 428, 40);
    this._preview.Bounds = new(16, 60, 428, 40);
    this._cancel.Bounds = new(360, 112, 80, 28);
    this._cancel.Click += (_, _) => this.Close();

    this.Controls.Add(this._message);
    this.Controls.Add(this._preview);
    this.Controls.Add(this._cancel);

    this._tick.Tick += (_, _) => this.Advance();
    this._tick.Start();
    this.Advance();
  }

  /// <summary>The process behind the window that was pointed at, or -1.</summary>
  public int ChosenPid { get; private set; } = -1;

  /// <summary>What was picked, for the caller to report when the process cannot be found.</summary>
  public WindowRecord? Picked { get; private set; }

  private void Advance() {
    // Followed live rather than only sampled at the end, so somebody can see it is tracking them
    // and move to the right window before the count runs out.
    var under = this._probe.WindowUnderPointer();
    this._preview.Text = under is { } window
      ? $"{(window.Title.Length > 0 ? window.Title : "(untitled)")} — {window.Class ?? "?"}, pid {window.Pid}"
      : "nothing under the pointer";

    if (--this._remaining > 0) {
      this._message.Text = $"Point at a window. Picking in {((this._remaining + 3) / 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}…";
      return;
    }

    this._tick.Stop();
    this.Picked = under;
    this.ChosenPid = under?.Pid ?? -1;
    this.Close();
  }

}
