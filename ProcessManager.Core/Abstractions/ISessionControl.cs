namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>What may be asked of a login session (PRD §43).</summary>
/// <remarks>
/// <para>
/// <see cref="Terminate"/> is what a person means by "log this user off": everything in the session
/// goes, unsaved work included, which is why it is class 2 and confirmed (PRD §69, §90).
/// </para>
/// <para>
/// <b>There is no disconnect.</b> Windows separates disconnecting a session from logging it off
/// because a terminal-services session survives without a client attached; logind has no such state
/// and neither does anything else on Linux. An item that could only ever refuse is a lie dressed as
/// a feature, so the verb is absent rather than present and apologetic (PRD §32).
/// </para>
/// </remarks>
public enum SessionCommand : byte {

  /// <summary>Nobody said. Refused, like every other unclassified request.</summary>
  None = 0,

  /// <summary>End the session and everything in it.</summary>
  Terminate,

  /// <summary>Lock the session's screen, if whatever is showing it will listen.</summary>
  Lock,

  /// <summary>Unlock it again.</summary>
  Unlock,

}

/// <summary>
/// Doing something to somebody else's login session (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ISystemProbe"/> for the same reason <see cref="IServiceControl"/> is:
/// reading who is logged in needs a file, and ending their session needs the manager that owns it
/// and whatever the machine's policy says about who may ask.
/// </para>
/// <para>
/// A session is named by the id <c>loginctl</c> uses, which a login record does not carry — it is
/// worked out from the session leader's cgroup. A row whose id could not be worked out cannot be
/// acted on at all, and a front-end must grey the item rather than offer it and then explain
/// (PRD §32).
/// </para>
/// </remarks>
public interface ISessionControl {

  /// <summary>Whether there is a login manager here at all to be asked.</summary>
  /// <remarks>
  /// False is not a failure. A machine with no logind has nothing to refuse: the commands do not
  /// apply, and a front-end reading this leaves them out rather than offering a disappointment.
  /// </remarks>
  bool IsAvailable { get; }

  /// <summary>Asks the login manager to do something to a session.</summary>
  /// <param name="sessionId">The id <c>loginctl</c> knows the session by.</param>
  ActionResult Apply(SessionCommand command, string sessionId);

  /// <summary>How dangerous each of these is, in §69's classes.</summary>
  /// <remarks>
  /// Ending a session takes every unsaved thing in it, which is class 2 and is asked about. Locking
  /// and unlocking a screen is undone by the item beside it, which is class 1. Nothing here is class
  /// 3: none of it can take the machine, and treating it as though it could would train people to
  /// click through the dialogs that matter.
  /// </remarks>
  static ActionClass ClassOf(SessionCommand command) => command switch {
    SessionCommand.Terminate => ActionClass.DataLoss,
    SessionCommand.Lock or SessionCommand.Unlock => ActionClass.Reversible,
    _ => ActionClass.Unclassified,
  };

  /// <summary>The verb, for a message to a person and for the command line to parse back.</summary>
  static string Verb(SessionCommand command) => command switch {
    SessionCommand.Terminate => "terminate-session",
    SessionCommand.Lock => "lock-session",
    SessionCommand.Unlock => "unlock-session",
    _ => "session-status",
  };

}
