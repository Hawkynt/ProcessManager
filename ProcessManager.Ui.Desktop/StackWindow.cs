using System.Globalization;
using System.Text;
using Hawkynt.NativeForms;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Ui.Desktop;

/// <summary>
/// One thread's stack, and an honest account of the part of it that is not here (PRD §30).
/// </summary>
/// <remarks>
/// <para>
/// The header is not decoration. Linux keeps two stacks per thread and gives up neither freely: the
/// kernel stack behind <c>/proc/[pid]/task/[tid]/stack</c> needs <c>CAP_SYS_ADMIN</c>, and the
/// thread's own stack is not exposed at all — walking it means unwinding another process's memory
/// with its debug information, which is the driver §4.1 rules out wearing different clothes. So this
/// window usually shows a short list, and the sentence above it is the difference between "that is
/// the whole stack" and "that is what the kernel would say".
/// </para>
/// <para>
/// A viewer that showed the same short list without the sentence would be lying by omission, which
/// is the failure §25's agent refused and the one this window exists to avoid repeating.
/// </para>
/// </remarks>
public sealed class StackWindow : Form {

  private const int _Margin = 10;
  private const int _ButtonHeight = 28;

  private const int _ButtonGap = 8;
  private const int _SummaryHeight = 78;

  private readonly ISystemProbe _probe;
  private readonly ProcessKey _key;
  private readonly int _threadId;
  private readonly string? _threadName;
  private readonly Label _summary = new();
  private readonly TreeListView _frames = new();
  private readonly Panel _buttons = new();
  private readonly Button _refresh = new() { Text = "Refresh" };
  private readonly Button _resolve = new() { Text = "Resolve symbols" };
  private readonly Button _copyFrame = new() { Text = "Copy frame" };
  private readonly Button _copyStack = new() { Text = "Copy stack" };
  private readonly Button _openModule = new() { Text = "Open module" };
  private readonly Button _save = new() { Text = "Save stack…" };

  private ThreadStack _stack;
  private bool _resolved;

  public StackWindow(ISystemProbe probe, ProcessKey key, int threadId, string? threadName = null) {
    ArgumentNullException.ThrowIfNull(probe);

    this._probe = probe;
    this._key = key;
    this._threadId = threadId;
    this._threadName = threadName;
    this._stack = ThreadStack.None(threadId, UnknownReason.NotSampledYet);

    this.Text = threadName is { Length: > 0 }
      ? $"Stack — thread {threadId} ({threadName})"
      : $"Stack — thread {threadId}";
    // Form.QuitsOnClose defaults to true because the first window shown owns the message loop; every
    // window that is not that one has to say so.
    this.QuitsOnClose = false;
    this.Bounds = new(0, 0, 980, 520);
    this.MinimumSize = new(560, 300);

    this._frames.Dock = DockStyle.Fill;
    this._frames.ShowColumnHeaders = true;
    // §30's columns, in §30's order. Source and source line are here and empty on purpose: a column
    // that says "no source information" is a fact about this build, and a column that is absent is a
    // requirement quietly dropped.
    // Summing to less than the window is wide, because this list scrolls up and down and not
    // sideways: a column past the right-hand edge is unreachable, and the first capture cut
    // "Displacement" in half.
    foreach (var (header, width) in (ReadOnlySpan<(string, int)>)[
      ("#", 40),
      ("Address", 140),
      ("Symbol", 246),
      ("Module", 210),
      ("Source", 88),
      ("Line", 50),
      ("Displacement", 104),
      ("Type", 70),
    ]) {
      var column = this._frames.Columns.Count;
      this._frames.Columns.Add(new(header, width, node => ((string[])node.Tag!)[column]));
    }

    this._refresh.Click += (_, _) => this.Reload(this._resolved);
    this._resolve.Click += (_, _) => this.Reload(resolveSymbols: true);
    this._copyFrame.Click += (_, _) => Clipboard.SetText(this.SelectedFrameText());
    this._copyStack.Click += (_, _) => Clipboard.SetText(Describe(in this._stack, this._threadName));
    this._openModule.Click += (_, _) => this.OpenModule();
    this._save.Click += (_, _) => this.Save();

    // The Fill child first and the docked edges after it, which is the order the detail pane's own
    // note records: a docked child claims its edge, and Fill takes whatever is left over.
    this.Controls.Add(this._frames);
    this._summary.Dock = DockStyle.Top;
    this._summary.Height = _SummaryHeight;
    this.Controls.Add(this._summary);
    this._buttons.Dock = DockStyle.Bottom;
    this._buttons.Height = _ButtonHeight + (2 * _Margin);
    this._buttons.Controls.Add(this._refresh);
    this._buttons.Controls.Add(this._resolve);
    this._buttons.Controls.Add(this._copyFrame);
    this._buttons.Controls.Add(this._copyStack);
    this._buttons.Controls.Add(this._openModule);
    this._buttons.Controls.Add(this._save);
    this.Controls.Add(this._buttons);

    // Laid out by arithmetic rather than by anchoring, for the reason MainWindow's layout note
    // records: an anchored child inside a docked container here grows without bound.
    this.Resize += (_, _) => this.ApplyLayout();
    this.ApplyLayout();
  }

