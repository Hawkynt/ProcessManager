using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What makes a rule fire (PRD §84).</summary>
/// <remarks>
/// <see cref="Unspecified"/> is nought so that a rule nobody finished parsing is not one that fires.
/// The same rule §72.3 gives for counters, applied to something that interrupts people: the value
/// nobody filled in must never turn out to be a real answer.
/// </remarks>
public enum AlertTrigger : byte {

  Unspecified = 0,

  /// <summary>The condition holds — immediately, or for at least <see cref="AlertRule.Dwell"/>.</summary>
  While,

  /// <summary>A process that matches the condition has appeared.</summary>
  Appears,

  /// <summary>A process that matched the condition has gone.</summary>
  Disappears,

  /// <summary>A field of a matching process reads differently than it did last sample.</summary>
  Changes,

}

/// <summary>What happens when a rule fires (PRD §84).</summary>
/// <remarks>
/// <para>
/// Two, and deliberately not three. §84 lists an operating-system notification beside these, and §64
/// refuses it with an argument rather than deferring it: a program that put a system-wide toast on
/// screen because of a rule somebody wrote in a text file would be interrupting the whole session
/// rather than the window they were looking at. So the visual notification is the status line, in
/// both front-ends, and there is no third member here for a front-end to have to decline.
/// </para>
/// <para>
/// <b>There is no member that ends a process.</b> §84 forbids automatic termination from a rule in a
/// baseline release, and the way that is kept is that nothing in this file can reach an action: this
/// type is the whole vocabulary of what firing does, and both of its members produce a sentence.
/// </para>
/// </remarks>
[Flags]
public enum AlertAction : byte {

  /// <summary>Nothing at all, which is what a rule with no action would do.</summary>
  None = 0,

  /// <summary>Say it on the status line, where §64 puts every other notice.</summary>
  Notify = 1,

  /// <summary>Record it in the timeline of §63, so it survives being looked away from.</summary>
  Log = 2,

  /// <summary>What a rule that says nothing about it means.</summary>
  Both = Notify | Log,

}

/// <summary>
/// One rule somebody wrote (PRD §84).
/// </summary>
/// <remarks>
/// <para>
/// The condition is <see cref="ProcessQuery"/> — the filter language of §56, unchanged and shared.
/// That is the whole design: greater and less than, equals, contains and regular expressions already
/// exist over every field in the registry, and inventing a second dialect for alerts would mean two
/// parsers to keep honest and two answers to "does this match". What a rule adds to a filter is
/// <em>when</em>, which a filter has no way to say: for how long, or on appearing, or on going, or
/// on changing.
/// </para>
/// <para>
/// So the grammar is a filter with a tail:
/// </para>
/// <code>
/// process.name == "myservice" AND process.cpu.usage &gt; 80% for 30s
/// name == "sshd" disappears then notify
/// user:root AND cpu:&gt;50 changes state
/// </code>
/// <para>
/// A rule that names no action does both of them, because a rule somebody bothered to write is a
/// rule they want to be told about and a rule worth having a record of.
/// </para>
/// </remarks>
public sealed class AlertRule {

  private AlertRule(string text, string conditionText, ProcessQuery condition, AlertTrigger trigger) {
    this.Text = text;
    this.ConditionText = conditionText;
    this.Condition = condition;
    this.Trigger = trigger;
  }

  /// <summary>The rule exactly as it was written, for redisplay and for the settings file.</summary>
  public string Text { get; }

  /// <summary>Just the condition, without the tail — what a sentence about this rule quotes.</summary>
  public string ConditionText { get; }

  /// <summary>Which processes this is about, in the language of §56.</summary>
  public ProcessQuery Condition { get; }

  public AlertTrigger Trigger { get; }

  /// <summary>How long the condition must hold before <see cref="AlertTrigger.While"/> fires.</summary>
  /// <remarks>
  /// Zero means "as soon as it holds", which is a rule somebody could mean and is what a rule with no
  /// <c>for</c> says. It is not a stand-in for "unset": an unset dwell and a dwell of nought fire at
  /// the same moment, so there is nothing here for a nought to be confidently wrong about.
  /// </remarks>
  public TimeSpan Dwell { get; private init; }

