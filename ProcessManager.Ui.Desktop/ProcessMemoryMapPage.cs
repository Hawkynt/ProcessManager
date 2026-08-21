using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One process's address space, mapping by mapping (PRD §34).
/// </summary>
/// <remarks>
/// <para>
/// The page the module list is not. §31 answers "which code is loaded" and folds a library's five
/// consecutive mappings into one row; this answers "what is at this address and what may be done to
/// it", and the fold would destroy the answer — a read-only segment and the writable one above it
/// become a single row claiming both.
/// </para>
/// <para>
/// Read when somebody opens the page and re-read when they ask, never on the tick. The counters come
/// from <c>smaps</c>, which makes the kernel walk the whole page table of the process; doing that once
/// a second for a browser would make the monitor the most expensive thing on the machine (PRD §5.4).
/// The heading carries the time it was collected, for the same reason the other on-demand lists do.
/// </para>
/// </remarks>
internal sealed class ProcessMemoryMapPage {

  private readonly Panel _panel = new();
  private readonly Panel _buttons = new();
  private readonly Button _reread = new() { Text = "Re-read" };
  /// <summary>Where the address is, for the row menu. A column index in one place, not four.</summary>
  private const int _StartColumn = 0;

  private const int _EndColumn = 1;

  private const int _PathColumn = 5;

  private readonly RecordTable _table = new(
    // Ordered by what a reader came for, and the order was decided by a photograph rather than by
    // taste: with the path last, the first screenful of a 1280-pixel window ended at the inode and
    // the one column that says what a region *is* was two thousand pixels off the right-hand edge.
    // So the identity comes sixth, before everything the region is costing (PRD §11).
    // A hex address is fourteen characters at this font; at 130 the start ran into the end.
    ("Start", 150),
    ("End", 150),
    ("Size", 84),
    ("Perm", 52),
    ("Kind", 108),
    ("Path", 400),
    ("Resident", 84),
    // What freeing this would actually cost: resident, written to, and belonging to nobody else, so
    // it has to go to swap rather than simply being dropped.
    ("Private dirty", 96),
    ("Proportional", 96),
    ("Anonymous", 90),
    ("Swap", 76),
    ("Huge pages", 92),
    ("Locked", 76),
    ("Offset", 88),
    ("Device", 60),
    ("Inode", 84),
    // The kernel's own two-letter codes, unabbreviated, and last so that the table stretches into
    // them. This is where the answers §34 asks for that have no counter of their own live — gd for a
    // guard region, ht for huge pages, nr for memory with no swap reserved, dd for a region left out
    // of a core dump (PRD §5.3).
    ("VmFlags", 210)
  );

  private readonly ISystemProbe _probe;
  private readonly IProcessActions? _actions;
  private MemoryMapReading _reading = MemoryMapReading.NotImplemented;

  public ProcessMemoryMapPage(ISystemProbe probe, IProcessActions? actions) {
    ArgumentNullException.ThrowIfNull(probe);
    this._probe = probe;
    this._actions = actions;

    this._table.Control.Dock = DockStyle.Fill;
    this._buttons.Dock = DockStyle.Bottom;
    this._buttons.Height = 40;
    this._reread.Click += (_, _) => this.Reread();
    this._buttons.Controls.Add(this._reread);

    this._table.ContextMenuStrip = this.BuildMenu();

    // The strip is added first so it claims its edge, and the table takes what is left.
    this._panel.Controls.Add(this._buttons);
    this._panel.Controls.Add(this._table.Control);
  }

  public Control Control => this._panel;

  /// <summary>Which process this page is about. Set once, by the window that owns it.</summary>
  public ProcessKey Key { get; set; }

  /// <summary>What the page says, for a test and for the capture log (PRD §9.6).</summary>
  public string Description => this._table.Description;

  /// <summary>How many mappings are listed — the empty-page detector a picture would show.</summary>
  public int RowCount => this._table.RowCount;

  /// <summary>The sentence above the list, which is the half that says why an empty one is empty.</summary>
  public string Heading => this._table.Heading;

  /// <summary>Whether the kernel answered, and which way it did not — for the tab-hiding preference.</summary>
  public MemoryMapState State => this._reading.State;

  public void Stretch() {
    const int Margin = 10;
    this._reread.Bounds = new(Margin, (this._buttons.Height - 28) / 2, 110, 28);
    this._table.Stretch();
  }

  /// <summary>
  /// Fills the page if it has not been filled yet.
  /// </summary>
  /// <remarks>
  /// Called when the page becomes the visible one. Once, and not every tick after that: a memory map
  /// is not a rate, and the cost of re-reading it is the size of the process rather than a constant
  /// (PRD §5.4). Whoever wants a newer one presses the button, which is the same bargain every other
  /// on-demand list in this program makes.
  /// </remarks>
  public void EnsureFilled() {
    if (this._filled)
      return;

    this.Reread();
  }

  private bool _filled;

