using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// Which of §69's four classes a mutation belongs to, and therefore how hard it is to do by accident.
/// </summary>
/// <remarks>
/// <para>
/// The class is a property of the <em>request</em> rather than of the method that carries it out, which
/// is why it lives beside <see cref="IProcessActions"/> and not inside it: sending
/// <c>SIGCONT</c> and sending <c>SIGKILL</c> are one method and two very different things to have
/// done, and a table keyed on the method could not tell them apart.
/// </para>
/// <para>
/// <see cref="Unclassified"/> is nought, and <see cref="ActionSafety.MustAsk"/> treats it as the most
/// dangerous class there is. That is the same rule <see cref="WindowCommand.None"/> follows and the
/// same one §72.3 states for readings: a default-constructed value must never turn out to be the
/// benign answer, because the one thing nobody filled in is the one thing nobody thought about.
/// </para>
/// </remarks>
public enum ActionClass : byte {

  /// <summary>Nobody said. Confirmed like <see cref="Unsafe"/> until somebody does.</summary>
  Unclassified = 0,

  /// <summary>
  /// Class 0. Reads something and changes nothing: copy, search, export, properties, reveal a path.
  /// </summary>
  ReadOnly,

  /// <summary>
  /// Class 1. Undone by the item beside it: priority, affinity, scheduling class, suspend and resume.
  /// </summary>
  Reversible,

  /// <summary>
  /// Class 2. Somebody's unsaved work may go with it: terminate, restart, stop a service, take a
  /// login entry out of the next boot.
  /// </summary>
  DataLoss,

  /// <summary>
  /// Class 3. Expert, and able to take the machine rather than the process: an arbitrary signal, a
  /// real-time scheduling class, freezing a whole cgroup.
  /// </summary>
  Unsafe,

}

/// <summary>
/// The confirmation policy §69 asks for, in one place so that the window and the terminal cannot
/// disagree about what needs asking (PRD §5.1, §58, §69).
/// </summary>
/// <remarks>
/// The classes existed in the action broker for some time before anything read them, which is the
/// same as not having them: a classification nothing consults is a comment. This is the half that
/// reads them.
/// </remarks>
public static class ActionSafety {

  /// <summary>
  /// Whether the person at the keyboard must be asked before this happens.
  /// </summary>
  /// <param name="class">Which of §69's classes the request is.</param>
  /// <param name="confirmsSingleActions">
  /// The <c>confirm.destructive</c> setting: whether a single-process action asks first. It governs
  /// classes 1 and 2 and nothing else — class 0 has nothing to ask about and class 3 is not something
  /// a preference may switch off.
  /// </param>
  /// <param name="systemTarget">
  /// Whether the target is one the machine depends on — see <see cref="IsSystemTarget"/>. §69 wants
  /// class 2 confirmed for these whatever the setting says, because the setting is turned off by
  /// people who end their own programs all day and not by people who meant to stop <c>systemd</c>.
  /// </param>
  public static bool MustAsk(ActionClass @class, bool confirmsSingleActions, bool systemTarget = false) => @class switch {
    ActionClass.ReadOnly => false,
    ActionClass.Reversible => confirmsSingleActions,
    ActionClass.DataLoss => systemTarget || confirmsSingleActions,
    ActionClass.Unsafe => true,

    // Including Unclassified. An action nobody sorted is asked about, and a value outside the enum
    // is asked about too — the failure that costs nothing is the extra dialog.
    _ => true,
  };

  /// <summary>
  /// Whether the machine depends on this process in a way a confirmation must not skip.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Two tests, both cheap and both already in the snapshot. Root's processes, because the setting
  /// that switches confirmations off is switched off for one's own editors and browsers; and the
  /// lowest handful of pids, because those are the machine's own scaffolding on both platforms —
  /// <c>init</c> and <c>kthreadd</c> on Linux, the idle process and the kernel on Windows.
  /// </para>
  /// <para>
  /// <b>It under-reports rather than over-reports, and knowingly.</b> A process that dropped from
  /// root to an ordinary user after it started reads as ordinary here, which is what its own
  /// credentials now say. What this must never do is the reverse — call something ordinary and let a
  /// confirmation be skipped over the one row on the machine that mattered.
  /// </para>
  /// </remarks>
  public static bool IsSystemTarget(in ProcessRecord process)
    => process.Pid is > 0 and <= 4 || process.UserId == 0;

  /// <summary>
  /// The extra sentence §69 asks a confirmation to carry when the target is the machine's own.
  /// </summary>
  /// <remarks>
  /// Named rather than adjectival — "this is one of root's" says something a reader can check against
  /// the user column, where "this is a critical process" is a verdict they cannot. The two lowest
  /// pids get a sentence of their own because they are not merely root's: nothing on the machine
  /// runs without them.
  /// </remarks>
  public static string SystemTargetWarning(int pid) => pid switch {
    1 => "This is the machine's init. Everything else on it is descended from this process.",
    <= 4 and > 0 => "This is part of the kernel's own scaffolding rather than a program somebody started.",
    _ => "This process belongs to root, so it is doing something for the whole machine rather than for one session.",
  };

  /// <summary>What a class is called, for a menu, a log line or an explanation.</summary>
  public static string Describe(ActionClass @class) => @class switch {
    ActionClass.ReadOnly => "reads and changes nothing",
    ActionClass.Reversible => "reversible",
    ActionClass.DataLoss => "unsaved work may be lost",
    ActionClass.Unsafe => "expert; this can take the machine rather than the process",
    _ => "unclassified, and therefore treated as the most dangerous",
  };

}