  /// <summary>Which thread this is about. It never changes — that is the point of the window.</summary>
  public int ThreadId => this._threadId;

  /// <summary>The stack as last read, for a test with no display to look at.</summary>
  public ThreadStack Stack => this._stack;

  /// <summary>
  /// Places the buttons across the foot of the window.
  /// </summary>
  /// <remarks>
  /// Each as wide as its own label, measured off a capture rather than guessed: at a shared 148 the
  /// row photographed as "Resolve symb…", which reads as a button that does something else. One
  /// width for all of them is either too narrow for the longest or wasteful for the rest.
  /// </remarks>
  public void ApplyLayout() {
    var top = _Margin;
    var left = _Margin;
    foreach (var (button, width) in (ReadOnlySpan<(Button, int)>)[
      (this._refresh, 100),
      (this._resolve, 180),
      (this._copyFrame, 120),
      (this._copyStack, 120),
      (this._openModule, 130),
      (this._save, 130),
    ]) {
      button.Bounds = new(left, top, width, _ButtonHeight);
      left += width + _ButtonGap;
    }
  }

  /// <summary>
  /// Takes the stack and shows it.
  /// </summary>
  /// <param name="resolveSymbols">
  /// Whether to open the images the frames fall in and search their symbol tables. Synchronous, and
  /// deliberately so — see the note on the button in §30.
  /// </param>
  public void Reload(bool resolveSymbols) {
    this._resolved = resolveSymbols;
    this._stack = this._probe.GetThreadStack(this._key, this._threadId, resolveSymbols);
    this._summary.Text = Summarize(in this._stack, this._threadId, this._threadName, resolveSymbols);
    this._resolve.Enabled = !resolveSymbols;

    var frames = this._stack.Frames;
    var wasSelected = this._frames.SelectedNode?.Text;
    this._frames.Nodes.Clear();
    if (frames.Count == 0) {
      // An empty list and a stack nobody was allowed to take look identical, so the empty row says
      // which it is (PRD §1.5).
      var cells = new string[this._frames.Columns.Count];
      Array.Fill(cells, string.Empty);
      cells[0] = "—";
      cells[2] = Humanize.Explain(this._stack.KernelReason);
      this._frames.Nodes.Add(new TreeNode(cells[0]) { Tag = cells });
      return;
    }

    for (var i = 0; i < frames.Count; ++i) {
      var cells = Cells(frames[i]);
      var node = new TreeNode(cells[0]) { Tag = cells };
      this._frames.Nodes.Add(node);
      if (wasSelected is not null && string.Equals(node.Text, wasSelected, StringComparison.Ordinal))
        this._frames.SelectedNode = node;
    }
  }

  /// <summary>What the window says, for a test or a capture with no display to read it off.</summary>
  public string Description => this._summary.Text;

  /// <summary>What the capture log records about this window (PRD §9.6).</summary>
  public string DescribeForCapture() {
    var text = new StringBuilder();
    text.Append(CultureInfo.InvariantCulture, $"stack window: {this._frames.Nodes.Count} row(s), {this._frames.Columns.Count} columns\n");
    text.Append(CultureInfo.InvariantCulture, $"stack frames: {this._stack.Frames.Count} ({this._stack.KernelFrameCount} kernel)\n");
    text.Append(CultureInfo.InvariantCulture, $"stack kernel: {this._stack.KernelReason}\n");
    return text.ToString();
  }

