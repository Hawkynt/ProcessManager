using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Abstractions;

/// <summary>
/// A process somebody wants started (PRD §54).
/// </summary>
/// <remarks>
/// <para>
/// Every field here is something a task manager's "run" box has always offered and that a plain
/// shell makes awkward: starting a program in a particular directory, with one environment variable
/// changed, at a low priority, pinned to two cores.
/// </para>
/// <para>
/// <b>There is deliberately no password on this record, and there never will be.</b> A "run as"
/// dialog that remembers a credential is a credential store nobody audited, and one that holds it
/// even briefly is a string in a heap that a core dump will carry (PRD §54). Running as another
/// user is the platform's own job — <c>sudo</c>, <c>pkexec</c>, <c>runas</c> — which is why
/// <see cref="Elevated"/> asks for a launcher rather than for a secret.
/// </para>
/// </remarks>
/// <param name="Arguments">
/// Already split. A single string would have to be re-split by somebody, and every program that
/// tries gets quoting wrong for at least one shell.
/// </param>
/// <param name="Environment">
/// Overrides, not a replacement. A process started with an emptied environment loses its locale, its
/// display and its path, which is almost never what somebody typing one variable meant.
/// </param>
/// <param name="Suspended">
/// Stopped the instant it exists, before it has run any of its own code — the only way to attach
/// something to a program's very first instruction. Expert, and off by default: a suspended process
/// nobody resumes looks exactly like one that hung.
/// </param>
public sealed record LaunchRequest(
  string FileName,
  IReadOnlyList<string> Arguments,
  string? WorkingDirectory = null,
  IReadOnlyList<KeyValuePair<string, string>>? Environment = null,
  bool Elevated = false,
  bool Suspended = false,
  int? Nice = null,
  ulong AffinityMask = 0,
  IoPriority? IoPriority = null
);

/// <summary>
/// What came of a launch (PRD §54).
/// </summary>
/// <remarks>
/// The pid and the identity pair are separate because they become known at different moments and one
/// can exist without the other. A program that exits before it can be read back — <c>echo</c>, or
/// anything that fails on its own terms — has a pid and no readable start time, and that is a
/// successful launch of a short-lived program rather than a failure to start one (PRD §8.2).
/// </remarks>
/// <param name="Pid">Always known once the process exists, even if it no longer does.</param>
/// <param name="Key">
/// The identity pair, or default where the process was gone before it could be read. Anything that
/// wants to act on the process later needs this and not the pid.
/// </param>
public readonly record struct LaunchResult(ActionResult Outcome, int Pid, ProcessKey Key) {

  public static LaunchResult Failed(ActionOutcome outcome, string detail)
    => new(ActionResult.Fail(outcome, detail), 0, default);

}
