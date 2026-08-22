using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Ending and locking login sessions, through <c>loginctl</c> (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// The same shape and the same reasoning as <see cref="SystemdServiceControl"/>. Reading who is
/// logged in needs a file and no privilege; ending somebody's session needs the manager that owns
/// it, and only logind may do that. So this asks the tool that speaks to it, which carries polkit —
/// an ordinary user gets whatever prompt their desktop is configured to give, and a session that
/// cannot prompt gets a refusal rather than a hang.
/// </para>
/// <para>
/// No shell, ever, and the id is validated before anything runs. A session id is a short run of
/// digits or letters; a "session" called <c>--all</c> would be a switch, and one with a slash in it
/// would be a path.
/// </para>
/// <para>
/// <b>Nothing here can be reached by a test.</b> The runner is injected for exactly that reason: a
/// test suite may assert what would have been invoked, and may not log somebody out of the machine
/// it happens to be running on.
/// </para>
/// </remarks>
public sealed class LoginctlSessionControl : ISessionControl {

  private readonly Func<string, IReadOnlyList<string>, (int Code, string Output, string Error)> _run;

  /// <param name="run">
  /// How a program is run. Injected so the tests can assert what would have been invoked without
  /// ending a session on the machine running them.
  /// </param>
  public LoginctlSessionControl(
    Func<string, IReadOnlyList<string>, (int Code, string Output, string Error)>? run = null
  ) => this._run = run ?? Run;

  /// <summary>Whether logind is here at all to be asked.</summary>
  public static bool IsPresent => Directory.Exists("/run/systemd/sessions");

  /// <inheritdoc />
  bool ISessionControl.IsAvailable => IsPresent;

  /// <inheritdoc />
  public ActionResult Apply(SessionCommand command, string sessionId) {
    if (command == SessionCommand.None)
      return ActionResult.Fail(ActionOutcome.Refused, "no session command was named");

    if (Validate(sessionId) is { } refusal)
      return refusal;

    if (!IsPresent)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        "there is no login manager on this machine, so there is nothing to ask"
      );

    // Never wait on a prompt this program cannot show — the same trap SystemdServiceControl
    // documents at length, and the same half-minute pause followed by a misleading timeout.
    var (code, _, error) = this._run(
      "loginctl",
      ["--no-ask-password", ISessionControl.Verb(command), "--", sessionId]
    );

    if (code == 0)
      return ActionResult.Ok;

    var detail = error.Trim();
    if (detail.Length == 0)
      detail = $"loginctl {ISessionControl.Verb(command)} {sessionId} exited with {code}";

    // The manager's own words, verbatim. Classifying a refusal by matching its text is wrong on any
    // machine that does not answer in English — this one answers in German — so the phrases below
    // are a best-effort refinement and the detail carries what was actually said either way.
    return LooksLikeARefusal(detail)
      ? ActionResult.Fail(ActionOutcome.NotPermitted, detail)
      : ActionResult.Fail(ActionOutcome.Failed, detail);
  }

  private static bool LooksLikeARefusal(string detail)
    => detail.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("Authentication is required", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Whether a string is a session id at all.
  /// </summary>
  /// <remarks>
  /// Checked here rather than left to the tool, because the clearest refusal is the one that happens
  /// before anything runs and because this is about to become a command-line argument. logind names
  /// a session with letters and digits and nothing else; anything with a slash, a dash at the front
  /// or a control character in it did not come from a session and must not reach a process.
  /// </remarks>
  private static ActionResult? Validate(string? sessionId) {
    if (sessionId is not { Length: > 0 })
      return ActionResult.Fail(ActionOutcome.Refused, "there is no session id — this login was not opened by systemd");

    if (sessionId.Length > 64)
      return ActionResult.Fail(ActionOutcome.Refused, "that is longer than a session id may be");

    foreach (var character in sessionId)
      if (!char.IsAsciiLetterOrDigit(character))
        return ActionResult.Fail(ActionOutcome.Refused, $"'{sessionId}' is not a session id");

    return null;
  }

  private static (int Code, string Output, string Error) Run(string program, IReadOnlyList<string> arguments) {
    var start = new System.Diagnostics.ProcessStartInfo(program) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      // No shell: the arguments are handed over as they are, so nothing re-splits or re-globs them.
      UseShellExecute = false,
    };

    foreach (var argument in arguments)
      start.ArgumentList.Add(argument);

    try {
      using var process = System.Diagnostics.Process.Start(start);
      if (process is null)
        return (-1, string.Empty, "loginctl could not be started");

      var output = process.StandardOutput.ReadToEnd();
      var error = process.StandardError.ReadToEnd();
      if (!process.WaitForExit(30_000)) {
        try {
          process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
        }

        return (-1, output, "loginctl did not answer within thirty seconds");
      }

      return (process.ExitCode, output, error);
    } catch (System.ComponentModel.Win32Exception) {
      return (-1, string.Empty, "loginctl is not installed on this machine");
    }
  }

}
