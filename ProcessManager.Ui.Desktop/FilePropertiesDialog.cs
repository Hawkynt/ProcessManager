using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// What a file on disk is: an executable, or a module a process has loaded (PRD §25.3, §25.6).
/// </summary>
/// <remarks>
/// <para>
/// The facts come free from the file system and are shown on opening. The hash does not — it reads
/// every byte — so it is a button, and a 300 MB runtime image is a second of disk that nobody paid
/// for unless they asked (PRD §70).
/// </para>
/// <para>
/// <b>Nothing here is a verdict.</b> A hash says what the bytes are and says nothing about whether
/// they are signed, trusted or known; those are separate operations and this program does not
/// conflate them. Local signature verification, trust-chain verification and reputation are §70's,
/// and none of them is implemented, so none of them is implied here either.
/// </para>
/// </remarks>
public sealed class FilePropertiesDialog : Form {

  private const int _Margin = 12;

  /// <summary>What one line of the facts label actually occupies, measured off a capture.</summary>
  private const int _LineHeight = 19;

  private const int _ButtonHeight = 28;

  private readonly Label _facts = new();
  private readonly Label _hash = new();
  private readonly Button _compute = new() { Text = "Compute SHA-256" };
  private readonly Button _reveal = new() { Text = "Open folder" };
  private readonly Button _close = new() { Text = "Close" };
  private readonly string _path;

  /// <param name="extra">
  /// Lines the caller already knows and this dialog cannot work out for itself — a module's image
  /// type and architecture come from the ELF header the modules view already read, and reading it
  /// again here would be a second open of the same file for the same answer.
  /// </param>
  /// <param name="actions">
  /// How "Open folder" starts the session's file manager, or null in a build with no actions — in
  /// which case the button says so rather than doing nothing.
  /// </param>
  public FilePropertiesDialog(string path, IReadOnlyList<KeyValuePair<string, string>>? extra = null, IProcessActions? actions = null) {
    ArgumentNullException.ThrowIfNull(path);

    this._path = path;
    this.Text = $"File — {System.IO.Path.GetFileName(path)}";
    // Form.QuitsOnClose defaults to true because the first window shown owns the message loop; every
    // window that is not that one has to say so.
    this.QuitsOnClose = false;

    var facts = FileFacts.Describe(path);
    var lines = new List<string> {
      $"path        {path}",
      $"size        {FileFactsFormatting.Size(in facts)}",
      $"modified    {FileFactsFormatting.Modified(in facts)}",
      $"permissions {facts.Permissions ?? "n/a"}",
    };

    if (extra is not null)
      foreach (var (name, value) in extra)
        lines.Add($"{name,-11} {value}");

    this._facts.Text = string.Join('\n', lines);
    this._hash.Text = "sha-256     not computed";

    this._compute.Click += (_, _) => {
      // Synchronous, and the button says what is happening first: this is somebody's deliberate
      // request for a whole-file read, not something that crept in behind a refresh.
      this._compute.Text = "Hashing…";
      this._hash.Text = $"sha-256     {FileDigest.Of(this._path).Display}";
      this._compute.Text = "Compute SHA-256";
      this._compute.Enabled = false;
    };

    this._reveal.Click += (_, _) => {
      if (actions is null) {
        MessageBox.Show("This build has no actions for this platform.", "Process Manager");
        return;
      }

      if (DesktopOpen.Reveal(this._path) is not { } request) {
        MessageBox.Show("This platform has no desktop opener to hand the folder to.", "Process Manager");
        return;
      }

      var result = actions.Launch(request);
      if (!result.Outcome.Succeeded)
        MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
    };

    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._facts);
    this.Controls.Add(this._hash);
    this.Controls.Add(this._compute);
    this.Controls.Add(this._reveal);
    this.Controls.Add(this._close);

    // Sized to what it holds. The first version reserved a row of 24 pixels per line and a fixed
    // 130 on top, which photographed as a box with eighty pixels of nothing in the middle of it.
    this.Bounds = new(0, 0, 720, (lines.Count * _LineHeight) + 116);
    this.MinimumSize = new(420, 200);
    // Laid out by arithmetic rather than by anchoring: a child anchored inside a docked container
    // here grows without bound, which is what MainWindow's own layout note records.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>What the box says, for a test that has no display to read it off.</summary>
  public string Description => $"{this._facts.Text}\n{this._hash.Text}";

  public void ApplyLayout() {
    var width = Math.Max(280, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);
    var hash = buttons - _LineHeight - 10;

    this._facts.Bounds = new(_Margin, _Margin, width, Math.Max(_LineHeight, hash - _Margin - 6));
    this._hash.Bounds = new(_Margin, hash, width, _LineHeight);
    // Wide enough for its own label: at 150 it photographed as "Compute SHA…", which reads as a
    // button that does something else.
    this._compute.Bounds = new(_Margin, buttons, 170, _ButtonHeight);
    this._reveal.Bounds = new(_Margin + 180, buttons, 120, _ButtonHeight);
    this._close.Bounds = new(this.Width - _Margin - 80, buttons, 80, _ButtonHeight);
  }

}
