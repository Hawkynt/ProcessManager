using Hawkynt.ProcessManager.Query;
using Hawkynt.NativeForms;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Picks which columns the process list shows (PRD §7.1).
/// </summary>
/// <remarks>
/// A checked list rather than the drag-and-drop header menu the reference tools use: the header has
/// no context menu of its own to hang one on, and a dialog that lists every column with what it
/// costs is arguably clearer anyway.
/// <para>
/// The columns that are showing come first, in the order they are in, and the rest follow in
/// registry order. That is not cosmetic: columns can be reordered now, and a chooser that listed
/// everything in registry order would silently throw that order away every time somebody ticked one
/// more column (PRD §11).
/// </para>
/// </remarks>
public sealed class ColumnChooser : Form {

  private const int _Margin = 12;
  private const int _ButtonHeight = 28;

  private readonly CheckedListBox _list = new();
  private readonly List<ProcessField> _order = [];
  private readonly Button _ok = new() { Text = "OK" };
  private readonly Button _cancel = new() { Text = "Cancel" };

  /// <param name="visible">
  /// The columns that are showing, in the order they are showing in. That order is preserved: the
  /// dialog lists them first and hands them back the same way round.
  /// </param>
  public ColumnChooser(IReadOnlyCollection<ProcessField> visible) {
    ArgumentNullException.ThrowIfNull(visible);

    this.Text = "Select columns";
    // A secondary window closing must not take the program with it. Form.QuitsOnClose defaults to
    // true because the first window shown owns the message loop; every window that is not that one
    // has to say so.
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 380, 560);

    foreach (var field in visible)
      this.Add(field, ticked: true);

    foreach (var info in ColumnSet.All)
      if (!visible.Contains(info.Id))
        this.Add(info.Id, ticked: false);

    this._ok.Click += (_, _) => {
      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();

    this.Controls.Add(this._list);
    this.Controls.Add(this._ok);
    this.Controls.Add(this._cancel);

    // The dialog is resizable, so the list has to be told to follow. Without this it stayed at the
    // size it was built at while the window grew around it, which made resizing look broken and left
    // the list showing a fraction of the columns it holds.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  private void Add(ProcessField field, bool ticked) {
    this._order.Add(field);
    this._list.Items.Add(ColumnSet.Info(field).Header);
    this._list.SetItemChecked(this._list.Items.Count - 1, ticked);
  }

  /// <summary>
  /// The list fills the dialog above the buttons, which sit at the bottom right.
  /// </summary>
  public void ApplyLayout() {
    var width = Math.Max(200, this.Width - (2 * _Margin));
    var buttonTop = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);

    this._list.Bounds = new(_Margin, _Margin, width, buttonTop - _Margin - 10);
    this._cancel.Bounds = new(this.Width - _Margin - 80, buttonTop, 80, _ButtonHeight);
    this._ok.Bounds = new(this._cancel.Bounds.X - 90, buttonTop, 80, _ButtonHeight);
  }

  /// <summary>True when the dialog was closed with OK.</summary>
  public bool Accepted { get; private set; }

  /// <summary>
  /// What was ticked. The name column is forced on: a list whose rows have no name is a list of
  /// numbers.
  /// </summary>
  public List<ProcessField> Selection {
    get {
      var result = new List<ProcessField>();
      for (var i = 0; i < this._order.Count; ++i)
        if (this._list.GetItemChecked(i))
          result.Add(this._order[i]);

      if (!result.Contains(ProcessField.Name))
        result.Insert(0, ProcessField.Name);

      return result;
    }
  }

}
