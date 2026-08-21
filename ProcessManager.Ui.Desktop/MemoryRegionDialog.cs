using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Everything one mapping is, in a box — and an honest account of the half of §25.5 that is refused.
/// </summary>
/// <remarks>
/// <para>
/// The map shows a row per mapping and cannot show what a row one line high has no room for: the
/// whole of a path, the whole of the kernel's <c>VmFlags</c> line, and the eight counters that say
/// what the region is costing spelled out rather than abbreviated to four characters.
/// </para>
/// <para>
/// It also answers the question the map is usually opened to ask about a file-backed row — how many
/// other mappings of the same file this process has, and where they run to. That is the §31 fold seen
/// from the other side: the module list turns a library's five consecutive mappings into one row
/// because it is answering "which code is loaded", and this is answering "what is at this address",
/// where the fold would destroy the answer (PRD §31, §34).
/// </para>
/// <para>
/// <b>Inspecting the region is not reading its bytes, and this box says so outright.</b>
/// <c>process_vm_readv</c> and <c>/proc/[pid]/mem</c> are both governed by
/// <c>PTRACE_MODE_ATTACH</c>, and every current distribution ships Yama's <c>ptrace_scope</c> at 1 —
/// so a hex view here would be a screen that refuses on the machine of anybody likely to open it. The
/// half that needs no such permission is which regions there are and what they are, and that is what
/// this shows (PRD §25.5).
/// </para>
/// </remarks>
public sealed class MemoryRegionDialog : Form {

  private const int _Margin = 12;

  /// <summary>What one line of the facts label occupies, measured off a capture.</summary>
  private const int _LineHeight = 19;

  private const int _ButtonHeight = 28;

