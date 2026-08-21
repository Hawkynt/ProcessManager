using System.Globalization;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The scheduler classes a process can be moved into, named once (PRD §25.2).
/// </summary>
/// <remarks>
/// One catalogue rather than a list in each front-end. The desktop menu, the terminal's chooser and
/// the CLI switch all read from here, so a class added to the kernel's vocabulary appears in all
/// three or in none — which is the failure the field registry exists to prevent for columns, applied
/// to the same problem one layer down.
/// </remarks>
public static class SchedulingClasses {

  /// <summary>
  /// One offered choice: what to call it, which class it is, and at what static priority.
  /// </summary>
  /// <param name="Name">
  /// The kernel's own name in brackets, deliberately. <c>SCHED_IDLE</c> is what <c>chrt</c> prints
  /// and what every manual page calls it, and a menu that said only "Low" would leave somebody
  /// unable to check the answer against the tool that reports it (PRD §5.3).
  /// </param>
  /// <param name="IsRealTime">
  /// Whether picking it needs privilege and can take the machine with it. A real-time task that
  /// spins never yields the processor it is on.
  /// </param>
  public readonly record struct Choice(string Name, SchedulingPolicy Policy, int Priority, bool IsRealTime);

  /// <summary>
  /// What a chooser offers, in the order it should offer it.
  /// </summary>
  /// <remarks>
  /// The real-time entries are offered at priority 1 — the lowest the class has. A menu is not the
  /// place to hand out a priority that outranks the kernel's own threads, and anyone who wants one
  /// higher is doing something deliberate enough to say so at a prompt (PRD §68).
  /// </remarks>
  public static IReadOnlyList<Choice> Offered { get; } = [
    new("Normal (SCHED_OTHER)", SchedulingPolicy.Other, 0, false),
    new("Batch (SCHED_BATCH)", SchedulingPolicy.Batch, 0, false),
    new("Idle (SCHED_IDLE)", SchedulingPolicy.Idle, 0, false),
    new("Real-time round-robin (SCHED_RR 1)", SchedulingPolicy.RoundRobin, 1, true),
    new("Real-time first-in-first-out (SCHED_FIFO 1)", SchedulingPolicy.Fifo, 1, true),
  ];

  /// <summary>The names a command line may use, for a help text that cannot drift from the parser.</summary>
  public const string Vocabulary = "other, batch, idle, rr[:1-99], fifo[:1-99]";

  /// <summary>
  /// Reads <c>idle</c>, <c>batch</c>, <c>rr:50</c> and the like.
  /// </summary>
  /// <remarks>
  /// The static priority is part of the word because it is part of the class: <c>SCHED_RR</c> at 1
  /// and <c>SCHED_RR</c> at 99 are not the same request, and separating them into two switches would
  /// let one be given without the other.
  /// </remarks>
  public static bool TryParse(string? text, out SchedulingPolicy policy, out int priority) {
    policy = SchedulingPolicy.Unknown;
    priority = 0;
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var colon = text.IndexOf(':', StringComparison.Ordinal);
    var name = (colon < 0 ? text : text[..colon]).Trim();
    if (colon >= 0 && !int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority))
      return false;

    policy = name.ToLowerInvariant() switch {
      "other" or "normal" or "ts" or "sched_other" => SchedulingPolicy.Other,
      "batch" or "sched_batch" => SchedulingPolicy.Batch,
      "idle" or "idl" or "sched_idle" => SchedulingPolicy.Idle,
      "rr" or "sched_rr" => SchedulingPolicy.RoundRobin,
      "fifo" or "ff" or "sched_fifo" => SchedulingPolicy.Fifo,
      _ => SchedulingPolicy.Unknown,
    };

    // A real-time class named without a priority means the lowest one, not zero — which the class
    // does not accept and which would be refused for a reason nobody asked about.
    if (colon < 0 && policy is SchedulingPolicy.RoundRobin or SchedulingPolicy.Fifo)
      priority = 1;

    return policy != SchedulingPolicy.Unknown;
  }

}
