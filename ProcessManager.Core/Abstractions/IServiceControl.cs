namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>What may be asked of a unit (PRD §41).</summary>
public enum ServiceCommand : byte {
  Start,
  Stop,
  Restart,
  Reload,
  Enable,
  Disable,
}

/// <summary>
/// Changing what the machine runs in the background (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ISystemProbe"/> because reading a unit and changing one are not the same
/// kind of act and do not need the same things. Reading needs the files on disk and no privilege at
/// all; changing needs the manager, which owns the state, and carries whatever the machine's policy
/// decides about who may ask.
/// </para>
/// <para>
/// It is an interface rather than the concrete control so that the window and the terminal can offer
/// the commands without either of them naming a platform. Both front-ends only ever saw the reading
/// half, so for a long time a unit could be started from the command line and from nowhere a person
/// would look — which is not parity with a tool that has a tab for it (PRD §91).
/// </para>
/// </remarks>
public interface IServiceControl {

  /// <summary>Whether there is a manager here at all to be asked.</summary>
  /// <remarks>
  /// False is not a failure and must not be presented as one. A machine with no service manager has
  /// nothing to refuse — the commands simply do not apply, and a front-end reading this hides them
  /// rather than offering something that can only disappoint.
  /// </remarks>
  bool IsAvailable { get; }

  /// <summary>
  /// Asks the manager to do something to a unit.
  /// </summary>
  /// <param name="userScope">
  /// The calling user's own manager rather than the system's. A user unit needs no privilege at all,
  /// and asking the system manager about one is an error rather than a permission problem — so the
  /// two are different questions and the caller has to say which it means.
  /// </param>
  ActionResult Apply(ServiceCommand command, string unit, bool userScope = false);

  /// <summary>The verb, for a message to a person and for the command line to parse back.</summary>
  static string Verb(ServiceCommand command) => command switch {
    ServiceCommand.Start => "start",
    ServiceCommand.Stop => "stop",
    ServiceCommand.Restart => "restart",
    ServiceCommand.Reload => "reload",
    ServiceCommand.Enable => "enable",
    ServiceCommand.Disable => "disable",
    _ => "status",
  };

}
