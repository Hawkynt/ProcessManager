using System.Globalization;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// Everything one open descriptor is: what it points at, what may be done with it, and — when
/// somebody asks — what else on the machine is holding the same thing (PRD §32).
/// </summary>
/// <remarks>
/// <para>
/// The table shows a row per descriptor and cannot show the per-kind detail: an epoll set watching
/// four hundred descriptors, an inotify watch list, the two pids a pidfd has in two namespaces. That
/// is what this is for, and it shows those lines as the kernel wrote them rather than folding them
/// into a shape they do not have (PRD §5.3).
/// </para>
/// <para>
/// Finding the other holders is a button and not a field. It is a descriptor scan of the whole
/// machine — the only way to the far end of a pipe, since the kernel names it nowhere — and doing
/// that on opening would make every properties box cost what a search costs (PRD §5.4).
/// </para>
/// </remarks>
public sealed class HandlePropertiesDialog : Form {

  private const int _Margin = 12;

  /// <summary>What one line of the facts label occupies, measured off a capture.</summary>
  private const int _LineHeight = 19;

  private const int _ButtonHeight = 28;

  private readonly Label _facts = new();
  private readonly Label _holders = new();
  private readonly Button _find = new() { Text = "Find other holders" };
  private readonly Button _reveal = new() { Text = "Open folder" };
  private readonly Button _close = new() { Text = "Close" };

  private readonly ISystemProbe _probe;
  private readonly ProcessKey _owner;
  private readonly HandleRecord _handle;
  private readonly IProcessActions? _actions;
  private SystemSnapshot? _machine;

  /// <param name="endpoint">
  /// The connection this descriptor's inode joins to, when the list already worked that out. Null
  /// for every kind that is not a socket, and for a socket whose row had gone by the time the two
  /// tables were read (PRD §40).
  /// </param>
  public HandlePropertiesDialog(
    ISystemProbe probe,
    ProcessKey owner,
    in HandleRecord handle,
    string? endpoint = null,
    IProcessActions? actions = null
  ) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this._owner = owner;
    this._handle = handle;
    this._actions = actions;

    this.Text = $"Descriptor {handle.Handle} — {Humanize.ResourceKind(handle.Kind)}";
    // Form.QuitsOnClose defaults to true because the first window shown owns the message loop; every
    // window that is not that one has to say so.
    this.QuitsOnClose = false;

    var lines = new List<string> {
      $"descriptor  {handle.Handle.ToString(CultureInfo.InvariantCulture)}",
      $"kind        {Humanize.ResourceKind(handle.Kind)}",
      $"name        {handle.Name ?? "the kernel would not name it"}",
      $"access      {Access(in handle)}",
      $"flags       {DescriptorParser.DescribeFlags(handle.OpenFlags) ?? Humanize.Placeholder(handle.OpenFlags.Reason)}",
      $"position    {Humanize.Count(handle.Position)}",
      $"inode       {Humanize.Count(handle.Inode)}",
      $"filesystem  {FileSystem(in handle)}",
    };

    if (endpoint is { Length: > 0 })
      lines.Add($"endpoint    {endpoint}");

    if (handle.TargetPid.TryGetValue(out var target))
      lines.Add($"names       process {target.ToString(CultureInfo.InvariantCulture)}");

    if (handle.Detail is { Length: > 0 } detail) {
      // As the kernel wrote it, one line each and indented under a heading, because the lines are a
      // different kind of thing for every kind of descriptor and only their own names explain them.
      lines.Add("kernel detail");
      foreach (var line in detail.Split('\n'))
        lines.Add("  " + line);
    }

    this._facts.Text = string.Join('\n', lines);
    this._holders.Text = "other holders  not looked for — it is a scan of every process";

    this._find.Click += (_, _) => this.FindHolders();
    this._reveal.Click += (_, _) => this.Reveal();
    this._close.Click += (_, _) => this.Close();

    this.Controls.Add(this._facts);
    this.Controls.Add(this._holders);
    this.Controls.Add(this._find);
    this.Controls.Add(this._reveal);
    this.Controls.Add(this._close);

    // Sized to what it holds, plus four lines for the holders the button may add.
    this.Bounds = new(0, 0, 760, ((lines.Count + 4) * _LineHeight) + 116);
    this.MinimumSize = new(460, 240);
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>What the box says, for a test and for a capture with no display to read it off.</summary>
  public string Description => $"{this._facts.Text}\n{this._holders.Text}";