  private static string[] Cells(in StackFrame frame) => [
    frame.Index.ToString(CultureInfo.InvariantCulture),
    Humanize.Address(frame.Address),
    frame.Symbol ?? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
    frame.Module is { Length: > 0 } module ? module : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform),
    // DWARF, which this build does not read. Saying so in the cell beats an empty column that reads
    // as "there is no source for this frame".
    frame.SourceFile ?? Humanize.Placeholder(UnknownReason.NotImplementedHere),
    frame.SourceLine > 0 ? frame.SourceLine.ToString(CultureInfo.InvariantCulture) : Humanize.Placeholder(UnknownReason.NotImplementedHere),
    frame.Displacement.HasValue
      ? "+" + frame.Displacement.Value.ToString("x", CultureInfo.InvariantCulture)
      : Humanize.Placeholder(frame.Displacement.Reason),
    frame.Kind switch {
      FrameKind.Kernel => "kernel",
      FrameKind.User => "user",
      FrameKind.Managed => "managed",
      _ => "—",
    },
  ];

  /// <summary>
  /// The paragraph above the list: what was read, and what was not, and why.
  /// </summary>
  /// <remarks>
  /// Written out rather than reduced to a placeholder glyph, because the reader of this window is
  /// asking a question that a dash does not answer. "Refused: the kernel stack needs CAP_SYS_ADMIN"
  /// tells somebody what to do next; "—" tells them the program is broken.
  /// </remarks>
  public static string Summarize(in ThreadStack stack, int threadId, string? threadName, bool resolved) {
    var who = threadName is { Length: > 0 } ? $"thread {threadId} ({threadName})" : $"thread {threadId}";
    var kernel = stack.KernelReason switch {
      UnknownReason.None => $"{stack.KernelFrameCount} frame(s) read",
      UnknownReason.NotSampledYet => "none parked — the thread is on a processor",
      UnknownReason.NotPermitted => "refused — /proc/[pid]/task/[tid]/stack needs CAP_SYS_ADMIN",
      _ => "not read — " + Humanize.Explain(stack.KernelReason),
    };

    // Four short lines rather than four long ones: the label does not wrap, so a sentence wider than
    // the window is a sentence with its point cut off.
    return $"{who} — {stack.Frames.Count} frame(s), symbols {(resolved ? "resolved" : "not resolved")}\n"
      + $"kernel stack   {kernel}\n"
      + "user frames    not unwound — Linux gives up only the instruction a thread will resume at\n"
      + "source lines   none — this build reads no DWARF (PRD §4.1, §30)";
  }

  /// <summary>The stack as text, for the clipboard and for a file (PRD §29, §30).</summary>
  public static string Describe(in ThreadStack stack, string? threadName) {
    var text = new StringBuilder();
    text.AppendLine(Summarize(in stack, stack.ThreadId, threadName, resolved: true));
    text.AppendLine();
    for (var i = 0; i < stack.Frames.Count; ++i) {
      var frame = stack.Frames[i];
      text.Append(CultureInfo.InvariantCulture, $"{frame.Index,3}  {Humanize.Address(frame.Address),-18}  ");
      text.Append(frame.Symbol ?? "<no symbol>");
      if (frame.Displacement.TryGetValue(out var displacement))
        text.Append(CultureInfo.InvariantCulture, $"+0x{displacement:x}");
      if (frame.Module is { Length: > 0 } module)
        text.Append(CultureInfo.InvariantCulture, $"  [{module}]");

      text.AppendLine();
    }

    return text.ToString();
  }

  private string SelectedFrameText()
    => this._frames.SelectedNode?.Tag is string[] cells ? string.Join('\t', cells) : string.Empty;

  /// <summary>
  /// Shows what the module of the selected frame is, on disk (PRD §30).
  /// </summary>
  /// <remarks>
  /// The same box the modules view opens, because it is the same question about the same file — and
  /// a kernel frame has no file at all, which is a refusal rather than an empty box.
  /// </remarks>
  private void OpenModule() {
    if (this._frames.SelectedNode?.Tag is not string[] cells || cells.Length < 4)
      return;

    var module = cells[3];
    if (module.Length == 0 || module[0] != '/') {
      MessageBox.Show(
        "This frame is not in a mapped file: a kernel frame lives in the kernel image or in a"
        + " loadable module, neither of which this process has open.",
        "Process Manager"
      );

      return;
    }

    new FilePropertiesDialog(
      module,
      [],
      actions: null,
      // The same verify delegate the modules view hangs on its own box: a frame's module is a file
      // like any other, and the question of whether its bytes are still the ones its package shipped
      // is the one somebody reading an unfamiliar frame is most likely to ask (PRD §25.6, §70).
      image => this._probe.DescribeImage(image, verify: true)
    ).ShowDialog();
  }

  private void Save() {
    var dialog = new SaveFileDialog {
      Title = $"Save the stack of thread {this._threadId}",
      FileName = $"thread-{this._threadId}.txt",
    };

    if (dialog.ShowDialog() != DialogResult.OK || dialog.FileName is not { Length: > 0 } path)
      return;

    try {
      File.WriteAllText(path, Describe(in this._stack, this._threadName));
    } catch (IOException e) {
      MessageBox.Show(e.Message, "Process Manager");
    } catch (UnauthorizedAccessException e) {
      MessageBox.Show(e.Message, "Process Manager");
    }
  }

}
