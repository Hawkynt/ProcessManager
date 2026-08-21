using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Which signal to send (PRD §25.1).
/// </summary>
/// <remarks>
/// <para>
/// A list rather than a submenu, because there are thirty-one of them and each needs a sentence.
/// The sentence is the point: <b>the default action of most signals is to end the process</b>, so
/// sending <c>SIGUSR1</c> to a program that never installed a handler for it kills the program. A
/// menu of bare names would hide exactly that.
/// </para>
/// <para>
/// The number box beside the list is not a convenience. The real-time signals have no name that can
/// be offered honestly — <c>SIGRTMIN</c> is whatever C library the <em>target</em> was linked
/// against reserved for itself, and a sender cannot see which — so the number, which is
/// unambiguous, is the only half that can be (PRD §5.3).
/// </para>
/// </remarks>
public sealed class SignalDialog : Form {

  private const int _Margin = 12;
  private const int _RowHeight = 19;
  private const int _ButtonHeight = 28;

  private readonly TreeListView _list = new();
  private readonly Label _consequence = new();
  private readonly Label _numberLabel = new() { Text = "…or a number:" };
  private readonly TextBox _number = new();
  private readonly Button _send = new() { Text = "Send" };
  private readonly Button _cancel = new() { Text = "Cancel" };

  public SignalDialog(string processName, int pid) {
    this.Text = $"Send a signal — {processName} ({pid})";
    this.QuitsOnClose = false;

    this._list.ShowColumnHeaders = true;
    this._list.ItemHeight = 17;
    this._list.Columns.Add(new("Signal", 130, node => ((string[])node.Tag!)[0]));
    this._list.Columns.Add(new("No.", 46, node => ((string[])node.Tag!)[1]));
    this._list.Columns.Add(new("If it does not handle it", 190, node => ((string[])node.Tag!)[2]));
    this._list.Columns.Add(new("What it is for", 520, node => ((string[])node.Tag!)[3]));

    foreach (var signal in Signals.All) {
      var cells = new[] {
        signal.Name,
        signal.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Describe(signal),
        signal.Meaning,
      };

      this._list.Nodes.Add(new TreeNode(signal.Name) { Tag = cells });
    }

    this._consequence.Text = Signals.All.Count > 0
      ? "Choose a signal."
      : Signals.UnknownArchitecture;

    this._list.AfterSelect += (_, _) => this.Explain();
    this._number.TextChanged += (_, _) => this.Explain();

    this._send.Click += (_, _) => {
      if (this.Chosen is null) {
        MessageBox.Show(
          $"Choose a signal from the list, or type a number from 1 to 64.\n\n{Signals.RealTimeAreNumberedOnly}",
          "Process Manager"
        );

        return;
      }

      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();

    this.Controls.Add(this._list);
    this.Controls.Add(this._consequence);
    this.Controls.Add(this._numberLabel);
    this.Controls.Add(this._number);
    this.Controls.Add(this._send);
    this.Controls.Add(this._cancel);

    // Tall enough for the whole table without scrolling: the list is the dialog, and a signal
    // chooser that shows eight of thirty-one is a chooser somebody has to hunt through.
    this.Bounds = new(0, 0, 900, (Math.Max(Signals.All.Count, 8) * _RowHeight) + 150);
    this.MinimumSize = new(520, 320);
    // Laid out by arithmetic rather than by anchoring, for the reason MainWindow's own layout note
    // records: a child anchored inside a docked container here grows without bound.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>True when the dialog was closed with Send.</summary>
  public bool Accepted { get; private set; }

  /// <summary>
  /// The signal number that was chosen, or null when nothing usable was.
  /// </summary>
  /// <remarks>
  /// The typed number wins over the selected row, because typing one is the later and more
  /// deliberate act — and because the row cannot be deselected in this toolkit, so a selection made
  /// on the way past would otherwise silently outrank what somebody typed.
  /// </remarks>
  public int? Chosen {
    get {
      if (this._number.Text is { Length: > 0 } text)
        return Signals.TryParse(text, out var typed) ? typed : null;

      return this._list.SelectedNode?.Tag is string[] cells
        && int.TryParse(cells[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
          ? number
          : null;
    }
  }

  /// <summary>What the box says, for a test and for the capture log that has no display to read it off.</summary>
  public string Description {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine(this.Text);
      foreach (var node in this._list.Nodes)
        if (node.Tag is string[] cells)
          text.AppendLine(string.Join("  ", cells));

      text.AppendLine(this._consequence.Text);
      return text.ToString();
    }
  }

  /// <summary>
  /// What the kernel does with it when nobody handled it, in the two words a column has room for.
  /// </summary>
  private static string Describe(in Signals.Signal signal) => signal.Default switch {
    Signals.Default.Terminates => signal.Catchable ? "ends the process" : "ends it; cannot be refused",
    Signals.Default.TerminatesWithCore => "ends it, and dumps core",
    Signals.Default.Stops => signal.Catchable ? "stops the process" : "stops it; cannot be refused",
    Signals.Default.Continues => "starts it again",
    _ => "nothing happens",
  };

  /// <summary>Puts the consequence of the current choice where it can be read before pressing Send.</summary>
  private void Explain()
    => this._consequence.Text = this.Chosen is { } number
      ? $"{Signals.Describe(number)} — {Signals.Consequence(number)}"
      : this._number.Text is { Length: > 0 }
        ? $"That is not a signal. {Signals.RealTimeAreNumberedOnly}"
        : "Choose a signal.";

  public void ApplyLayout() {
    var width = Math.Max(320, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);
    var numbers = buttons - _ButtonHeight - 8;
    var consequence = numbers - (_RowHeight * 2) - 6;

    this._list.Bounds = new(_Margin, _Margin, width, Math.Max(_RowHeight, consequence - _Margin - 6));
    this._consequence.Bounds = new(_Margin, consequence, width, _RowHeight * 2);
    this._numberLabel.Bounds = new(_Margin, numbers + 4, 100, _RowHeight);
    this._number.Bounds = new(_Margin + 104, numbers, 90, 24);
    this._cancel.Bounds = new(this.Width - _Margin - 90, buttons, 90, _ButtonHeight);
    this._send.Bounds = new(this._cancel.Bounds.X - 100, buttons, 90, _ButtonHeight);
  }

}
