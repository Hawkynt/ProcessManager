using Hawkynt.ProcessManager.Model;
using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// What the terminal opens with: the settings file, layered under whatever this run's flags said
/// (PRD §11, §67).
/// </summary>
/// <remarks>
/// Until this existed the interactive terminal ignored <c>--sort</c>, <c>--columns</c> and the
/// settings file entirely — only the capture path read them — so a saved layout came back in the
/// window and not in the terminal.
/// </remarks>
public sealed record TerminalStartup {

  public ProcessField SortColumn { get; init; } = ProcessField.CpuPercent;

  public bool SortDescending { get; init; } = true;

  public bool TreeMode {
    get => this.Grouping == ProcessGrouping.ParentTree;
    init => this.Grouping = value ? ProcessGrouping.ParentTree : ProcessGrouping.None;
  }

  /// <summary>What the rows are grouped by (PRD §83). The tree is one of the answers.</summary>
  public ProcessGrouping Grouping { get; init; }

  /// <summary>The saved columns, or null to let the width decide (PRD §57.1).</summary>
  public ProcessField[]? Columns { get; init; }

  /// <summary>How many leading columns are pinned (PRD §11, §57.2).</summary>
  public int PinnedColumns { get; init; } = 1;

  /// <summary>Whether the tick opens switched off, with refreshes asked for by hand (PRD §12).</summary>
  public bool ManualRefresh { get; init; }

  public GraphStyle? Graphs { get; init; }

}

/// <summary>
/// Owns the real terminal: alternate screen, cursor, mouse, resize, and the key loop.
/// </summary>
/// <remarks>
/// Separate from <see cref="TerminalUi"/> on purpose. Everything that composes a frame is in the UI
/// and can be driven with no terminal at all, which is what makes the golden-frame test possible;
/// everything that can leave a terminal in a broken state is here, in one place with one restore
/// path (PRD §9.6, §11).
/// </remarks>
public sealed class TerminalHost : IDisposable {

  private readonly TextWriter _output;
  private readonly List<ConsoleKeyInfo> _pending = [];
  private bool _entered;
  private bool _mouse;

  public TerminalHost(TextWriter? output = null) => this._output = output ?? Console.Out;

  /// <summary>Whether to ask the terminal for mouse reports (PRD §57.5).</summary>
  public bool UseMouse { get; set; } = true;