  /// <summary>
  /// The length of time in the words it was written in, for the sentence a firing rule produces.
  /// </summary>
  /// <remarks>
  /// Quoted back rather than reformatted. Somebody who wrote <c>for 90s</c> is looking for the words
  /// they wrote when the notice appears, and telling them "for 1:30" makes them work out whether it
  /// is the same rule.
  /// </remarks>
  public string DwellText { get; private init; } = string.Empty;

  /// <summary>The field <see cref="AlertTrigger.Changes"/> watches. Null for every other trigger.</summary>
  public ProcessField? Watched { get; private init; }

  /// <summary>What firing does.</summary>
  public AlertAction Actions { get; private init; } = AlertAction.Both;

  /// <summary>
  /// Reads a rule, or says what is wrong with it.
  /// </summary>
  /// <remarks>
  /// Nothing here falls back to a substring search the way an interactive filter box does. A filter
  /// box is being typed into and a half-written query is an ordinary state of it; a rule comes out of
  /// a settings file, and one that quietly became a search for the word "for" would be a rule that
  /// looked armed and watched nothing.
  /// </remarks>
  public static bool TryParse(string? text, out AlertRule? rule, out string? error) {
    rule = null;
    error = null;
    if (string.IsNullOrWhiteSpace(text)) {
      error = "a rule with nothing in it watches nothing";
      return false;
    }

    var whole = text.Trim();
    var words = Split(whole);
    var actions = AlertAction.Both;
    var end = words.Count;

    // "then notify", "then log", "then notify and log" — taken off the end first, so the trigger
    // below sees the same shape whether or not anybody named an action.
    var thenAt = LastWord(words, whole, "then", end);
    if (thenAt >= 0) {
      if (!TryReadActions(words, whole, thenAt + 1, end, out actions, out error))
        return false;

      end = thenAt;
    }

    var trigger = AlertTrigger.While;
    var dwell = TimeSpan.Zero;
    var dwellText = string.Empty;
    ProcessField? watched = null;

    if (end > 0 && Word(words, whole, end - 1) is { } last) {
      if (string.Equals(last, "appears", StringComparison.OrdinalIgnoreCase)) {
        trigger = AlertTrigger.Appears;
        --end;
      } else if (string.Equals(last, "disappears", StringComparison.OrdinalIgnoreCase)) {
        trigger = AlertTrigger.Disappears;
        --end;
      }
    }

    // A rule that trails off after "for" is half-written. Letting it through would parse the word as
    // a search term and arm a rule that watches something other than what it says, which is the one
    // failure this whole parser exists to avoid.
    if (trigger == AlertTrigger.While && end >= 1
        && string.Equals(Word(words, whole, end - 1), "for", StringComparison.Ordinal)) {
      error = "'for' with no value after it: how long should the condition hold?";
      return false;
    }

    if (trigger == AlertTrigger.While && end >= 2) {
      var keyword = Word(words, whole, end - 2);
      // "for 30s" and "changes state" are both a keyword and one word after it.
      if (string.Equals(keyword, "for", StringComparison.OrdinalIgnoreCase)) {
        if (!Quantity.TryParse(Word(words, whole, end - 1), FieldUnit.Nanoseconds, out var nanoseconds)
            || nanoseconds < 0) {
          error = $"'{Word(words, whole, end - 1)}' is not a length of time";
          return false;
        }

        dwell = TimeSpan.FromTicks((long)(nanoseconds / 100d));
        dwellText = Word(words, whole, end - 1);
        end -= 2;
      } else if (string.Equals(keyword, "changes", StringComparison.OrdinalIgnoreCase)) {
        if (!FieldRegistry.TryParse(Word(words, whole, end - 1), out var field)) {
          error = $"there is no field called '{Word(words, whole, end - 1)}'";
          return false;
        }

        trigger = AlertTrigger.Changes;
        watched = field;
        end -= 2;
      }
    }

    // A bare "changes" with no field watches the state, which is the one §84 names.
    if (trigger == AlertTrigger.While && end >= 1
        && string.Equals(Word(words, whole, end - 1), "changes", StringComparison.OrdinalIgnoreCase)) {
      trigger = AlertTrigger.Changes;
      watched = ProcessField.State;
      --end;
    }

    var conditionText = end <= 0 ? string.Empty : whole[..words[end - 1].End].TrimEnd();
    if (conditionText.Length == 0) {
      error = "a rule needs something to be about; there is no condition in front of its trigger";
      return false;
    }

    if (!ProcessQuery.TryParse(conditionText, out var condition, out var problem)) {
      error = problem;
      return false;
    }

    rule = new(whole, conditionText, condition, trigger) {
      Dwell = dwell,
      DwellText = dwellText,
      Watched = watched,
      Actions = actions,
    };

    return true;
  }

