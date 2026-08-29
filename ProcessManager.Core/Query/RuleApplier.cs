using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What happened when a rule was applied to one process (PRD §66).</summary>
/// <param name="Key">Which process.</param>
/// <param name="What">Which preference — "priority", "affinity", "I/O priority".</param>
/// <param name="Result">What the platform said.</param>
public readonly record struct RuleApplication(ProcessKey Key, string What, ActionResult Result);

/// <summary>
/// Applies the preferences on a rule to the processes it recognises (PRD §66).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only rules that say so, and only once per process.</b> Recording that a backup job ought to run
/// at idle priority is a note; renicing it is this program reaching out and changing the machine
/// because of a line in a file. So the opt-in is per rule rather than global — somebody can keep
/// twenty notes and have one of them act — and a process is touched once, when it is first seen.
/// </para>
/// <para>
/// Once rather than every sample for a reason worth stating: a person who lowers a process's priority
/// by hand after the rule ran has overruled the rule, and a program that quietly put it back every
/// second would be fighting them with no way to win. The rule states what a program should start at,
/// not what it must stay at.
/// </para>
/// <para>
/// Keyed on the identity pair, so a recycled pid is a new process and gets the rule applied again
/// rather than being mistaken for one already handled (§8.2, §72.2).
/// </para>
/// </remarks>
public sealed class RuleApplier {

  private readonly HashSet<ProcessKey> _handled = [];

  /// <summary>How many processes have been through this, for a test and for a diagnostic.</summary>
  public int HandledCount => this._handled.Count;

  /// <summary>Every application so far, newest last, so a timeline can record what a rule did.</summary>
  public List<RuleApplication> Log { get; } = [];

  /// <summary>Forgets what it has done, for a change of rules or a change of machine.</summary>
  public void Reset() {
    this._handled.Clear();
    this.Log.Clear();
  }

  /// <summary>
  /// Applies whatever the first matching rule asks for, to the processes not yet seen.
  /// </summary>
  /// <param name="rules">The rules, tried in order.</param>
  /// <param name="actions">What will carry the change out.</param>
  /// <param name="snapshot">This sample.</param>
  /// <returns>How many processes were acted on.</returns>
  public int Apply(ProcessRules rules, IProcessActions actions, SystemSnapshot snapshot) {
    ArgumentNullException.ThrowIfNull(rules);
    ArgumentNullException.ThrowIfNull(actions);
    ArgumentNullException.ThrowIfNull(snapshot);

    if (rules.Count == 0)
      return 0;

    var acted = 0;
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i) {
      ref readonly var process = ref processes[i];
      var key = process.Key;
      if (!this._handled.Add(key))
        continue;

      if (rules.For(process) is not { AppliesScheduling: true, HasPreferences: true } rule)
        continue;

      if (rule.PreferredPriority is { } priority)
        this.Record(key, "priority", actions.SetPriority(key, priority));

      if (rule.PreferredAffinity is { Length: > 0 } affinity && CpuList.TryParseMask(affinity, out var mask))
        this.Record(key, "affinity", actions.SetAffinity(key, mask));

      if (rule.PreferredIoPriority != IoPriorityClass.None)
        this.Record(key, "I/O priority", actions.SetIoPriority(key, new(rule.PreferredIoPriority)));

      ++acted;
    }

    // Processes that have gone are forgotten, or this grows for as long as the program runs. The
    // machine's own list is the bound: what is not in this sample cannot be acted on again.
    if (this._handled.Count > processes.Length * 2)
      this.Forget(snapshot);

    return acted;
  }

  private void Forget(SystemSnapshot snapshot) {
    var alive = new HashSet<ProcessKey>();
    var processes = snapshot.Processes;
    for (var i = 0; i < processes.Length; ++i)
      alive.Add(processes[i].Key);

    this._handled.IntersectWith(alive);
  }

  private void Record(ProcessKey key, string what, ActionResult result) {
    this.Log.Add(new(key, what, result));

    // Bounded, because a machine that starts a thousand processes a minute under a rule that cannot
    // be applied would otherwise fill memory with the same refusal.
    if (this.Log.Count > 500)
      this.Log.RemoveRange(0, this.Log.Count - 500);
  }

}