  /// <summary>
  /// Runs until the user quits. Sampling happens on this thread between key waits, so a slow sample
  /// delays a keystroke by at most one sample — and the sample cost is on screen, so it is visible
  /// when it does.
  /// </summary>
  public void Run(
    Sampler sampler,
    ISystemProbe probe,
    IProcessActions? actions,
    TimeSpan interval,
    TerminalStartup? startup = null,
    IServiceControl? services = null
  ) {
    var (width, height) = ReadSize();
    var ui = new TerminalUi(sampler, probe, actions, width, height, DetectColorDepth(), services) {
      Keys = KeyBindings.Load(),
      ClipboardOutput = this._output,
    };

    // The picker moves it from here on, which is why the loop below reads it round rather than
    // keeping the value it was called with (PRD §12).
    ui.IntervalMilliseconds = (int)Math.Round(interval.TotalMilliseconds);

    if (startup is not null) {
      ui.View.SortColumn = startup.SortColumn;
      ui.View.SortDescending = startup.SortDescending;
      ui.View.Grouping = startup.Grouping;
      if (startup.Graphs is { } graphs)
        ui.GraphStyle = graphs;

      if (startup.Columns is { Length: > 0 } columns)
        ui.Columns.Apply(columns);

      // After the columns, because a pinned run is a count into the list the line above just
      // replaced — and Apply resets it, which is what a fresh set of columns should do.
      ui.Columns.SetFrozen(startup.PinnedColumns);
      if (startup.ManualRefresh)
        ui.SetManualRefresh();
    }

    foreach (var problem in ui.Keys.Errors)
      Console.Error.WriteLine($"procman: {problem}");

    this.Enter();
    try {
      ui.Update();
      ui.Flush(this._output);

      var pace = ui.IntervalMilliseconds;
      var nextSample = DateTime.UtcNow + TimeSpan.FromMilliseconds(pace);
      while (!ui.ShouldQuit) {
        if (pace != ui.IntervalMilliseconds) {
          // A rate chosen mid-wait applies to the wait that is running, not to the one after it.
          // Asking for a quarter of a second in the middle of a ten-second wait and then watching
          // nothing happen for ten seconds is how a picker gets a reputation for not working.
          nextSample += TimeSpan.FromMilliseconds(ui.IntervalMilliseconds - pace);
          pace = ui.IntervalMilliseconds;
        }

        // A resize is noticed here rather than through SIGWINCH: the signal would arrive on another
        // thread in the middle of a frame, and the whole point of the diff is that nobody else
        // writes between compose and flush.
        var (currentWidth, currentHeight) = ReadSize();
        if (currentWidth != width || currentHeight != height) {
          (width, height) = (currentWidth, currentHeight);
          ui.Resize(width, height);
          ui.Refresh();
          ui.Flush(this._output);
        }

        if (Console.KeyAvailable) {
          this.HandleInput(ui);
          ui.Flush(this._output);
          continue;
        }

        // An escape sequence that stopped arriving was not a mouse report: it was the Escape key.
        // Nothing else can tell the difference, which is why every terminal program has this timeout
        // — here it is one poll, so Escape takes at most 25 ms to close an overlay.
        if (this._pending.Count > 0) {
          this.ReplayPending(ui);
          ui.Flush(this._output);
          continue;
        }

        // Read round rather than held: the picker moves the rate mid-session, and a loop that kept
        // the value it started with would go on sampling at the old one until the program was
        // restarted (PRD §12).
        if (ui.Sampling && DateTime.UtcNow >= nextSample) {
          ui.Update();
          ui.Flush(this._output);
          nextSample = DateTime.UtcNow + TimeSpan.FromMilliseconds(pace);
          continue;
        }

        // Polling rather than blocking on a key: Console.ReadKey has no timeout, and a UI that only
        // repaints when a key is pressed is not a monitor. 25 ms is under the threshold where a
        // keystroke feels delayed and far above where the poll itself costs anything.
        Thread.Sleep(25);
      }
    } finally {
      this.Leave();
    }
  }

  /// <summary>
  /// Reads one keypress, or reassembles the several that a mouse report arrives as.
  /// </summary>
  /// <remarks>
  /// A mouse report is an escape sequence the console layer does not recognise, so it comes back one
  /// character at a time: <c>Esc</c>, <c>[</c>, <c>&lt;</c>, then digits and a final letter. They are
  /// collected until they decode or stop looking like a report, and anything that stops looking like
  /// one is handed to the UI as the keys it always was — so a terminal that sends something else
  /// entirely loses nothing.
  /// </remarks>
  private void HandleInput(TerminalUi ui) {
    var key = Console.ReadKey(intercept: true);
    if (this._pending.Count == 0 && key.KeyChar != '\u001b') {
      if (ui.HandleKey(key))
        ui.Refresh();

      return;
    }

    this._pending.Add(key);
    var text = Text(this._pending);
    if (MouseInput.TryDecode(text, out var mouse)) {
      this._pending.Clear();
      if (ui.HandleMouse(mouse))
        ui.Refresh();

      return;
    }

    // Still might be one; nothing is drawn until it is known either way. A report is never longer
    // than this, so a run of keys that happens to start with Escape cannot be buffered for ever.
    if (MouseInput.IsPrefix(text) && this._pending.Count < 32)
      return;

    this.ReplayPending(ui);
  }

