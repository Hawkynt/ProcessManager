using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Ui.Terminal;

/// <summary>
/// Owns the real terminal: alternate screen, cursor, resize, and the key loop.
/// </summary>
/// <remarks>
/// Separate from <see cref="TerminalUi"/> on purpose. Everything that composes a frame is in the UI
/// and can be driven with no terminal at all, which is what makes the golden-frame test possible;
/// everything that can leave a terminal in a broken state is here, in one place with one restore
/// path (PRD §9.6, §11).
/// </remarks>
public sealed class TerminalHost : IDisposable {

  private readonly TextWriter _output;
  private bool _entered;

  public TerminalHost(TextWriter? output = null) => this._output = output ?? Console.Out;

  /// <summary>
  /// Runs until the user quits. Sampling happens on this thread between key waits, so a slow sample
  /// delays a keystroke by at most one sample — and the sample cost is on screen, so it is visible
  /// when it does.
  /// </summary>
  public void Run(Sampler sampler, ISystemProbe probe, IProcessActions? actions, TimeSpan interval) {
    var (width, height) = ReadSize();
    var ui = new TerminalUi(sampler, probe, actions, width, height, DetectColorDepth());

    this.Enter();
    try {
      ui.Update();
      ui.Flush(this._output);

      var nextSample = DateTime.UtcNow + interval;
      while (!ui.ShouldQuit) {
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
          if (ui.HandleKey(Console.ReadKey(intercept: true)))
            ui.Refresh();

          ui.Flush(this._output);
          continue;
        }

        if (DateTime.UtcNow >= nextSample) {
          ui.Update();
          ui.Flush(this._output);
          nextSample = DateTime.UtcNow + interval;
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

    return term.Contains("256", StringComparison.Ordinal) ? ColorDepth.Ansi256 : ColorDepth.Ansi16;
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
    this._output.Flush();
  }

  private void Leave() {
    if (!this._entered)
      return;

    this._entered = false;
    Console.CancelKeyPress -= this.OnCancel;
    AppDomain.CurrentDomain.ProcessExit -= this.OnProcessExit;
    AppDomain.CurrentDomain.UnhandledException -= this.OnUnhandled;
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
