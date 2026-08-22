using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Starting a program from the window (PRD §54, §91).
/// </summary>
/// <remarks>
/// <para>
/// The command line has been able to do this for a while and the window could not, which §91 counts
/// as not having the capability: somebody with a window open who wants to start something does not
/// go and find a terminal, and the reason Task Manager has had this box since 1996 is that the
/// moment you need it is the moment the shell is what is broken.
/// </para>
/// <para>
/// The same <see cref="LaunchRequest"/> the command line builds, so the two cannot start a program
/// differently — the arguments are split here rather than passed as one string, because a path with
/// a space in it is the ordinary case and re-splitting a joined line is how that goes wrong.
/// </para>
/// </remarks>
public sealed class RunTaskDialog : Form {

  private const int _Margin = 12;
  private const int _RowHeight = 24;
  private const int _ButtonHeight = 28;
  private const int _LabelWidth = 140;

  private readonly Label _programLabel = new() { Text = "Program" };
  private readonly TextBox _program = new();
  private readonly Label _argumentsLabel = new() { Text = "Arguments" };
  private readonly TextBox _arguments = new();
  private readonly Label _directoryLabel = new() { Text = "Start in" };
  private readonly TextBox _directory = new();
  private readonly CheckBox _elevated = new() { Text = "Run with administrative privilege" };
  private readonly CheckBox _suspended = new() { Text = "Start it stopped, so it can be looked at before it runs" };
  private readonly Label _note = new();
  private readonly Button _run = new() { Text = "Run" };
  private readonly Button _cancel = new() { Text = "Cancel" };

  public RunTaskDialog(string? startIn = null) {
    this.Text = "Run a new task";
    this.QuitsOnClose = false;

    this._directory.Text = startIn ?? string.Empty;
    this._note.Text =
      "Arguments are split on spaces, and a quoted run stays one argument. The program is started as "
      + "a child of this one and survives it.";

    this._run.Click += (_, _) => {
      if (this._program.Text is not { Length: > 0 }) {
        MessageBox.Show("There is no program named to run.", "Process Manager");
        return;
      }

      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();

    foreach (var control in (ReadOnlySpan<Control>)[
      this._programLabel, this._program,
      this._argumentsLabel, this._arguments,
      this._directoryLabel, this._directory,
      this._elevated, this._suspended, this._note, this._run, this._cancel,
    ])
      this.Controls.Add(control);

    // A label's caption is not the field's, so each field says what it is to a reader who cannot see
    // the label beside it (PRD §74).
    this._program.AccessibleName = this._programLabel.Text;
    this._arguments.AccessibleName = this._argumentsLabel.Text;
    this._directory.AccessibleName = this._directoryLabel.Text;

    this.Bounds = new(0, 0, 640, 280);
    this.MinimumSize = new(420, 240);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>True when the dialog was closed with Run.</summary>
  public bool Accepted { get; private set; }

  /// <summary>
  /// What was asked for, in the shape the command line uses.
  /// </summary>
  /// <remarks>
  /// Null when nothing usable was named. The working directory is null rather than empty when it was
  /// left blank, because null means "wherever this program is" and an empty string is a path that
  /// does not exist.
  /// </remarks>
  public LaunchRequest? Request
    => this._program.Text is { Length: > 0 } program
      ? new(
        program,
        SplitArguments(this._arguments.Text),
        this._directory.Text is { Length: > 0 } directory ? directory : null,
        Elevated: this._elevated.Checked,
        Suspended: this._suspended.Checked
      )
      : null;

  /// <summary>
  /// Splits a line the way a shell would, without being one.
  /// </summary>
  /// <remarks>
  /// Quotes hold a run together and everything else splits on whitespace. Deliberately no expansion
  /// of anything: no globs, no variables, no backticks. A box that quietly ran a shell would make
  /// every character somebody typed a possible command, which is a much larger thing to offer than
  /// "start this program" — and the whole reason this exists is the case where the shell is what has
  /// gone wrong.
  /// </remarks>
  internal static IReadOnlyList<string> SplitArguments(string? line) {
    var arguments = new List<string>();
    if (line is not { Length: > 0 })
      return arguments;

    var current = new System.Text.StringBuilder();
    var quote = '\0';
    var started = false;
    foreach (var character in line) {
      if (quote != '\0') {
        if (character == quote)
          quote = '\0';
        else
          current.Append(character);

        continue;
      }

      if (character is '"' or '\'') {
        quote = character;
        // An empty quoted run is still an argument — "" is how somebody passes one on purpose.
        started = true;
        continue;
      }

      if (char.IsWhiteSpace(character)) {
        if (started || current.Length > 0)
          arguments.Add(current.ToString());

        current.Clear();
        started = false;
        continue;
      }

      current.Append(character);
    }

    if (started || current.Length > 0)
      arguments.Add(current.ToString());

    return arguments;
  }

  private void ApplyLayout() {
    var width = this.ClientSize.Width;
    var fieldLeft = _Margin + _LabelWidth;
    var fieldWidth = Math.Max(80, width - fieldLeft - _Margin);
    var y = _Margin;

    foreach (var (label, field) in (ReadOnlySpan<(Label, TextBox)>)[
      (this._programLabel, this._program),
      (this._argumentsLabel, this._arguments),
      (this._directoryLabel, this._directory),
    ]) {
      label.Bounds = new(_Margin, y + 4, _LabelWidth - 8, 20);
      field.Bounds = new(fieldLeft, y, fieldWidth, 22);
      y += _RowHeight + 4;
    }

    y += 4;
    this._elevated.Bounds = new(_Margin, y, width - (2 * _Margin), 20);
    y += _RowHeight;
    this._suspended.Bounds = new(_Margin, y, width - (2 * _Margin), 20);
    y += _RowHeight + 4;
    this._note.Bounds = new(_Margin, y, width - (2 * _Margin), 34);

    var buttons = this.ClientSize.Height - _ButtonHeight - _Margin;
    this._cancel.Bounds = new(width - _Margin - 96, buttons, 96, _ButtonHeight);
    this._run.Bounds = new(this._cancel.Bounds.X - 100, buttons, 96, _ButtonHeight);
  }

}