  /// <summary>The rule in a sentence, for a settings dialog or a help text.</summary>
  public string Describe() {
    var what = this.Trigger switch {
      AlertTrigger.Appears => "appears",
      AlertTrigger.Disappears => "disappears",
      AlertTrigger.Changes => $"changes its {FieldRegistry.Get(this.Watched ?? ProcessField.State).Header.ToLowerInvariant()}",
      _ => this.Dwell > TimeSpan.Zero
        ? $"has matched for {this.DwellText}"
        : "matches",
    };

    return $"a process where {this.ConditionText} {what}";
  }

  #region taking the words apart

  private readonly record struct Token(int Start, int End);

  /// <summary>
  /// Where each word of the rule is, with anything inside quotes left whole.
  /// </summary>
  /// <remarks>
  /// Quote-aware and not a <c>Split(' ')</c>, because a rule may legitimately be about a process
  /// called "for" or a path with a space in it, and a tail-scanner that read the last word out of a
  /// quoted string would silently turn a condition into a trigger.
  /// </remarks>
  private static List<Token> Split(string text) {
    var words = new List<Token>();
    var i = 0;
    while (i < text.Length) {
      while (i < text.Length && char.IsWhiteSpace(text[i]))
        ++i;

      if (i >= text.Length)
        break;

      var start = i;
      var quote = '\0';
      while (i < text.Length && (quote != '\0' || !char.IsWhiteSpace(text[i]))) {
        if (quote == '\0' && text[i] is '"' or '\'')
          quote = text[i];
        else if (quote != '\0' && text[i] == quote)
          quote = '\0';

        ++i;
      }

      words.Add(new(start, i));
    }

    return words;
  }

  private static string Word(List<Token> words, string text, int index)
    => (uint)index < (uint)words.Count ? text[words[index].Start..words[index].End] : string.Empty;

  /// <summary>The last unquoted word equal to <paramref name="keyword"/>, or -1.</summary>
  private static int LastWord(List<Token> words, string text, string keyword, int end) {
    for (var i = end - 1; i >= 0; --i) {
      var word = Word(words, text, i);
      if (word.Length > 0 && word[0] is '"' or '\'')
        continue;

      if (string.Equals(word, keyword, StringComparison.OrdinalIgnoreCase))
        return i;
    }

    return -1;
  }

  private static bool TryReadActions(
    List<Token> words,
    string text,
    int from,
    int end,
    out AlertAction actions,
    out string? error
  ) {
    actions = AlertAction.None;
    error = null;
    for (var i = from; i < end; ++i) {
      var word = Word(words, text, i).TrimEnd(',');
      if (word is "and" or "AND" || word.Length == 0)
        continue;

      switch (word.ToLowerInvariant()) {
        case "notify" or "notification": actions |= AlertAction.Notify; break;
        case "log" or "record": actions |= AlertAction.Log; break;
        default:
          // Named so that "then kill" fails loudly. §84 forbids ending a process from a rule, and a
          // rule that asked for it and was quietly given a notice instead would read, to whoever
          // wrote it, as a rule that was doing something.
          error = $"'{word}' is not something a rule can do; there is notify and there is log";
          return false;
      }
    }

    if (actions != AlertAction.None)
      return true;

    error = "'then' with nothing after it";
    return false;
  }