  /// <summary>
  /// Hands the buffered keys to the UI as the keys they were.
  /// </summary>
  /// <remarks>
  /// With one exception: a control sequence that is not a mouse report is dropped rather than
  /// replayed. Some terminal describes some key with a sequence the console layer does not know —
  /// Ctrl+arrow on half of them — and replaying it as an Escape followed by <c>[1;5D</c> would quit
  /// the program because the user pressed Ctrl and an arrow. A lone Escape is still the Escape key.
  /// </remarks>
  private void ReplayPending(TerminalUi ui) {
    var keys = this._pending.ToArray();
    this._pending.Clear();
    if (keys.Length > 2 && keys[0].KeyChar == '\u001b' && keys[1].KeyChar == '[')
      return;

    for (var i = 0; i < keys.Length; ++i) {
      // The first one is the Escape itself, which arrives with no ConsoleKey attached because the
      // console layer did not recognise what followed it.
      var key = i == 0 && keys[i].KeyChar == '\u001b' && keys[i].Key == default
        ? new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false)
        : keys[i];

      if (ui.HandleKey(key))
        ui.Refresh();
    }
  }

  private static string Text(List<ConsoleKeyInfo> keys) {
    var builder = new StringBuilder(keys.Count);
    foreach (var key in keys)
      builder.Append(key.KeyChar);

    return builder.ToString();
  }

  private static (int Width, int Height) ReadSize() {
    try {
      return (Math.Max(40, Console.WindowWidth), Math.Max(10, Console.WindowHeight));
    } catch (IOException) {
      // No terminal attached (a pipe, a CI runner). The defaults keep the renderer working so that
      // --capture-frame produces something rather than throwing.
      return (100, 30);
    }
  }

  /// <summary>
  /// What the terminal admits to supporting, from the environment rather than from a guess.
  /// </summary>
  public static ColorDepth DetectColorDepth() {
    if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
      return ColorDepth.None;

    var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
    if (colorTerm is "truecolor" or "24bit")
      return ColorDepth.TrueColor;

    var term = Environment.GetEnvironmentVariable("TERM");
    if (term is null or "dumb")
      return ColorDepth.None;

    return term.Contains("256", StringComparison.Ordinal) || term.Contains("direct", StringComparison.Ordinal)
      ? ColorDepth.Ansi256
      : ColorDepth.Ansi16;
  }

  private void Enter() {
    if (this._entered)
      return;

    this._entered = true;
    // The handlers matter more than they look: a terminal left on the alternate screen with the
    // cursor hidden is a terminal the user has to reset by hand, and a crash is exactly when that
    // happens. Ctrl+C, an unhandled exception and a normal exit all end up in Leave.
    Console.CancelKeyPress += this.OnCancel;
    AppDomain.CurrentDomain.ProcessExit += this.OnProcessExit;
    AppDomain.CurrentDomain.UnhandledException += this.OnUnhandled;
    this._output.Write(Ansi.EnterAlternateScreen);
    this._output.Write(Ansi.HideCursor);
    if (this.UseMouse) {
      this._mouse = true;
      this._output.Write(Ansi.EnableMouse);
    }

    this._output.Flush();
  }

  private void Leave() {
    if (!this._entered)
      return;

    this._entered = false;
    Console.CancelKeyPress -= this.OnCancel;
    AppDomain.CurrentDomain.ProcessExit -= this.OnProcessExit;
    AppDomain.CurrentDomain.UnhandledException -= this.OnUnhandled;
    if (this._mouse) {
      this._mouse = false;
      // Before anything else: a terminal left reporting the mouse turns every later click in that
      // window into gibberish on the shell's command line.
      this._output.Write(Ansi.DisableMouse);
    }

    this._output.Write(Ansi.ShowCursor);
    this._output.Write(Ansi.Reset);
    this._output.Write(Ansi.LeaveAlternateScreen);
    this._output.Flush();
  }

  private void OnCancel(object? sender, ConsoleCancelEventArgs e) {
    this.Leave();
    e.Cancel = false;
  }

  private void OnProcessExit(object? sender, EventArgs e) => this.Leave();

  private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e) => this.Leave();

  public void Dispose() => this.Leave();

}
