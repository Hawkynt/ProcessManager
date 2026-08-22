using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--session</c>: ending or locking somebody's login (PRD §43).
/// </summary>
/// <remarks>
/// The counterpart to <c>--users</c>, which only reads. Reading who is logged in needs a file;
/// ending their session is logind's business, so this asks <c>loginctl</c> and carries whatever
/// polkit decides — including the refusal, in the manager's own words rather than ours.
/// </remarks>
internal static class SessionControlCommand {

  public static int Run(Sampler sampler, ISystemProbe probe, string? verb, string? sessionId, bool assumeYes, bool confirms) {
    var (known, command) = Parse(verb);
    if (!known) {
      Console.Error.WriteLine($"procman: '{verb}' is not something that can be done to a session.");
      Console.Error.WriteLine("Try: terminate, lock or unlock. There is no 'disconnect': Linux has no such state.");
      return 1;
    }

    if (ProbeFactory.CreateSessionControl() is not { } control) {
      Console.Error.WriteLine("procman: there is no login manager on this machine to ask.");
      return 1;
    }

    // systemTarget for the one that ends a session, which makes the confirmation unconditional: the
    // preference that switches confirmations off is switched off by people who end their own editors
    // all day, and not by people who meant to log somebody out of a machine they share. §43 asks for
    // the confirmation outright, and §69 has exactly this lever for exactly this reason.
    if (ActionSafety.MustAsk(ISessionControl.ClassOf(command), confirms, systemTarget: command == SessionCommand.Terminate)
        && !Confirm(sampler, probe, command, sessionId, assumeYes))
      return 1;

    var result = control.Apply(command, sessionId ?? string.Empty);
    if (result.Succeeded) {
      Console.WriteLine($"{verb} {sessionId}: done.");
      return 0;
    }

    Console.Error.WriteLine($"procman: {result.Detail}");
    return 1;
  }

  /// <summary>
  /// Says what is about to happen and to whom, and waits for a yes (PRD §90).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Never "are you sure". The sentence names the account, the terminal, the session and how many
  /// processes go with it, because the count is the part somebody can act on: "this will close 318
  /// programs" means something and "log off?" does not.
  /// </para>
  /// <para>
  /// A run whose input is not a terminal cannot be asked, and is refused rather than assumed to
  /// consent. <c>--yes</c> is how a script says it meant it, which is a decision written into the
  /// command rather than one taken on its behalf (PRD §5.5).
  /// </para>
  /// </remarks>
  private static bool Confirm(Sampler sampler, ISystemProbe probe, SessionCommand command, string? sessionId, bool assumeYes) {
    Console.WriteLine(Describe(sampler, probe, command, sessionId));
    if (assumeYes)
      return true;

    if (Console.IsInputRedirected) {
      Console.Error.WriteLine("procman: this needs confirming and there is nobody to ask. Add --yes if you meant it.");
      return false;
    }

    Console.Write("Type yes to go ahead: ");
    return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The §90 sentence: the action, the target, and what it costs.</summary>
  internal static string Describe(Sampler sampler, ISystemProbe probe, SessionCommand command, string? sessionId) {
    var who = "that session";
    var terminal = string.Empty;
    var user = string.Empty;
    foreach (var session in probe.GetSessions())
      if (session.SessionId is { Length: > 0 } id && string.Equals(id, sessionId, StringComparison.Ordinal)) {
        user = session.UserName;
        terminal = session.Terminal;
        who = $"{session.UserName} on {session.Terminal}";
        break;
      }

    if (command != SessionCommand.Terminate)
      return $"{(command == SessionCommand.Lock ? "Lock" : "Unlock")} the screen of session {sessionId} ({who}).";

    // A session logind opened that the login records do not carry — a graphical login on many
    // desktops is exactly that. §90 wants a consequence named, so what cannot be named is said
    // outright: an unexplained short sentence would read as a small action, and this is not one.
    if (user.Length == 0)
      return $"Log off session {sessionId}. Everything in it stops, unsaved work included.\n"
        + "This session is not in the login records, so the account, the terminal and the number of "
        + "processes it will take cannot be named here. `loginctl session-status "
        + $"{sessionId}` will say whose it is.";

    sampler.Sample();
    var processes = 0;
    var snapshot = sampler.Current;
    for (var i = 0; i < snapshot.Processes.Length; ++i)
      if (string.Equals(snapshot.Processes[i].UserName, user, StringComparison.Ordinal))
        ++processes;

    // The account's processes and not the session's, and the sentence says so. Nothing in a process
    // record names the login that started it, so a per-session count would be invented where this is
    // a true figure about a slightly wider set (PRD §5.3).
    return $"Log {user} off session {sessionId} ({terminal}). Everything in the session stops, including "
      + $"whatever has not been saved. {processes.ToString(CultureInfo.InvariantCulture)} processes currently "
      + "belong to that account.";
  }

  private static (bool Known, SessionCommand Command) Parse(string? verb) => verb?.ToLowerInvariant() switch {
    "terminate" or "logoff" or "log-off" => (true, SessionCommand.Terminate),
    "lock" => (true, SessionCommand.Lock),
    "unlock" => (true, SessionCommand.Unlock),
    _ => (false, SessionCommand.None),
  };

}
