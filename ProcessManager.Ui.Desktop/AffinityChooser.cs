using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Which logical processors a process may run on (PRD §26).
/// </summary>
/// <remarks>
/// <para>
/// One box per core, labelled with what the core actually is rather than only its number. On a
/// hybrid part "CPU 14" says nothing and "CPU 14 (E)" says which half of the machine you are pinning
/// to — and pinning a latency-sensitive process to the efficiency cores by accident is the whole
/// mistake this label exists to prevent.
/// </para>
/// <para>
/// An empty mask is refused rather than sent. The kernel refuses it too, but a dialog that lets
/// somebody clear every box and press OK has already wasted their time.
/// </para>
/// </remarks>
public sealed class AffinityChooser : Form {

  private const int _Margin = 12;
  private const int _RowHeight = 22;

  private readonly CheckedListBox _list = new();
  private readonly Button _ok = new() { Text = "OK" };
  private readonly Button _cancel = new() { Text = "Cancel" };
  private readonly Button _all = new() { Text = "All" };
  private readonly int _cores;

  public AffinityChooser(string processName, int pid, int cores, CpuTopology topology) {
    ArgumentNullException.ThrowIfNull(topology);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cores);

    this._cores = Math.Min(cores, 64);
    this.Text = $"Affinity — {processName} ({pid})";
    this.QuitsOnClose = false;

    var kinds = new Dictionary<int, CoreKind>();
    foreach (var core in topology.Cores)
      kinds[core.Logical] = core.Kind;

    for (var core = 0; core < this._cores; ++core) {
      var suffix = kinds.TryGetValue(core, out var kind) && kind != CoreKind.Unknown
        ? kind == CoreKind.Performance ? " (P)" : " (E)"
        : string.Empty;

      this._list.Items.Add($"CPU {core}{suffix}");
      // Everything ticked to begin with: the mask the dialog opens on has to be the one the process
      // already has, and a process nobody has pinned may run anywhere.
      this._list.SetItemChecked(core, true);
    }

    this._ok.Click += (_, _) => {
      if (this.Mask == 0) {
        MessageBox.Show("A process has to be allowed at least one core.", "Process Manager");
        return;
      }

      this.Accepted = true;
      this.Close();
    };

    this._cancel.Click += (_, _) => this.Close();
    this._all.Click += (_, _) => {
      for (var core = 0; core < this._cores; ++core)
        this._list.SetItemChecked(core, true);
    };

    this.Controls.Add(this._list);
    this.Controls.Add(this._all);
    this.Controls.Add(this._ok);
    this.Controls.Add(this._cancel);

    var height = Math.Min(520, (this._cores * _RowHeight) + 120);
    this.Bounds = new(0, 0, 320, height);
    this.MinimumSize = new(260, 220);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>True when the dialog was closed with OK.</summary>
  public bool Accepted { get; private set; }

  /// <summary>The cores that are ticked, as the bit mask the scheduler takes.</summary>
  public ulong Mask {
    get {
      var mask = 0ul;
      for (var core = 0; core < this._cores; ++core)
        if (this._list.GetItemChecked(core))
          mask |= 1ul << core;

      return mask;
    }
  }

  public void ApplyLayout() {
    var width = Math.Max(180, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - 28);

    this._list.Bounds = new(_Margin, _Margin, width, buttons - _Margin - 10);
    this._all.Bounds = new(_Margin, buttons, 60, 28);
    this._cancel.Bounds = new(this.Width - _Margin - 80, buttons, 80, 28);
    this._ok.Bounds = new(this._cancel.Bounds.X - 90, buttons, 80, 28);
  }

}