  /// <summary>Re-reads the process's map and replaces the list.</summary>
  public void Reread() {
    this._filled = true;
    this._reading = this._probe.GetMemoryRegions(this.Key);
    var regions = this._reading.Regions;

    this._table.Fill(this.Describe(), regions.Count, index => {
      var region = regions[index];
      return [
        Humanize.Address(region.Start),
        Humanize.Address(region.End),
        Humanize.Bytes(region.Size),
        // Empty is a hole rather than "no access": a region with none is "---p" and still says which
        // of shared and private it is (PRD §72.3).
        MapsParser.Format(region.Permissions),
        Kind(region.Kind),
        // The kernel's annotation goes back on here and nowhere else: the path itself has to stay a
        // path for anything that wants to look the file up.
        region.Path is { Length: > 0 } path ? (region.IsDeleted ? path + " (deleted)" : path) : string.Empty,
        Humanize.Bytes(region.ResidentBytes),
        Humanize.Bytes(region.PrivateDirtyBytes),
        Humanize.Bytes(region.ProportionalBytes),
        Humanize.Bytes(region.AnonymousBytes),
        Humanize.Bytes(region.SwapBytes),
        Humanize.Bytes(region.HugePageBytes),
        Humanize.Bytes(region.LockedBytes),
        Humanize.Address(region.FileOffset),
        region.Device ?? string.Empty,
        Humanize.Count(region.Inode),
        region.Flags ?? Humanize.Placeholder(UnknownReason.NotSampledYet),
      ];
    });
  }

  /// <summary>
  /// The sentence above the list.
  /// </summary>
  /// <remarks>
  /// Never left to the row count. Nought mappings is the one answer that means two different things —
  /// a kernel thread has no address space and another user's process has one it will not show — and
  /// half an answer is a third (PRD §5.3, §72.3).
  /// </remarks>
  private string Describe() {
    if (this._reading.State != MemoryMapState.Available)
      return this._reading.Explain();

    var regions = this._reading.Regions;
    if (regions.Count == 0)
      return "Nothing is mapped. A kernel thread has no address space of its own, which is what this "
        + "looks like from outside.";

    var reserved = 0ul;
    var resident = 0ul;
    var residentKnown = false;
    foreach (var region in regions) {
      reserved += region.Size;
      if (!region.ResidentBytes.TryGetValue(out var value))
        continue;

      resident += value;
      residentKnown = true;
    }

    var text = string.Create(
      CultureInfo.InvariantCulture,
      $"{regions.Count} mappings · {Humanize.Bytes(reserved)} of address space"
    );

    // Reserved and resident are not the same quantity and are never added: a process reserves
    // gigabytes it has never touched, and the difference between the two numbers is the whole reason
    // "virtual size" is a bad answer to "how much memory is this using" (PRD §17).
    if (residentKnown)
      text += $" · {Humanize.Bytes(resident)} of it resident";

    text += $" · read at {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
    if (!this._reading.Detailed)
      text += " — the kernel would not walk this process's page tables, so the addresses are here and the counters are not";

    return text;
  }

  private static string Kind(MemoryRegionKind kind) => kind switch {
    MemoryRegionKind.FileBacked => "file",
    MemoryRegionKind.Anonymous => "anonymous",
    MemoryRegionKind.Heap => "heap",
    MemoryRegionKind.Stack => "stack",
    MemoryRegionKind.SharedMemory => "shared memory",
    MemoryRegionKind.Device => "device",
    MemoryRegionKind.KernelProvided => "kernel",
    MemoryRegionKind.Pseudo => "pseudo",
    _ => Humanize.Placeholder(UnknownReason.NotSampledYet),
  };

  #region what may be done with a row

  private ContextMenuStrip BuildMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(Item("Copy start address", () => this.Copy(_StartColumn)));
    menu.Items.Add(Item("Copy range", this.CopyRange));
    menu.Items.Add(Item("Copy path", () => this.Copy(_PathColumn)));
    menu.Items.Add(Item("Copy row", this.CopyRow));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(Item("File properties…", this.ShowFileProperties));
    return menu;

    static ToolStripMenuItem Item(string text, Action action) {
      var item = new ToolStripMenuItem(text);
      item.Click += (_, _) => action();
      return item;
    }
  }

  private void Copy(int column) {
    if (this._table.Selected is { } cells && column < cells.Length && cells[column].Length > 0)
      Clipboard.SetText(cells[column]);
  }

  /// <summary>
  /// The whole range, in the notation the kernel wrote it in.
  /// </summary>
  /// <remarks>
  /// <c>start-end</c> rather than <c>start+size</c>, because that is what <c>maps</c> says and what
  /// every tool that reads a memory map takes — a range pasted into <c>gdb</c> or <c>grep</c> has to
  /// match the file it came from (PRD §5.3).
  /// </remarks>
  private void CopyRange() {
    if (this._table.Selected is not { } cells || cells.Length <= _EndColumn)
      return;

    Clipboard.SetText($"{cells[_StartColumn]}-{cells[_EndColumn]}");
  }

  private void CopyRow() {
    if (this._table.Selected is { } cells)
      Clipboard.SetText(string.Join("  ", cells));
  }

  /// <summary>
  /// What the file behind a mapping is, on disk.
  /// </summary>
  /// <remarks>
  /// Only for a mapping that has one. An anonymous region, the heap and <c>[vdso]</c> have no file at
  /// all, and offering a dialog that could only say so is worse than saying so here.
  /// </remarks>
  private void ShowFileProperties() {
    if (this._table.Selected is not { } cells || cells.Length <= _PathColumn || cells[_PathColumn].Length == 0) {
      MessageBox.Show(
        "This mapping has no file behind it. Anonymous memory, the heap and the regions the kernel "
        + "provides are not on any disk.",
        "Process Manager"
      );

      return;
    }

    var path = cells[_PathColumn];
    if (path.EndsWith(" (deleted)", StringComparison.Ordinal)) {
      MessageBox.Show(
        $"{path[..^" (deleted)".Length]} was unlinked while it was mapped. The mapping is still the "
        + "old file's contents; there is nothing on disk to describe.",
        "Process Manager"
      );

      return;
    }

    new FilePropertiesDialog(path, [], this._actions).ShowDialog();
  }

  #endregion

}