  #endregion

}

/// <summary>
/// Runs the rules of §84 against each sample.
/// </summary>
/// <remarks>
/// <para>
/// <b>Edge-triggered, exactly as §64's own thresholds are.</b> A process that has matched a rule for
/// a minute is one thing that happened and not sixty: the rule fires when it starts holding and says
/// nothing more until it has stopped holding. The dwell of <c>for 30s</c> is measured by adding up
/// the intervals the condition held across, so a machine whose refresh is two seconds and one whose
/// refresh is a quarter of a second agree about what thirty seconds is.
/// </para>
/// <para>
/// <b>A process that does not match is not a process that stopped matching.</b> A condition over a
/// reading nobody could take does not match — that is <see cref="ProcessQuery"/>'s rule and §72.3's
/// — so an unreadable counter leaves a rule unfired rather than firing it or clearing it. The
/// difference matters most for <see cref="AlertTrigger.Disappears"/>, which is why what is
/// remembered there is the last sample a process was <em>seen</em> matching in, and not the absence
/// of a match in the sample it went.
/// </para>
/// <para>
/// Nothing here reads the machine. It is handed the snapshot and the delta the sampler took anyway,
/// so however many rules somebody writes, the cost is a walk of the table per rule and no syscall at
/// all (PRD §5.4).
/// </para>
/// </remarks>
public sealed class AlertWatch {

  private sealed class State {
    public readonly Dictionary<ProcessKey, double> HeldSeconds = [];
    public readonly HashSet<ProcessKey> Firing = [];
    public readonly HashSet<ProcessKey> Matched = [];
    public readonly Dictionary<ProcessKey, string?> Watched = [];
  }

  private readonly AlertRule[] _rules;
  private readonly State[] _states;
  private readonly Dictionary<ProcessKey, string> _names = [];

  public AlertWatch(IReadOnlyList<AlertRule> rules) {
    ArgumentNullException.ThrowIfNull(rules);
    this._rules = [.. rules];
    this._states = new State[this._rules.Length];
    for (var i = 0; i < this._states.Length; ++i)
      this._states[i] = new();
  }

  /// <summary>The rules this watch runs, for a front-end deciding whether to bother.</summary>
  public IReadOnlyList<AlertRule> Rules => this._rules;

