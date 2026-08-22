using System.Globalization;
using System.Text;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>What a rule recognises a program by (PRD §66).</summary>
public enum RuleMatch : byte {

  /// <summary>Nothing, which is what an unparsable line becomes rather than something that matches.</summary>
  None = 0,

  /// <summary>The image path, which is what was actually executed.</summary>
  Path,

  /// <summary>The digest of the image, which survives a move and not an update.</summary>
  Hash,

  /// <summary>The name the process goes by, which two programs may share.</summary>
  Name,

  /// <summary>A pattern over the whole command line, for the arguments case.</summary>
  CommandLine,

  /// <summary>Who signed the image, or what the package database recorded.</summary>
  Signer,

}

/// <summary>
/// Whether a rule applies to a process, and the third answer that is neither yes nor no (PRD §66).
/// </summary>
public enum RuleVerdict : byte {

  /// <summary>It does not describe this process.</summary>
  NoMatch = 0,

  /// <summary>It does.</summary>
  Match,

  /// <summary>
  /// The reading the rule matches on has not been taken, so nobody can say.
  /// </summary>
  /// <remarks>
  /// Its own answer for the reason §72.3 gives about every other unknown. A digest and a signer are
  /// read on request rather than every sample, so a rule keyed on one against a record nobody hashed
  /// is not a rule that failed to match — treating it as "no" would silently drop every hash rule on
  /// a table that never computes hashes, and a person would see their rule doing nothing with nothing
  /// to tell them why.
  /// </remarks>
  Unknown,

}

/// <summary>
/// One thing somebody has said about a program, kept across sessions (PRD §66).
/// </summary>
/// <param name="Match">Which reading identifies the program.</param>
/// <param name="Pattern">
/// What that reading has to be. Exact for a digest; for the rest, <c>*</c> and <c>?</c> mean what
/// they mean in a shell — and nothing else does, so a rule is a pattern and never an expression
/// somebody has to debug.
/// </param>
/// <param name="Note">Whatever the person wanted to remember about it.</param>
/// <param name="Colour">
/// The row's colour, as <c>#rrggbb</c>. Held as written rather than parsed here, because Core draws
/// nothing and a colour that only one front-end can name would be a rule that means two things.
/// </param>
/// <param name="Category">
/// What the person calls it, which is not <see cref="ProcessCategory"/>'s answer. That one is derived
/// from what the machine says; this one is what somebody decided, and folding the two together would
/// let a guess overwrite a statement.
/// </param>
/// <param name="ExpectedPublisher">
/// Who the image is supposed to be signed by. <b>A statement of expectation and not a verdict</b>: it
/// says what a person believes, and comparing it against <see cref="ProcessRecord.ImageSigner"/> is a
/// separate question with its own answer (§21, §70).
/// </param>
/// <param name="PreferredPriority">The nice value or priority class this program should run at.</param>
/// <param name="PreferredAffinity">
/// The processors it should be held to, in the kernel's own list notation — the same spelling
/// <c>taskset</c> prints and accepts, so a person can paste one either way.
/// </param>
/// <param name="PreferredIoPriority">Which class its disk requests should be scheduled in.</param>
/// <param name="AppliesScheduling">
/// Whether the three preferences above are <i>applied</i> rather than merely recorded.
/// </param>
/// <remarks>
/// <b>Off unless the rule says so, one rule at a time.</b> Recording that a backup job ought to run at
/// idle priority is a note; renicing it the moment it appears is the program reaching out and changing
/// the machine because of a line in a file. The second is a reasonable thing to ask for and an
/// unreasonable thing to assume, so it is a field on the rule rather than a global preference — a
/// person can keep twenty notes and have exactly one of them act.
/// </remarks>
public readonly record struct ProcessRule(
  RuleMatch Match,
  string Pattern,
  string? Note = null,
  string? Colour = null,
  string? Category = null,
  string? ExpectedPublisher = null,
  int? PreferredPriority = null,
  string? PreferredAffinity = null,
  IoPriorityClass PreferredIoPriority = IoPriorityClass.None,
  bool AppliesScheduling = false
) {

  /// <summary>Whether this rule could ever recognise anything.</summary>
  /// <remarks>
  /// A rule with no matcher or an empty pattern is not a rule that matches everything; it is a line
  /// nobody finished. Matching everything would apply somebody's half-written note to every process
  /// on the machine.
  /// </remarks>
  public bool IsUsable => this.Match != RuleMatch.None && !string.IsNullOrWhiteSpace(this.Pattern);

  /// <summary>Whether this rule has any preference to apply, as against something to remember.</summary>
  public bool HasPreferences
    => this.PreferredPriority.HasValue
      || !string.IsNullOrWhiteSpace(this.PreferredAffinity)
      || this.PreferredIoPriority != IoPriorityClass.None;

  /// <summary>Whether it describes this process, or whether nobody can say.</summary>
  public RuleVerdict AppliesTo(in ProcessRecord process) {
    if (!this.IsUsable)
      return RuleVerdict.NoMatch;

    var subject = this.Match switch {
      RuleMatch.Path => process.ImagePath,
      RuleMatch.Hash => process.ImageSha256,
      RuleMatch.Name => process.Name,
      RuleMatch.CommandLine => process.CommandLine,
      RuleMatch.Signer => process.ImageSigner,
      _ => null,
    };

    // A reading nobody took is not a reading that failed to match. The digest and the signer are read
    // on request rather than every sample, so this is the ordinary state of a hash rule against a
    // table that has not been asked to hash anything (PRD §72.3).
    if (subject is not { Length: > 0 })
      return RuleVerdict.Unknown;

    // A digest is compared and never matched as a pattern: an asterisk in a hash is a typo, and
    // treating it as a wildcard would turn one into a rule matching most of the machine.
    return this.Match == RuleMatch.Hash
      ? string.Equals(subject, this.Pattern, StringComparison.OrdinalIgnoreCase)
        ? RuleVerdict.Match
        : RuleVerdict.NoMatch
      : Glob.Matches(this.Pattern, subject) ? RuleVerdict.Match : RuleVerdict.NoMatch;
  }

}

