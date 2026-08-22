using System.Globalization;
using System.Text;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What a binary on disk is: its headers, its sections, what it needs and what it publishes
/// (PRD §53, §35).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and structurally so.</b> There is no button here that writes to the file and there
/// is not going to be one: §53's last line is that this is a viewer and not a patcher, and §4 does
/// not ship a debugger or a memory reverse-engineering suite. Everything on every page comes from
/// reading a file that somebody named.
/// </para>
/// <para>
/// A rail of pages rather than a tab strip, because there are sixteen of them and a tab strip with
/// sixteen captions on it is a strip somebody scrolls sideways looking for the one they wanted — the
/// same argument §25.4 makes for the Inspect menu.
/// </para>
/// <para>
/// <b>The strings page is not entered on selecting it.</b> Every other page reads a few kilobytes of
/// structure; that one reads every byte of the file, which for a runtime image is seconds of disk.
/// So it opens with the cost written on the button and nothing scanned, which is §35's requirement
/// that the warning arrive before the scan rather than after (PRD §5.4).
/// </para>
/// </remarks>
public sealed class BinaryInspectorWindow : Form {

  private const int _Margin = 10;
  private const int _RailWidth = 168;
  /// <summary>What one line of the note actually occupies, measured off a capture.</summary>
  private const int _LineHeight = 19;
  private const int _ButtonHeight = 28;
  private const int _ButtonGap = 8;

  private readonly string _path;
  private readonly ListBox _pages = new();
  private readonly TreeListView _rows = new();
  private readonly Label _note = new();
  private readonly Panel _buttons = new();
  private readonly Button _copy = new() { Text = "Copy page" };
  private readonly Button _save = new() { Text = "Save page…" };
  private readonly Button _scan = new() { Text = "Scan for text" };
  private readonly TextBox _match = new();
  private readonly Label _matchLabel = new() { Text = "matching" };

  /// <summary>
  /// §35's configurable minimum length, as a box beside the button rather than as a setting.
  /// </summary>
  /// <remarks>
  /// Four is the default because it is what <c>strings</c> uses; the number that suits a particular
  /// question is not, which is the whole reason the requirement says "configurable". A value that is
  /// not a number at all is read as the default rather than refused, because a scan is cheap to
  /// repeat and an error dialog over a typed character is not.
  /// </remarks>
  private readonly TextBox _minimum = new() { Text = "4" };

  private readonly Label _minimumLabel = new() { Text = "at least" };

  private BinaryPage _page = BinaryPage.Summary;

  /// <summary>
  /// A real empty view rather than <c>default</c>, whose headers and rows are both null.
  /// </summary>
  /// <remarks>
  /// The layout runs once before the first page is read, and a struct's zero has no lists on it at
  /// all — which took the capture leg down with a null reference where every test passed, because
  /// no test constructs the window and then measures it before showing anything.
  /// </remarks>
  private BinaryView _view = BinaryView.Empty("Summary", "nothing has been read yet");
  private bool _scanned;

  /// <summary>Set while the rail is being moved to match the page, so the event it raises is ignored.</summary>
  private bool _syncing;

  /// <summary>How many lines the note wrapped to, which is how much room the table gets.</summary>
  private int _noteLines = 1;

  public BinaryInspectorWindow(string path) {
    ArgumentNullException.ThrowIfNull(path);

    this._path = path;
    this.Text = $"Binary inspector — {System.IO.Path.GetFileName(path)}";
    // Form.QuitsOnClose defaults to true because the first window shown owns the message loop; every
    // window that is not that one has to say so.
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 1080, 620);
    this.MinimumSize = new(640, 360);

    foreach (var page in BinaryInspector.Pages)
      this._pages.Items.Add(BinaryInspector.Title(page));

    this._pages.SelectedIndex = 0;
    this._pages.SelectedIndexChanged += (_, _) => this.Choose();

    this._rows.ShowColumnHeaders = true;
    this._copy.Click += (_, _) => Clipboard.SetText(this.Description);
    this._save.Click += (_, _) => this.Save();
    this._scan.Click += (_, _) => {
      this._scanned = true;
      this.ShowPage(this._page);
    };

    this.Controls.Add(this._rows);
    this.Controls.Add(this._pages);
    this.Controls.Add(this._note);
    this._buttons.Controls.Add(this._copy);
    this._buttons.Controls.Add(this._save);
    this._buttons.Controls.Add(this._matchLabel);
    this._buttons.Controls.Add(this._match);
    this._buttons.Controls.Add(this._minimumLabel);
    this._buttons.Controls.Add(this._minimum);
    this._buttons.Controls.Add(this._scan);
    this.Controls.Add(this._buttons);