  /// <summary>
  /// Everything the rules have to say about the interval that just ended.
  /// </summary>
  /// <remarks>
  /// The first sample of a run says nothing, for the reason §64 gives: against no previous snapshot
  /// every process on the machine has just appeared, and a program that announced three hundred of
  /// them would have taught its reader to ignore it before they finished reading the first.
  /// </remarks>
  public IReadOnlyList<Notification> Examine(SystemSnapshot snapshot, SnapshotDelta delta) {
    ArgumentNullException.ThrowIfNull(snapshot);
    ArgumentNullException.ThrowIfNull(delta);

    var found = new List<Notification>();
    if (this._rules.Length == 0)
      return found;

    var processes = snapshot.Processes;
    if (!delta.HasPrevious) {
      this.Prime(processes);
      return found;
    }

    var elapsed = delta.ElapsedSeconds;
    for (var r = 0; r < this._rules.Length; ++r) {
      var rule = this._rules[r];
      var state = this._states[r];

      // What went, before the table is walked: a process that has ended is not in it, and the only
      // record of it having ever matched is the one kept last time round.
      if (rule.Trigger == AlertTrigger.Disappears)
        foreach (var key in delta.Exited) {
          if (!state.Matched.Remove(key))
            continue;

          var name = this._names.TryGetValue(key, out var known) ? known : "a process";
          found.Add(new(
            NotificationKind.RuleFired,
            $"{name} (PID {key.Pid.ToString(CultureInfo.InvariantCulture)}) has gone, having matched {rule.ConditionText}"
          ));
        }

      foreach (var key in delta.Exited) {
        state.HeldSeconds.Remove(key);
        state.Firing.Remove(key);
        state.Matched.Remove(key);
        state.Watched.Remove(key);
      }

      for (var i = 0; i < processes.Length; ++i) {
        var key = processes[i].Key;
        var matches = rule.Condition.Matches(in processes[i], delta, i);
        if (!matches) {
          state.HeldSeconds.Remove(key);
          state.Firing.Remove(key);
          state.Matched.Remove(key);
          state.Watched.Remove(key);
          continue;
        }

        state.Matched.Add(key);
        var who = $"{processes[i].Name} (PID {key.Pid.ToString(CultureInfo.InvariantCulture)})";
        switch (rule.Trigger) {
          case AlertTrigger.Appears:
            if (delta.IsNew(i))
              found.Add(new(NotificationKind.RuleFired, $"{who} has appeared, matching {rule.ConditionText}"));

            break;

          case AlertTrigger.Changes: {
            var now = FieldAccessor.RawText(rule.Watched ?? ProcessField.State, in processes[i], delta, i);
            // A process seen for the first time has nothing to have changed from. Recording the
            // reading without announcing it is the difference between a rule that watches for a
            // change and one that fires for every process it has not met yet.
            if (state.Watched.TryGetValue(key, out var before) && !string.Equals(before, now, StringComparison.Ordinal))
              found.Add(new(
                NotificationKind.RuleFired,
                $"{who}: {FieldRegistry.Get(rule.Watched ?? ProcessField.State).Header.ToLowerInvariant()} "
                + $"changed from {Say(before)} to {Say(now)}"
              ));

            state.Watched[key] = now;
            break;
          }

          case AlertTrigger.Disappears:
            // Nothing to say while it is still here; the whole rule is about it not being.
            break;

          default: {
            // The interval is added before the comparison, so a rule that says "for 30s" against a
            // one-second refresh fires on the thirtieth sample the condition held through and not on
            // the thirty-first.
            var held = state.HeldSeconds.TryGetValue(key, out var since) ? since + elapsed : elapsed;
            state.HeldSeconds[key] = held;
            if (held + 1e-9 < rule.Dwell.TotalSeconds || !state.Firing.Add(key))
              break;

            found.Add(new(
              NotificationKind.RuleFired,
              rule.Dwell > TimeSpan.Zero
                ? $"{who} has matched {rule.ConditionText} for {rule.DwellText}"
                : $"{who} matches {rule.ConditionText}"
            ));

            break;
          }
        }
      }
    }

    this.Prime(processes);
    return found;
  }

  /// <summary>
  /// Whether any rule wants to be shown, and whether any wants to be recorded.
  /// </summary>
  /// <remarks>
  /// Asked once by the front-end rather than per notification, because a rule's actions belong to the
  /// rule and a notification is a sentence. Where several rules fire at once and one of them asked
  /// only to be logged, the honest thing at this granularity is to do both — so the split is
  /// deliberately reported per <em>watch</em>, and a front-end that shows nothing shows nothing
  /// because nobody asked for a notice at all.
  /// </remarks>
  public AlertAction Actions {
    get {
      var actions = AlertAction.None;
      foreach (var rule in this._rules)
        actions |= rule.Actions;

      return actions;
    }
  }

  private static string Say(string? reading) => reading is { Length: > 0 } ? reading : "nothing readable";

  /// <summary>
  /// The names of what is running now, so that what ends next time can be named rather than numbered.
  /// </summary>
  /// <remarks>
  /// Rebuilt rather than grown, exactly as <see cref="NotificationWatch"/> does it: a machine that
  /// has started and ended ten thousand processes holds ten thousand entries fewer than it would with
  /// a cache that only ever added.
  /// </remarks>
  private void Prime(ReadOnlySpan<ProcessRecord> processes) {
    this._names.Clear();
    foreach (ref readonly var process in processes)
      this._names[process.Key] = process.Name;
  }

}
