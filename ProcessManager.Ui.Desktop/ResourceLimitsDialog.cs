using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Every ceiling on a process, and its standing with the out-of-memory killer (PRD §25.2, §25.5).
/// </summary>
/// <remarks>
/// <para>
/// Each ceiling carries what running into it actually does, because that is never the same thing
/// twice: <c>RLIMIT_CPU</c> sends a signal, <c>RLIMIT_NOFILE</c> fails an <c>open</c>, and
/// <c>RLIMIT_AS</c> fails an allocation the program probably does not check. A column of numbers
/// without them is a sheet nobody can act on.
/// </para>
/// <para>
/// <b>Lowering a hard limit cannot be undone</b> without <c>CAP_SYS_RESOURCE</c> — the kernel
/// permits it to anybody and permits nobody to raise it again — which makes it the one irreversible
/// thing here and the one the confirmation names outright (PRD §5.5).
/// </para>
/// <para>
/// The out-of-memory adjustment sits under the ceilings and is not one of them. It limits nothing
/// and reserves nothing; it decides <em>which</em> process dies when the machine has run out, so
/// lowering one process's score points the killer at somebody else's.
/// </para>
/// </remarks>
public sealed class ResourceLimitsDialog : Form {

  private const int _Margin = 12;
  private const int _RowHeight = 19;
  private const int _ButtonHeight = 28;

  private readonly TreeListView _list = new();
  private readonly Label _oomLabel = new();
  private readonly TextBox _oom = new();
  private readonly Button _applyOom = new() { Text = "Set" };
  private readonly Label _softLabel = new() { Text = "Soft" };
  private readonly TextBox _soft = new();
  private readonly Label _hardLabel = new() { Text = "Hard" };
  private readonly TextBox _hard = new();
  private readonly Button _apply = new() { Text = "Apply to the selected limit" };
  private readonly Button _close = new() { Text = "Close" };

  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private readonly ProcessKey _key;
  private readonly string _name;

  public ResourceLimitsDialog(ISystemProbe probe, IProcessActions? actions, ProcessKey key, string processName) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this._actions = actions;
    this._key = key;
    this._name = processName;

    this.Text = $"Limits — {processName} ({key.Pid})";
    this.QuitsOnClose = false;

    this._list.ShowColumnHeaders = true;
    this._list.ItemHeight = 17;
    this._list.Columns.Add(new("Limit", 170, node => ((string[])node.Tag!)[0]));
    this._list.Columns.Add(new("Now", 120, node => ((string[])node.Tag!)[1]));
    this._list.Columns.Add(new("Ceiling", 120, node => ((string[])node.Tag!)[2]));
    this._list.Columns.Add(new("What happens when it is reached", 620, node => ((string[])node.Tag!)[3]));
    this._list.AfterSelect += (_, _) => this.ShowSelected();