    // Laid out by arithmetic rather than by anchoring, for the reason MainWindow's layout note
    // records: an anchored child inside a docked container here grows without bound.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
    this.ShowPage(BinaryPage.Summary);
  }

  /// <summary>Which page is open, for a test with no display to look at.</summary>
  public BinaryPage Page => this._page;

  /// <summary>The page as text: the note, then the table (PRD §95).</summary>
  public string Description => $"{this._note.Text}\n\n{this._view.Describe()}";

  private void Choose() {
    var index = this._pages.SelectedIndex;
    if (index >= 0 && index < BinaryInspector.Pages.Length)
      this.ShowPage(BinaryInspector.Pages[index]);
  }

  /// <summary>
  /// Opens the file, builds one page and shows it.
  /// </summary>
  /// <remarks>
  /// Opened per page rather than held open for the life of the window. The window is modeless and
  /// outlives any one sample of anything, and a file handle kept for as long as somebody leaves a
  /// window open is a handle on an image a package manager wants to replace (PRD §8.2).
  /// </remarks>
  public void ShowPage(BinaryPage page) {
    this._page = page;
    // The rail follows, because the page can be chosen from somewhere else: the capture leg asks for
    // one, and a rail still highlighting "Summary" over a section table is a window disagreeing with
    // itself about what it is showing — which is how the first capture of this window came out.
    // Guarded, because moving the selection raises the event that got us here.
    if (!this._syncing) {
      this._syncing = true;
      try {
        for (var i = 0; i < BinaryInspector.Pages.Length; ++i)
          if (BinaryInspector.Pages[i] == page) {
            this._pages.SelectedIndex = i;
            break;
          }
      } finally {
        this._syncing = false;
      }
    }

    using var inspector = BinaryInspector.Open(this._path);

    // The strings page is the one that costs the size of the file, so it says what it will cost and
    // waits to be told (PRD §5.4, §35).
    var wantsScan = page == BinaryPage.Strings;
    this._scan.Visible = wantsScan;
    this._match.Visible = wantsScan;
    this._matchLabel.Visible = wantsScan;
    this._minimum.Visible = wantsScan;
    this._minimumLabel.Visible = wantsScan;
    if (wantsScan) {
      this._scan.Text = $"Scan {Bytes(inspector.ScanCost)} for text";
      if (!this._scanned) {
        this._view = new(
          "Strings",
          ["Offset", "Encoding", "Length", "Text"],
          [],
          $"Nothing has been scanned. Reading this file for text means reading all "
          + $"{Bytes(inspector.ScanCost)} of it, which is why it is a button rather than something "
          + "that happens when you look at this page."
        );

        this.Fill();
        return;
      }

      this._view = inspector.Strings(TextScanOptions.Default with {
        Pattern = this._match.Text is { Length: > 0 } pattern ? pattern : null,
        MinimumLength = int.TryParse(this._minimum.Text, out var least) && least > 0
          ? least
          : TextScanOptions.Default.MinimumLength,
      });

      this.Fill();
      return;
    }

    this._scanned = false;
    this._view = inspector.View(page);
    this.Fill();
  }

  private void Fill() {
    var note = this._view.Note is { Length: > 0 } written
      ? written.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal)
      : $"{this._view.Rows.Count.ToString("N0", CultureInfo.InvariantCulture)} row(s).";

    // Wrapped here rather than by the label, which does not: a sentence wider than the window is a
    // sentence with its point cut off, and these notes are where "why is this page empty" lives.
    // Eight pixels to the character rather than seven: at seven the last line of a five-line note
    // came out one word past the right-hand edge, and a clipped explanation is worse than no room
    // for one.
    this._note.Text = Wrap(note, Math.Max(40, (this.Width - _RailWidth - (3 * _Margin)) / 8), out this._noteLines);

    // Columns are rebuilt per page: the sixteen pages have between two and eleven of them, and a grid
    // that kept the previous page's headers would label a symbol table with a section table's words.
    this._rows.Columns.Clear();
    for (var i = 0; i < this._view.Headers.Count; ++i) {
      var column = i;
      this._rows.Columns.Add(new(
        this._view.Headers[i],
        ColumnWidth(this._view.Headers[i], this._view.Headers.Count),
        node => node.Tag is string[] cells && column < cells.Length ? cells[column] : string.Empty
      ));
    }

    this._rows.Nodes.Clear();
    foreach (var row in this._view.Rows)
      this._rows.Nodes.Add(new TreeNode(row.Length > 0 ? row[0] : string.Empty) { Tag = row });

    this.ApplyLayout();
  }

  /// <summary>
  /// How wide one column wants to be, by what it holds rather than by where it is.
  /// </summary>
  /// <remarks>
  /// By the heading, because the wide column is not always the last one: on the sections page the
  /// last is an alignment and the second is a section name, and a rule that gave the remainder to
  /// whichever came last photographed <c>.note.gnu.bu…</c> beside four inches of "Align". A width
  /// here is what the column asks for; the list scrolls sideways for whatever does not fit, which is
  /// the same arrangement §11 gives the process table.
  /// </remarks>
  private static int ColumnWidth(string header, int columns) => header switch {
    "Field" => 190,
    "Value" or "Text" => columns <= 2 ? 760 : 420,
    "Name" or "Types" or "Forwards to" or "Library" or "Segment" => 220,
    "#" => 44,
    "Ordinal" or "Hint" or "Length" or "Align" or "Link" or "Info" or "Sections" => 70,
    "Encoding" or "Type" or "Kind" or "Binding" or "Scope" or "Visibility" or "Flags" => 110,
    _ => 130,
  };

  /// <summary>Breaks a note at word boundaries, and says how many lines it took.</summary>
  private static string Wrap(string text, int width, out int lines) {
    var line = new StringBuilder();
    var wrapped = new StringBuilder();
    lines = 1;
    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
      if (line.Length > 0 && line.Length + 1 + word.Length > width) {
        wrapped.Append(line).Append('\n');
        line.Clear();
        ++lines;
      }

      if (line.Length > 0)
        line.Append(' ');

      line.Append(word);
    }

    return wrapped.Append(line).ToString();
  }

  private static string Bytes(long value) => value >= 1024 * 1024
    ? (value / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB"
    : (value / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " kB";

  public void ApplyLayout() {
    var buttons = _ButtonHeight + (2 * _Margin);
    var height = Math.Max(120, this.Height - buttons);
    this._pages.Bounds = new(_Margin, _Margin, _RailWidth, height - (2 * _Margin));

    var left = _RailWidth + (2 * _Margin);
    var width = Math.Max(200, this.Width - left - _Margin);
    // The note takes as many lines as it wrapped to. A fixed band either clips a five-line
    // explanation or leaves a strip of nothing above every page whose note is one line.
    // A page with no rows on it is all note, and the grid is hidden rather than left as an empty
    // grey rectangle: on those pages the paragraph *is* the answer, and a table nobody can fill
    // beside it reads as a view that failed to load (PRD §72.3).
    var empty = this._view.Rows.Count == 0;
    var note = Math.Clamp(this._noteLines, 1, empty ? 16 : 5) * _LineHeight;
    this._note.Bounds = new(left, _Margin, width, note);
    this._rows.Visible = !empty;
    this._rows.Bounds = new(left, _Margin + note + 4, width, Math.Max(80, height - (2 * _Margin) - note - 4));
    this._buttons.Bounds = new(0, height, this.Width, buttons);

    var x = _Margin;
    foreach (var (control, w) in (ReadOnlySpan<(Control, int)>)[
      (this._copy, 110),
      (this._save, 120),
      (this._scan, 200),
      (this._minimumLabel, 62),
      (this._minimum, 50),
      (this._matchLabel, 70),
      (this._match, 200),
    ]) {
      control.Bounds = new(x, _Margin, w, _ButtonHeight);
      x += w + _ButtonGap;
    }
  }

  /// <summary>
  /// What the capture log records about this window (PRD §9.6).
  /// </summary>
  /// <remarks>
  /// The shape and never the file. The counts are the empty-table detector — a layout that lost its
  /// grid reports nought rows and photographs as a grey rectangle — and the name of a program on the
  /// capturing machine belongs in neither a log nor a repository (PRD §97).
  /// </remarks>
  public string DescribeForCapture() {
    var text = new StringBuilder();
    text.Append(CultureInfo.InvariantCulture, $"binary page: {this._view.Title}, {this._rows.Nodes.Count} row(s), {this._rows.Columns.Count} columns\n");
    return text.ToString();
  }

  private void Save() {
    var dialog = new SaveFileDialog {
      Title = $"Save the {this._view.Title.ToLowerInvariant()} of {System.IO.Path.GetFileName(this._path)}",
      FileName = $"{System.IO.Path.GetFileNameWithoutExtension(this._path)}-{this._view.Title.ToLowerInvariant().Replace(' ', '-')}.txt",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName is not { Length: > 0 } path)
      return;

    try {
      File.WriteAllText(path, this.Description);
    } catch (IOException e) {
      MessageBox.Show(e.Message, "Process Manager");
    } catch (UnauthorizedAccessException e) {
      MessageBox.Show(e.Message, "Process Manager");
    }
  }

}