/// <summary>
/// <c>*</c> and <c>?</c>, and nothing else (PRD §66).
/// </summary>
/// <remarks>
/// Deliberately not a regular expression. A rule file is edited by hand and read by somebody who
/// wants to say "anything under /usr/bin", and every character of a path — the dots, the plus in a
/// package name, the brackets a build system leaves behind — is a metacharacter in a regular
/// expression and an ordinary character here. The other half of the argument is that a pattern
/// somebody mistypes should match nothing rather than take exponential time deciding.
/// </remarks>
internal static class Glob {

  internal static bool Matches(string pattern, string subject) {
    // Iterative with a backtrack point rather than recursive: linear on everything, and no stack to
    // exhaust on a pattern full of asterisks.
    var p = 0;
    var s = 0;
    var star = -1;
    var afterStar = 0;

    while (s < subject.Length) {
      if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], subject[s]))) {
        ++p;
        ++s;
      } else if (p < pattern.Length && pattern[p] == '*') {
        star = p++;
        afterStar = s;
      } else if (star >= 0) {
        p = star + 1;
        s = ++afterStar;
      } else
        return false;
    }

    while (p < pattern.Length && pattern[p] == '*')
      ++p;

    return p == pattern.Length;
  }

  // Case-insensitively, because a path on Windows is and a name on either platform is what somebody
  // typed from memory. Ordinal and not the current culture: a rule must mean the same thing in
  // Istanbul as it does anywhere else.
  private static bool Same(char a, char b)
    => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

}

/// <summary>
/// Every rule somebody has written, and the file they live in (PRD §66).
/// </summary>
/// <remarks>
/// <para>
/// A separate file from the settings, for the reason §44's record is: this is data that grows rather
/// than a preference, and a settings file somebody edits by hand should not fill up with rows.
/// Tab-separated and one rule to a line for the same reason the usage record is — it diffs, and a
/// person can read it without this program.
/// </para>
/// <para>
/// <b>Order matters and the first match wins.</b> Not "the most specific", which sounds better and
/// cannot be defined: is a path more specific than a digest, or a command line than a signer? A file
/// read top to bottom is a rule a person can predict from looking at it.
/// </para>
/// </remarks>
public sealed class ProcessRules {

  private readonly List<ProcessRule> _rules = [];

  /// <summary>The rules, in the order they will be tried.</summary>
  public IReadOnlyList<ProcessRule> Rules => this._rules;

  /// <summary>How many there are.</summary>
  public int Count => this._rules.Count;

  /// <summary>Adds one to the end, where it will be tried last.</summary>
  /// <remarks>An unusable rule is refused rather than stored: see <see cref="ProcessRule.IsUsable"/>.</remarks>
  public bool Add(ProcessRule rule) {
    if (!rule.IsUsable)
      return false;

    this._rules.Add(rule);
    return true;
  }

  /// <summary>Forgets one by position.</summary>
  public bool RemoveAt(int index) {
    if ((uint)index >= (uint)this._rules.Count)
      return false;

    this._rules.RemoveAt(index);
    return true;
  }

  /// <summary>Forgets all of them.</summary>
  public void Clear() => this._rules.Clear();

  /// <summary>
  /// The first rule that describes this process, or none.
  /// </summary>
  /// <param name="couldNotTell">
  /// True where a rule was passed over only because the reading it matches on had not been taken. The
  /// caller can then say so instead of showing nothing, which is the difference between "no rule
  /// applies" and "you asked about a digest nobody computed" (PRD §72.3).
  /// </param>
  public ProcessRule? For(in ProcessRecord process, out bool couldNotTell) {
    couldNotTell = false;
    foreach (var rule in this._rules)
      switch (rule.AppliesTo(process)) {
        case RuleVerdict.Match: return rule;
        case RuleVerdict.Unknown: couldNotTell = true; break;
      }

    return null;
  }