  /// <summary>
  /// Looks for every other descriptor on the machine pointing at the same inode (PRD §32).
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is how the far end of a pipe is found: both ends are descriptors on one inode, and the
  /// kernel publishes the pairing nowhere — so the only way to it is the one <c>lsof</c> takes,
  /// which is to look at every process. The same scan answers "who else has this file open", which
  /// is the question §33 exists for asked about one row instead of a pattern.
  /// </para>
  /// <para>
  /// What it cannot see is said rather than implied. An ordinary account may list its own processes'
  /// descriptors and nobody else's, so the count of processes that answered travels with the result:
  /// "nothing else holds this" and "nothing we were allowed to ask holds this" are different
  /// statements (PRD §72.3).
  /// </para>
  /// </remarks>
  public void FindHolders() {
    if (!this._handle.Inode.TryGetValue(out var inode)) {
      this._holders.Text = "other holders  cannot be looked for: this descriptor has no inode to match on"
        + $" ({Humanize.Placeholder(this._handle.Inode.Reason)})";
      return;
    }

    this._find.Text = "Scanning…";
    this._machine ??= new();
    this._probe.Sample(this._machine);

    var scan = ResourceSearch.FindHolders(this._probe, this._machine, inode, this._owner);
    this._find.Text = "Find other holders";
    this._find.Enabled = false;

    var text = new System.Text.StringBuilder();
    text.Append("other holders  ");
    if (scan.Holders.Count == 0)
      text.Append("none found");
    else
      for (var i = 0; i < scan.Holders.Count && i < 6; ++i) {
        var holder = scan.Holders[i];
        if (i > 0)
          text.Append("\n               ");

        text.Append(CultureInfo.InvariantCulture, $"{holder.ProcessName} ({holder.Pid}) fd {holder.Descriptor}");
        if (holder.Access is { Length: > 0 } access)
          text.Append(CultureInfo.InvariantCulture, $" {access}");
      }

    // The denominator, always. Without it the sentence above is a claim about the machine, and it is
    // only ever a claim about the part of the machine this account can see.
    text.Append(CultureInfo.InvariantCulture, $"\n               {scan.Answered} of {scan.Total} processes answered");
    this._holders.Text = text.ToString();
    this.ApplyLayout();
  }

  /// <summary>
  /// What the holder may do with it, spelled out rather than left as the two letters.
  /// </summary>
  /// <remarks>
  /// <c>path</c> is the fourth value and is not an access mode at all: an <c>O_PATH</c> descriptor
  /// refers to a file and may be used for neither reading nor writing it (PRD §32).
  /// </remarks>
  private static string Access(in HandleRecord handle) => handle.Access switch {
    "r" => "r — read only",
    "w" => "w — write only",
    "rw" => "rw — read and write",
    "path" => "O_PATH — refers to the file, and may neither read nor write it",
    { Length: > 0 } other => other,
    _ => Humanize.Placeholder(handle.OpenFlags.Reason),
  };

  /// <summary>Which mount the descriptor's inode is on, or why there is none.</summary>
  private static string FileSystem(in HandleRecord handle) {
    if (handle.Device is { Length: > 0 } device)
      return handle.FileSystem is { Length: > 0 } type ? $"{type} on device {device}" : $"device {device}";

    return handle.MountId.TryGetValue(out var id)
      // The mount id is real and matches nothing in the process's mount table: sockfs, pipefs and
      // anon_inodefs are mounted nowhere and appear in no table. That is where this descriptor is.
      ? $"none — mount {id.ToString(CultureInfo.InvariantCulture)} is a file system the kernel mounts nowhere"
      : Humanize.Placeholder(handle.MountId.Reason);
  }

  private void Reveal() {
    if (this._handle.Name is not { Length: > 0 } name || !name.StartsWith('/')) {
      MessageBox.Show("This descriptor has no path: the kernel names it, not the file system.", "Process Manager");
      return;
    }

    if (this._actions is null) {
      MessageBox.Show("This build has no actions for this platform.", "Process Manager");
      return;
    }

    if (DesktopOpen.Reveal(name) is not { } request) {
      MessageBox.Show("This platform has no desktop opener to hand the folder to.", "Process Manager");
      return;
    }

    var result = this._actions.Launch(request);
    if (!result.Outcome.Succeeded)
      MessageBox.Show(result.Outcome.Detail ?? result.Outcome.Outcome.ToString(), "Process Manager");
  }

  public void ApplyLayout() {
    var width = Math.Max(300, this.Width - (2 * _Margin));
    var buttons = Math.Max(_Margin + 40, this.Height - _Margin - _ButtonHeight);
    var holders = Math.Max(_Margin + _LineHeight, buttons - (4 * _LineHeight) - 10);

    this._facts.Bounds = new(_Margin, _Margin, width, Math.Max(_LineHeight, holders - _Margin - 6));
    this._holders.Bounds = new(_Margin, holders, width, 4 * _LineHeight);
    this._find.Bounds = new(_Margin, buttons, 170, _ButtonHeight);
    this._reveal.Bounds = new(_Margin + 180, buttons, 120, _ButtonHeight);
    this._close.Bounds = new(this.Width - _Margin - 80, buttons, 80, _ButtonHeight);
  }

}