    this._applyOom.Click += (_, _) => this.ApplyOom();
    this._apply.Click += (_, _) => this.ApplyLimit();
    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._list);
    this.Controls.Add(this._oomLabel);
    this.Controls.Add(this._oom);
    this.Controls.Add(this._applyOom);
    this.Controls.Add(this._softLabel);
    this.Controls.Add(this._soft);
    this.Controls.Add(this._hardLabel);
    this.Controls.Add(this._hard);
    this.Controls.Add(this._apply);
    this.Controls.Add(this._close);

    this.Bounds = new(0, 0, 1000, (16 * _RowHeight) + 190);
    this.MinimumSize = new(560, 340);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
    this.Reload();
  }

  /// <summary>What the box says, for a test and for the capture log.</summary>
  public string Description {
    get {
      var text = new System.Text.StringBuilder();
      text.AppendLine(this.Text);
      foreach (var node in this._list.Nodes)
        if (node.Tag is string[] cells)
          text.AppendLine(string.Join("  ", cells));

      text.AppendLine(this._oomLabel.Text);
      return text.ToString();
    }
  }

  /// <summary>How many ceilings the sheet is showing, for a test with no display to count them on.</summary>
  public int RowCount => this._list.Nodes.Count;

  /// <summary>
  /// Reads the whole sheet again.
  /// </summary>
  /// <remarks>
  /// After every change rather than patching the row that was edited: the kernel may have clamped
  /// what was asked for, and a row showing what was requested rather than what was applied is the
  /// one way this dialog could lie.
  /// </remarks>
  private void Reload() {
    this._list.Nodes.Clear();
    if (this._probe.DescribeResourceLimits(this._key) is not { } limits) {
      this._oomLabel.Text = "Nothing here is readable: this is another user's process, or one that has ended.";
      return;
    }

    foreach (var limit in limits.Limits) {
      if (ResourceLimits.Of(limit.Kind) is not { } definition)
        continue;

      var cells = new[] {
        definition.Name,
        ResourceLimits.Format(definition.Unit, limit.Soft),
        ResourceLimits.Format(definition.Unit, limit.Hard),
        definition.Consequence,
      };

      this._list.Nodes.Add(new TreeNode(definition.Name) { Tag = cells });
    }

    // The adjustment and the badness are different questions and are shown as two: one is what
    // somebody asked for, the other is what the kernel would actually do about it.
    this._oomLabel.Text =
      $"Out of memory — adjustment {Adjustment(limits.OomScoreAdjustment)}, badness now "
      + $"{limits.OomScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not readable"}. "
      + "The killer picks the highest badness on the machine; this changes who dies, not how much may be used.";

    this._oom.Text = limits.OomScoreAdjustment?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    this.ShowSelected();
  }

  private static string Adjustment(int? value) => value switch {
    null => "not readable",
    ProcessLimits.OomAdjustmentMinimum => "-1000, exempt",
    0 => "0, untouched",
    _ => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
  };

  /// <summary>Puts the selected row's two values in the boxes, so an edit starts from what is true.</summary>
  private void ShowSelected() {
    if (this._list.SelectedNode?.Tag is not string[] cells)
      return;

    this._soft.Text = cells[1];
    this._hard.Text = cells[2];
  }

  private void ApplyLimit() {
    if (this._list.SelectedNode?.Tag is not string[] cells) {
      MessageBox.Show("Choose the limit to change first.", "Process Manager");
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (!ResourceLimits.TryParse(cells[0], out var kind)) {
      MessageBox.Show($"{cells[0]} is not a limit this build can set.", "Process Manager");
      return;
    }

    if (!ResourceLimits.TryParseValue(this._soft.Text, out var soft) || !ResourceLimits.TryParseValue(this._hard.Text, out var hard)) {
      MessageBox.Show(
        "A limit is a number, a number with a suffix such as 8MiB, or the word unlimited.",
        "Process Manager"
      );

      return;
    }

    // Named, targeted, and with the consequence spelled out — and the consequence here is not the
    // usual one: lowering a hard limit is the only change on this sheet that cannot be undone
    // without a capability nobody running a desktop has (PRD §5.5, §90).
    var irreversible = Lowers(cells[2], hard);
    var question =
      $"Set {ResourceLimits.Name(kind)} on {this._name} (PID {this._key.Pid}) to "
      + $"{this._soft.Text} of {this._hard.Text}?";

    var consequence = irreversible
      ? "Lowering the ceiling cannot be undone: the kernel lets anybody lower a hard limit and nobody "
        + "raise it again without CAP_SYS_RESOURCE, so this process is held there for the rest of its life."
      : "The soft limit is the one the kernel enforces; the process may raise it again up to its ceiling on its own.";

    if (MessageBox.Show($"{question}\n\n{consequence}", "Process Manager", MessageBoxButtons.YesNo) != DialogResult.Yes)
      return;

    var result = this._actions.SetResourceLimit(this._key, kind, soft, hard);
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");

    this.Reload();
  }

  /// <summary>
  /// Whether the ceiling is being brought down, which is the irreversible direction.
  /// </summary>
  /// <remarks>
  /// Compared against the text the row is showing rather than against a value read again, so that
  /// what the confirmation claims is what the person is looking at. Unlimited is not a quantity and
  /// anything at all is below it.
  /// </remarks>
  private static bool Lowers(string current, ulong? wanted) {
    if (string.Equals(current, "unlimited", StringComparison.Ordinal))
      return wanted is not null;

    return wanted is { } value && ResourceLimits.TryParseValue(current, out var was) && was is { } previous && value < previous;
  }

  private void ApplyOom() {
    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (!int.TryParse(this._oom.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var adjustment)) {
      MessageBox.Show("The out-of-memory adjustment is a whole number from -1000 to 1000.", "Process Manager");
      return;
    }

    var question = $"Set the out-of-memory adjustment of {this._name} (PID {this._key.Pid}) to {adjustment}?";
    var consequence = adjustment < 0
      ? "A negative adjustment makes the kernel less likely to choose this process when memory runs out, "
        + "which makes it more likely to choose something else instead. It reserves nothing and limits nothing."
      : "A positive adjustment volunteers this process to be killed first when memory runs out. "
        + "It reserves nothing and limits nothing.";

    if (MessageBox.Show($"{question}\n\n{consequence}", "Process Manager", MessageBoxButtons.YesNo) != DialogResult.Yes)
      return;

    var result = this._actions.SetOomScoreAdjustment(this._key, adjustment);
    if (!result.Succeeded)
      MessageBox.Show(result.Detail ?? result.Outcome.ToString(), "Process Manager");

    this.Reload();
  }

  public void ApplyLayout() {
    var width = Math.Max(360, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);
    var editors = buttons - _ButtonHeight - 10;
    var oomRow = editors - _ButtonHeight - 10;
    var oomText = oomRow - (_RowHeight * 2) - 4;

    this._list.Bounds = new(_Margin, _Margin, width, Math.Max(_RowHeight, oomText - _Margin - 6));
    this._oomLabel.Bounds = new(_Margin, oomText, width, _RowHeight * 2);
    this._oom.Bounds = new(_Margin, oomRow, 90, 24);
    this._applyOom.Bounds = new(_Margin + 100, oomRow, 60, _ButtonHeight);

    this._softLabel.Bounds = new(_Margin, editors + 4, 36, _RowHeight);
    this._soft.Bounds = new(_Margin + 40, editors, 120, 24);
    this._hardLabel.Bounds = new(_Margin + 172, editors + 4, 40, _RowHeight);
    this._hard.Bounds = new(_Margin + 216, editors, 120, 24);
    this._apply.Bounds = new(_Margin + 348, editors, 220, _ButtonHeight);

    this._close.Bounds = new(this.Width - _Margin - 90, buttons, 90, _ButtonHeight);
  }

}