  /// <summary>The same, for a caller that does not care why.</summary>
  public ProcessRule? For(in ProcessRecord process) => this.For(process, out _);

  private const string _Header =
    "# match\tpattern\tnote\tcolour\tcategory\tpublisher\tpriority\taffinity\tio\tapply";

  /// <summary>The file, as text.</summary>
  public string Save() {
    var text = new StringBuilder();
    text.AppendLine("# procman rules (PRD §66). One rule to a line, first match wins.");
    text.AppendLine("# apply=yes lets the last three columns change the machine; anything else records them only.");
    text.AppendLine(_Header);

    foreach (var rule in this._rules)
      text.Append(NameOf(rule.Match)).Append('\t')
        .Append(Clean(rule.Pattern)).Append('\t')
        .Append(Clean(rule.Note)).Append('\t')
        .Append(Clean(rule.Colour)).Append('\t')
        .Append(Clean(rule.Category)).Append('\t')
        .Append(Clean(rule.ExpectedPublisher)).Append('\t')
        .Append(rule.PreferredPriority?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('\t')
        .Append(Clean(rule.PreferredAffinity)).Append('\t')
        .Append(NameOf(rule.PreferredIoPriority)).Append('\t')
        .AppendLine(rule.AppliesScheduling ? "yes" : "no");

    return text.ToString();
  }

  /// <summary>
  /// Reads the file back.
  /// </summary>
  /// <remarks>
  /// A line this cannot understand is dropped and the rest are kept. The alternative — refusing the
  /// whole file — turns one typo into "all your rules have vanished", which is the worse of the two
  /// failures by a distance.
  /// </remarks>
  public static ProcessRules Parse(string? contents) {
    var rules = new ProcessRules();
    if (contents is not { Length: > 0 })
      return rules;

    foreach (var line in contents.Split('\n')) {
      var trimmed = line.Trim('\r', ' ', '\t');
      if (trimmed.Length == 0 || trimmed[0] == '#')
        continue;

      var fields = line.TrimEnd('\r', '\n').Split('\t');
      if (fields.Length < 2)
        continue;

      var match = MatchOf(fields[0]);
      if (match == RuleMatch.None)
        continue;

      rules.Add(new(
        match,
        fields[1].Trim(),
        Field(fields, 2),
        Field(fields, 3),
        Field(fields, 4),
        Field(fields, 5),
        int.TryParse(Field(fields, 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)
          ? priority
          : null,
        Field(fields, 7),
        IoClassOf(Field(fields, 8)),
        // Anything that is not an explicit yes leaves the machine alone. A rule file that has been
        // truncated, or written by a version that did not have this column, must not start reniceing
        // things because a field was missing.
        string.Equals(Field(fields, 9), "yes", StringComparison.OrdinalIgnoreCase)
      ));
    }

    return rules;
  }

  private static string? Field(string[] fields, int index) {
    if (index >= fields.Length)
      return null;

    var value = fields[index].Trim();
    return value.Length > 0 ? value : null;
  }

  // A tab in a value would add a column and shift every field after it along by one, which reads as
  // a rule about something else entirely rather than as a broken line.
  private static string Clean(string? value)
    => value is not { Length: > 0 } ? string.Empty : value.Replace('\t', ' ').Replace('\n', ' ').Trim();

  /// <summary>The word for a matcher, in the file and in a menu.</summary>
  public static string NameOf(RuleMatch match) => match switch {
    RuleMatch.Path => "path",
    RuleMatch.Hash => "hash",
    RuleMatch.Name => "name",
    RuleMatch.CommandLine => "cmdline",
    RuleMatch.Signer => "signer",
    _ => "none",
  };

  /// <summary>And back, for reading the file.</summary>
  public static RuleMatch MatchOf(string? word) => word?.Trim().ToLowerInvariant() switch {
    "path" => RuleMatch.Path,
    "hash" or "sha256" => RuleMatch.Hash,
    "name" => RuleMatch.Name,
    "cmdline" or "commandline" => RuleMatch.CommandLine,
    "signer" => RuleMatch.Signer,
    _ => RuleMatch.None,
  };

  private static string NameOf(IoPriorityClass io) => io switch {
    IoPriorityClass.Realtime => "realtime",
    IoPriorityClass.BestEffort => "besteffort",
    IoPriorityClass.Idle => "idle",
    _ => string.Empty,
  };

  private static IoPriorityClass IoClassOf(string? word) => word?.Trim().ToLowerInvariant() switch {
    "realtime" => IoPriorityClass.Realtime,
    "besteffort" or "best-effort" => IoPriorityClass.BestEffort,
    "idle" => IoPriorityClass.Idle,
    _ => IoPriorityClass.None,
  };

}
