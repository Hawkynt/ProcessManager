using Hawkynt.NativeForms;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Picks which columns the process list shows (PRD §7.1).
/// </summary>
/// <remarks>
/// A checked list rather than the drag-and-drop header menu the reference tools use: the header has
/// no context menu of its own to hang one on, and a dialog that lists every column with what it costs
/// is arguably clearer anyway. Order is fixed — reordering columns is not implemented.
/// </remarks>
public sealed class ColumnChooser : Form {

  private readonly CheckedListBox _list = new();
  private readonly List<DesktopColumn> _order = [];

  public ColumnChooser(IReadOnlyCollection<DesktopColumn> visible) {
    ArgumentNullException.ThrowIfNull(visible);

    this.Text = "Select columns";
    this.Bounds = new(0, 0, 380, 460);

    this._list.Bounds = new(12, 12, 344, 372);
    foreach (var info in ColumnSet.All) {
      this._order.Add(info.Column);
      this._list.Items.Add(info.Header);
      this._list.SetItemChecked(this._list.Items.Count - 1, visible.Contains(info.Column));
    }

    var ok = new Button { Text = "OK", Bounds = new(180, 394, 80, 28) };
    var cancel = new Button { Text = "Cancel", Bounds = new(270, 394, 80, 28) };
    ok.Click += (_, _) => {
      this.Accepted = true;
      this.Close();
    };

    cancel.Click += (_, _) => this.Close();

    this.Controls.Add(this._list);
    this.Controls.Add(ok);
    this.Controls.Add(cancel);
  }

  /// <summary>True when the dialog was closed with OK.</summary>
  public bool Accepted { get; private set; }

  /// <summary>
  /// What was ticked. The name column is forced on: a list whose rows have no name is a list of
  /// numbers.
  /// </summary>
  public List<DesktopColumn> Selection {
    get {
      var result = new List<DesktopColumn>();
      for (var i = 0; i < this._order.Count; ++i)
        if (this._list.GetItemChecked(i))
          result.Add(this._order[i]);

      if (!result.Contains(DesktopColumn.Name))
        result.Insert(0, DesktopColumn.Name);

      return result;
    }
  }

}