  private readonly Label _facts = new();
  private readonly Button _copy = new() { Text = "Copy" };
  private readonly Button _file = new() { Text = "File properties…" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly string? _path;
  private readonly IProcessActions? _actions;
  private readonly Func<string, ImageTrust>? _verify;

  /// <param name="siblings">
  /// Every mapping the page is showing, so the box can say how many of them are of the same file.
  /// Handed in rather than read again: the page has just paid for a page-table walk, and paying for a
  /// second one to answer a question about a row it already holds would be exactly what §5.4 forbids.
  /// </param>
  /// <param name="verify">
  /// Asks whoever shipped the backing file whether these are still its bytes, on the one read the
  /// hash already pays for. Null where nothing can answer (PRD §70).
  /// </param>
  public MemoryRegionDialog(
    in MemoryRegionRecord region,
    IReadOnlyList<MemoryRegionRecord>? siblings = null,
    IProcessActions? actions = null,
    Func<string, ImageTrust>? verify = null
  ) {
    this._path = region.Path;
    this._actions = actions;
    this._verify = verify;

    this.Text = $"Mapping — {Humanize.Address(region.Start)}";
    // Form.QuitsOnClose defaults to true because the first window shown owns the message loop; every
    // window that is not that one has to say so.
    this.QuitsOnClose = false;

    var lines = new List<string> {
      // start-end, the way maps writes it, so a range pasted into another tool matches the file it
      // came from (PRD §5.3).
      $"range       {Humanize.Address(region.Start)}-{Humanize.Address(region.End)}",
      $"size        {Humanize.Bytes(region.Size)}",
      $"protection  {Protection(region.Permissions)}",
      $"kind        {Kind(region.Kind)}",
      $"path        {Path(in region)}",
      $"offset      {Humanize.Address(region.FileOffset)}",
      $"device      {region.Device ?? "none — this mapping is on no device"}",
      $"inode       {Humanize.Count(region.Inode)}",
      $"resident    {Humanize.Bytes(region.ResidentBytes)}",
      // The share of the resident pages this process is actually answerable for. The only resident
      // figure that may be summed across processes without counting a shared page several times.
      $"proportional {Humanize.Bytes(region.ProportionalBytes)}",
      // What freeing this would cost: written to, belonging to nobody else, so it has to go to swap
      // rather than simply being dropped.
      $"private dirty {Humanize.Bytes(region.PrivateDirtyBytes)}",
      $"shared dirty {Humanize.Bytes(region.SharedDirtyBytes)}",
      // How much of the mapping no longer matches its file — nought for a clean file mapping, all of
      // it for an anonymous one, and the copy-on-write count for everything in between.
      $"anonymous   {Humanize.Bytes(region.AnonymousBytes)}",
      $"swapped     {Humanize.Bytes(region.SwapBytes)}",
      $"locked      {Humanize.Bytes(region.LockedBytes)}",
      $"huge pages  {Humanize.Bytes(region.HugePageBytes)}",
      // Verbatim and never translated: this is where the answers §34 asks for that have no counter of
      // their own live, and inventing English for a set the kernel extends every few releases would
      // go stale in silence.
      $"VmFlags     {region.Flags ?? Humanize.Placeholder(UnknownReason.NotSampledYet)}",
    };

    if (Fold(in region, siblings) is { } fold)
      lines.Add(fold);

    // Said and not left out. A box about a region that showed no way to look at its contents reads as
    // one that failed to; this is a refusal with a reason behind it (PRD §25.5, §72.3).
    lines.Add("contents    not readable from here — process_vm_readv and /proc/[pid]/mem are both");
    lines.Add("            governed by PTRACE_MODE_ATTACH, and Yama's ptrace_scope is 1 on every");
    lines.Add("            current distribution, where a process may read only its own descendants");

    this._facts.Text = string.Join('\n', lines);

    this._copy.Click += (_, _) => Clipboard.SetText(this._facts.Text);
    this._file.Click += (_, _) => this.ShowFileProperties();
    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._facts);
    this.Controls.Add(this._copy);
    this.Controls.Add(this._file);
    this.Controls.Add(this._close);

    // Sized to what it holds rather than to a guess: a fixed height photographed as a box with eighty
    // pixels of nothing in the middle of it, which is the defect the file properties box records.
    this.Bounds = new(0, 0, 720, (lines.Count * _LineHeight) + 96);
    this.MinimumSize = new(460, 240);
    // Laid out by arithmetic, for the reason MainWindow's layout note records: a child anchored
    // inside a docked container here grows without bound.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>What the box says, for a test with no display to read it off.</summary>
  public string Description => this._facts.Text;

  /// <summary>
  /// How many mappings of the same file this process has, and what they span.
  /// </summary>
  /// <remarks>
  /// Null for a region with no file, and for a file mapped only once — a line saying "1 mapping" would
  /// be noise on nearly every anonymous row on the page. What makes the count worth showing is that
  /// it is the number §31 folds away: a library's read-only, executable, read-write and relocation
  /// segments are four rows here and one there, and a reader comparing the two pages needs to know
  /// which of them is doing the folding.
  /// </remarks>
  private static string? Fold(in MemoryRegionRecord region, IReadOnlyList<MemoryRegionRecord>? siblings) {
    if (region.Path is not { Length: > 0 } path || siblings is null)
      return null;

    var count = 0;
    var lowest = ulong.MaxValue;
    var highest = 0ul;
    foreach (var other in siblings) {
      if (!string.Equals(other.Path, path, StringComparison.Ordinal))
        continue;

      ++count;
      if (other.Start < lowest)
        lowest = other.Start;

      if (other.End > highest)
        highest = other.End;
    }

    return count < 2
      ? null
      : $"this file   {count.ToString(CultureInfo.InvariantCulture)} mappings in this process, "
        + $"{Humanize.Address(lowest)}-{Humanize.Address(highest)} — the modules view folds them into one row";
  }

  /// <summary>
  /// The four characters, and then what they mean.
  /// </summary>
  /// <remarks>
  /// Both, rather than one or the other. The letters are what <c>maps</c> writes and what somebody
  /// checking this against the file needs; the words are what the reader of a properties box came for.
  /// </remarks>
  private static string Protection(MapPermissions permissions) {
    var letters = MapsParser.Format(permissions);
    if (letters.Length == 0)
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    var what = new List<string>(3);
    if ((permissions & MapPermissions.Read) != 0)
      what.Add("read");

    if ((permissions & MapPermissions.Write) != 0)
      what.Add("write");

    if ((permissions & MapPermissions.Execute) != 0)
      what.Add("execute");

    // A region with none of the three is the interesting case rather than an empty one: that is what
    // a guard page is, and a blank here would read as a value nobody managed to read.
    var access = what.Count > 0 ? string.Join(", ", what) : "no access at all — a guard region";
    return $"{letters} — {access}, {((permissions & MapPermissions.Shared) != 0 ? "shared" : "private")}";
  }

  private static string Kind(MemoryRegionKind kind) => kind switch {
    MemoryRegionKind.FileBacked => "file",
    MemoryRegionKind.Anonymous => "anonymous — no file behind it",
    MemoryRegionKind.Heap => "heap — the brk heap, and only the one the kernel grows",
    MemoryRegionKind.Stack => "stack — the initial thread's, the only one Linux still labels",
    MemoryRegionKind.SharedMemory => "shared memory — named, and on no disk",
    MemoryRegionKind.Device => "device",
    MemoryRegionKind.KernelProvided => "kernel-provided — present in every process and belonging to none",
    MemoryRegionKind.Pseudo => "a bracketed name this build does not recognise",
    _ => Humanize.Placeholder(UnknownReason.NotSampledYet),
  };

  private static string Path(in MemoryRegionRecord region) {
    if (region.Path is not { Length: > 0 } path)
      return "none — anonymous memory is on no disk";

    // The kernel's own annotation, and it is the whole finding for this row: the file is gone and the
    // mapping is still running the bytes it had, which is the ordinary state of a program whose
    // package was upgraded underneath it.
    return region.IsDeleted ? path + " (deleted — the file was unlinked while it was mapped)" : path;
  }

  /// <summary>
  /// What the file behind the mapping is, on disk.
  /// </summary>
  /// <remarks>
  /// Only for a mapping that has one, and only for a file that is still there. An anonymous region,
  /// the heap and <c>[vdso]</c> are on no disk, and a deleted file has nothing left to describe —
  /// offering a box that could only say so is worse than saying so here.
  /// </remarks>
  private void ShowFileProperties() {
    if (this._path is not { Length: > 0 } path) {
      MessageBox.Show(
        "This mapping has no file behind it. Anonymous memory, the heap and the regions the kernel "
        + "provides are not on any disk.",
        "Process Manager"
      );

      return;
    }

    if (!File.Exists(path)) {
      MessageBox.Show(
        $"{path} is not on disk any more. The mapping is still the old file's contents, which is what "
        + "the deleted mark on the row means; there is nothing left to describe.",
        "Process Manager"
      );

      return;
    }

    new FilePropertiesDialog(path, [], this._actions, this._verify).ShowDialog();
  }

  public void ApplyLayout() {
    var width = Math.Max(300, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);

    this._facts.Bounds = new(_Margin, _Margin, width, Math.Max(_LineHeight, buttons - _Margin - 6));
    this._copy.Bounds = new(_Margin, buttons, 90, _ButtonHeight);
    this._file.Bounds = new(_Margin + 100, buttons, 150, _ButtonHeight);
    this._close.Bounds = new(this.Width - _Margin - 80, buttons, 80, _ButtonHeight);
  }

}
