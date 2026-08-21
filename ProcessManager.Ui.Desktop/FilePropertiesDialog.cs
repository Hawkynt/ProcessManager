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

  /// <summary>
  /// The band the hash and its verdicts occupy: the digest, the package, the two verdict words and a
  /// sentence apiece naming what each of them compared. Reserved whether or not there is anything to
  /// put in it, so that pressing the button does not resize the window under the reader's hand.
  /// </summary>
  /// <remarks>
  /// Seven and not three since the trust chain arrived beside the signature. §70's whole design is
  /// that one word answers five questions and the heading says which was asked, so the two headings
  /// have to be on screen together — a box showing only "Unsigned" cannot say whether the bytes
  /// changed or nobody signed for them (PRD §70).
  /// </remarks>
  private const int _HashLines = 7;

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
  /// <param name="verify">
  /// Asks whoever shipped the file whether these are still its bytes, on the one read of them the
  /// hash already pays for (PRD §31, §70). Null where nothing can answer, and then the button hashes
  /// and says only what the hash says — which is the truthful outcome, because a hash is not a
  /// verdict.
  /// </param>
  public FilePropertiesDialog(
    string path,
    IReadOnlyList<KeyValuePair<string, string>>? extra = null,
    IProcessActions? actions = null,
    Func<string, Model.ImageTrust>? verify = null
  ) {
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

    if (verify is not null)
      this._compute.Text = "Compute SHA-256 and check";

    this._compute.Click += (_, _) => {
      // Synchronous, and the button says what is happening first: this is somebody's deliberate
      // request for a whole-file read, not something that crept in behind a refresh.
      var label = this._compute.Text;
      this._compute.Text = "Hashing…";
      this._hash.Text = verify is null
        ? $"sha-256     {FileDigest.Of(this._path).Display}"
        : Verified(verify(this._path));

      this._compute.Text = label;
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
    this.Bounds = new(0, 0, 720, ((lines.Count + _HashLines) * _LineHeight) + 106);
    this.MinimumSize = new(420, 200);
    // Laid out by arithmetic rather than by anchoring: a child anchored inside a docked container
    // here grows without bound, which is what MainWindow's own layout note records.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>What the box says, for a test that has no display to read it off.</summary>
  public string Description => $"{this._facts.Text}\n{this._hash.Text}";

  /// <summary>
  /// The hash and the two verdicts, as three statements and never as one (PRD §25.6, §31, §70).
  /// </summary>
  /// <remarks>
  /// <para>
  /// On separate lines on purpose. The hash says what the bytes are; the signature says whether the
  /// party that shipped them still recognises them; the trust chain says whether anybody this machine
  /// trusts signed for that party. Each carries the sentence naming what was actually compared —
  /// because "Unsigned" on its own is read as an accusation when what it means on most Linux machines
  /// is that a package manager records a digest and nobody's signature.
  /// </para>
  /// <para>
  /// The chain is here rather than folded into the line above it, which is the whole of §70's first
  /// requirement: a locally built package whose files are untouched is <em>Verified</em> in the first
  /// and <em>Unsigned</em> in the second, and one word carrying both findings is how this program used
  /// to report a developer's own build as suspect. Where nothing on the machine keeps such a record at
  /// all, the row says so — <c>dpkg</c> stores no signature over an installed file, which is a
  /// different statement from "nobody signed it" (PRD §72.3).
  /// </para>
  /// </remarks>
  private static string Verified(Model.ImageTrust trust) {
    var digest = trust.Sha256 is { Length: 64 } hex
      ? string.Join(' ', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 8, 8)))
      : "not computed";

    var lines = new List<string> {
      $"sha-256     {digest}",
      // Which package claims the file, because both verdicts below are about that package and a
      // reader who cannot see which one was asked cannot check the answer.
      $"package     {trust.Package.Text ?? Query.Humanize.Placeholder(trust.Package.Reason)}",
      $"signature   {Model.SignatureStatusText.Text(trust.Signature)}",
    };

    if (trust.Detail is { Length: > 0 } detail)
      lines.Add($"            {detail}");

    lines.Add(trust.TrustChain == Model.SignatureStatus.NotChecked
      ? $"trust chain {Query.Humanize.Placeholder(trust.ChainReason)}"
      : $"trust chain {Model.SignatureStatusText.Text(trust.TrustChain)}");

    if (trust.ChainDetail is { Length: > 0 } chain)
      lines.Add($"            {chain}");

    // Who assembled the package, and deliberately not called a signer: nobody signed the file, and
    // this is the name the database records rather than a name any signature carries (PRD §31).
    if (trust.Publisher is { Length: > 0 } publisher)
      lines.Add($"packager    {publisher}");

    return string.Join('\n', lines);
  }

  public void ApplyLayout() {
    var width = Math.Max(280, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);
    var hash = buttons - (_HashLines * _LineHeight) - 10;

    this._facts.Bounds = new(_Margin, _Margin, width, Math.Max(_LineHeight, hash - _Margin - 6));
    this._hash.Bounds = new(_Margin, hash, width, _HashLines * _LineHeight);
    // Wide enough for its own label: at 150 it photographed as "Compute SHA…", which reads as a
    // button that does something else.
    this._compute.Bounds = new(_Margin, buttons, 170, _ButtonHeight);
    this._reveal.Bounds = new(_Margin + 180, buttons, 120, _ButtonHeight);
    this._close.Bounds = new(this.Width - _Margin - 80, buttons, 80, _ButtonHeight);
  }

}
