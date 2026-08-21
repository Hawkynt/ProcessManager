using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Starting and stopping systemd units (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// Reading a unit needs nothing but the files on disk, which is why
/// <see cref="SystemdServiceReader"/> spawns nothing. Changing one is the opposite: the state lives
/// in the manager rather than on disk, and only the manager may change it. So this asks
/// <c>systemctl</c>, which is the supported way in and carries polkit with it — an ordinary user
/// gets the prompt their desktop is configured to give, and a session that cannot prompt gets a
/// refusal rather than a hang.
/// </para>
/// <para>
/// No shell, ever. The unit name is passed as its own argument after <c>--</c>, so a unit called
/// <c>--version</c> is a unit name rather than a switch, and a name with a space in it stays one
/// name. The name is checked against what systemd itself permits before that, because the clearest
/// refusal is the one that happens before anything is run.
/// </para>
/// </remarks>
public sealed class SystemdServiceControl : IServiceControl {

  private readonly Func<string, IReadOnlyList<string>, (int Code, string Output, string Error)> _run;

  /// <param name="run">
  /// How a program is run. Injected so the tests can assert what would have been invoked without
  /// starting or stopping anything on the machine running them — which is not a thing a test suite
  /// may do to somebody's computer.
  /// </param>
  public SystemdServiceControl(
    Func<string, IReadOnlyList<string>, (int Code, string Output, string Error)>? run = null
  ) => this._run = run ?? Run;

  /// <summary>Whether the manager is even there to be asked.</summary>
  public static bool IsPresent => Directory.Exists("/run/systemd/system");

  /// <inheritdoc />
  bool IServiceControl.IsAvailable => IsPresent;

  /// <summary>
  /// Asks the manager to do something to a unit.
  /// </summary>
  /// <param name="userScope">
  /// The calling user's own manager rather than the system's. A user unit needs no privilege at all,
  /// and asking the system manager about one is an error rather than a permission problem — so the
  /// two are different questions and the caller has to say which it means.
  /// </param>
  public ActionResult Apply(ServiceCommand command, string unit, bool userScope = false) {
    if (Validate(unit) is { } refusal)
      return refusal;

    if (!IsPresent)
      return ActionResult.Fail(
        ActionOutcome.NotSupportedOnPlatform,
        "there is no systemd on this machine, so there is no manager to ask"
      );

    var arguments = new List<string>();
    if (userScope)
      arguments.Add("--user");

    // Never wait on a prompt this program cannot show. Without it, a session with no agent — a
    // terminal over ssh, a service, a test — sits there until polkit gives up, which on this machine
    // took the better part of half a minute and then reported a connection timeout as though the
    // manager were broken. Refusing at once and saying why is a better answer than a long pause and
    // a misleading one.
    arguments.Add("--no-ask-password");
    arguments.Add(IServiceControl.Verb(command));
    // Everything after this is a name, whatever it looks like.
    arguments.Add("--");
    arguments.Add(unit);

    var (code, _, error) = this._run("systemctl", arguments);
    if (code == 0)
      return ActionResult.Ok;

    var detail = error.Trim();
    if (detail.Length == 0)
      detail = $"systemctl {IServiceControl.Verb(command)} {unit} exited with {code}";

    // The manager's own words, verbatim, whatever they are. Classifying a refusal by matching its
    // text was the first version of this and it is wrong: systemctl's messages are translated, and
    // this very machine answers in German. Matching "Access denied" would have filed every refusal
    // on a non-English desktop as an ordinary failure — confidently, and only for users who do not
    // read English.
    //
    // So the English phrases below are a best-effort refinement and nothing depends on them: the
    // detail carries what the manager said either way, and the caller shows that rather than a
    // word of ours.
    return LooksLikeARefusal(detail)
      ? ActionResult.Fail(ActionOutcome.NotPermitted, detail)
      : ActionResult.Fail(ActionOutcome.Failed, detail);
  }

  /// <summary>
  /// Whether the manager's answer reads like a refusal by policy rather than a failure.
  /// </summary>
  /// <remarks>
  /// Advisory only, and deliberately so — see the note at the call site. These are the phrases an
  /// English-language systemd uses; a machine in any other language falls through to "failed" and
  /// still shows the reader exactly what it was told, which is the part that matters.
  /// </remarks>
  private static bool LooksLikeARefusal(string detail)
    => detail.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("Interactive authentication required", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
    || detail.Contains("Authentication is required", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Whether a name is one systemd would accept.
  /// </summary>
  /// <remarks>
  /// Checked here rather than left to the tool, because the refusal is clearer before anything runs
  /// and because a name is about to become a command-line argument. A path separator is the one that
  /// matters: <c>../../etc/passwd</c> is not a unit and must not reach a process that might treat it
  /// as a path.
  /// </remarks>
  private static ActionResult? Validate(string? unit) {
    if (unit is not { Length: > 0 })
      return ActionResult.Fail(ActionOutcome.Refused, "there is no unit named");

    if (unit.Length > 255)
      return ActionResult.Fail(ActionOutcome.Refused, "that is longer than a unit name may be");

    foreach (var character in unit)
      if (character is '/' or '\0' or '\n' || char.IsControl(character))
        return ActionResult.Fail(ActionOutcome.Refused, $"'{unit}' is not a unit name");

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
        return (-1, string.Empty, "systemctl could not be started");

      var output = process.StandardOutput.ReadToEnd();
      var error = process.StandardError.ReadToEnd();
      // Bounded: an interactive polkit prompt that nobody answers must not hold the program for ever.
      if (!process.WaitForExit(30_000)) {
        try {
          process.Kill(entireProcessTree: true);
        } catch (InvalidOperationException) {
        }

        return (-1, output, "systemctl did not answer within thirty seconds");
      }

      return (process.ExitCode, output, error);
    } catch (System.ComponentModel.Win32Exception) {
      return (-1, string.Empty, "systemctl is not installed on this machine");
    }
  }

}
