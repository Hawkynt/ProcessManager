using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Settings;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Notes and rules somebody has written about a program, kept across sessions (PRD §66).
/// </summary>
/// <remarks>
/// <para>
/// The dangerous half of this feature is the third box: a rule may change the machine. So the opt-in
/// is per rule rather than global, a process is touched once rather than every sample, and everything
/// below that could apply a preference by accident is asserted not to.
/// </para>
/// <para>
/// The quiet half is the second: a rule keyed on a digest, against a table nobody asked to hash
/// anything, matches nothing — and "did not match" is the wrong answer to give for it. That is a
/// third verdict rather than a false, for the same reason a counter has a reason.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ProcessRuleTests {

  private static SystemSnapshot Machine(params (string Name, string? Path, string? CommandLine, string? Hash, string? Signer)[] processes)
    => Machine(500ul, processes);

  private static SystemSnapshot Machine(
    ulong startedAt,
    params (string Name, string? Path, string? CommandLine, string? Hash, string? Signer)[] processes
  ) {
    var snapshot = new SystemSnapshot();
    var slots = snapshot.PrepareProcesses(processes.Length);
    for (var i = 0; i < processes.Length; ++i) {
      ref var record = ref slots[i];
      record.Key = new(1000 + i, startedAt + (ulong)i);
      record.Name = processes[i].Name;
      record.ImagePath = processes[i].Path;
      record.CommandLine = processes[i].CommandLine;
      record.ImageSha256 = processes[i].Hash;
      record.ImageSigner = processes[i].Signer;
    }

    return snapshot;
  }

  private static ProcessRecord One(string name, string? path = null, string? commandLine = null, string? hash = null, string? signer = null)
    => Machine((name, path, commandLine, hash, signer)).Processes[0];

  /// <summary>Records every action asked of it and refuses none, so a test sees exactly what was sent.</summary>
  private sealed class Recording : IProcessActions {
    public List<string> Sent { get; } = [];

    public ActionResult Terminate(ProcessKey key) { this.Sent.Add($"terminate {key.Pid}"); return ActionResult.Ok; }
    public ActionResult Suspend(ProcessKey key) { this.Sent.Add($"suspend {key.Pid}"); return ActionResult.Ok; }
    public ActionResult Resume(ProcessKey key) { this.Sent.Add($"resume {key.Pid}"); return ActionResult.Ok; }
    public ActionResult SendSignal(ProcessKey key, int signal) { this.Sent.Add($"signal {key.Pid} {signal}"); return ActionResult.Ok; }

    public ActionResult SetPriority(ProcessKey key, int priority) {
      this.Sent.Add($"priority {key.Pid} {priority}");
      return ActionResult.Ok;
    }

    public ActionResult SetAffinity(ProcessKey key, ulong mask) {
      this.Sent.Add($"affinity {key.Pid} {mask:x}");
      return ActionResult.Ok;
    }

    public ActionResult SetIoPriority(ProcessKey key, IoPriority priority) {
      this.Sent.Add($"io {key.Pid} {priority.Class}");
      return ActionResult.Ok;
    }
  }

  // --- what a rule recognises -------------------------------------------------------------------

  [Test]
  public void APathRuleRecognisesThePath() {
    var rule = new ProcessRule(RuleMatch.Path, "/usr/bin/firefox");
    Assert.That(rule.AppliesTo(One("firefox", path: "/usr/bin/firefox")), Is.EqualTo(RuleVerdict.Match));
    Assert.That(rule.AppliesTo(One("firefox", path: "/opt/firefox/firefox")), Is.EqualTo(RuleVerdict.NoMatch));
  }

  /// <summary>
  /// A star and a question mark mean what they mean in a shell. Nothing else does — a rule file is
  /// edited by hand, and every character of a path is a metacharacter in a regular expression.
  /// </summary>
  [TestCase("/usr/bin/*", "/usr/bin/firefox", true)]
  [TestCase("/usr/bin/*", "/usr/local/bin/firefox", false)]
  [TestCase("*firefox", "/opt/firefox/firefox", true)]
  [TestCase("/usr/bin/pytho?", "/usr/bin/python", true)]
  [TestCase("/usr/bin/pytho?", "/usr/bin/pythonic", false)]
  [TestCase("*", "/anything/at/all", true)]
  public void AStarAndAQuestionMarkAreThePattern(string pattern, string path, bool matches)
    => Assert.That(
      new ProcessRule(RuleMatch.Path, pattern).AppliesTo(One("x", path: path)),
      Is.EqualTo(matches ? RuleVerdict.Match : RuleVerdict.NoMatch)
    );

  /// <summary>
  /// A dot, a plus and a bracket are characters and not syntax. This is the assertion that says the
  /// pattern language is a glob rather than an expression somebody has to escape.
  /// </summary>
  [TestCase("/usr/lib/lib.so", "/usr/libxlibaso", false)]
  [TestCase("/opt/g++/bin", "/opt/g++/bin", true)]
  [TestCase("/opt/[old]/x", "/opt/[old]/x", true)]
  [TestCase("/opt/[old]/x", "/opt/o/x", false)]
  public void NothingElseIsSyntax(string pattern, string path, bool matches)
    => Assert.That(
      new ProcessRule(RuleMatch.Path, pattern).AppliesTo(One("x", path: path)),
      Is.EqualTo(matches ? RuleVerdict.Match : RuleVerdict.NoMatch)
    );

  /// <summary>
  /// A digest is compared and never globbed: an asterisk in a hash is a typo, and treating it as a
  /// wildcard would silently turn one rule into a rule about most of the machine.
  /// </summary>
  [Test]
  public void ADigestIsComparedAndNotGlobbed() {
    const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    Assert.That(new ProcessRule(RuleMatch.Hash, Hash).AppliesTo(One("x", hash: Hash)), Is.EqualTo(RuleVerdict.Match));
    Assert.That(new ProcessRule(RuleMatch.Hash, "9f86*").AppliesTo(One("x", hash: Hash)), Is.EqualTo(RuleVerdict.NoMatch));
  }

  /// <summary>And case-insensitively, because a digest is written both ways and means the same thing.</summary>
  [Test]
  public void ADigestIsTheSameInEitherCase()
    => Assert.That(
      new ProcessRule(RuleMatch.Hash, "9F86D081").AppliesTo(One("x", hash: "9f86d081")),
      Is.EqualTo(RuleVerdict.Match)
    );

  [Test]
  public void ACommandLineRuleReadsTheWholeLine()
    => Assert.That(
      new ProcessRule(RuleMatch.CommandLine, "*--headless*").AppliesTo(One("chrome", commandLine: "chrome --headless --x")),
      Is.EqualTo(RuleVerdict.Match)
    );

  [Test]
  public void ASignerRuleReadsWhoSignedIt()
    => Assert.That(
      new ProcessRule(RuleMatch.Signer, "Mozilla*").AppliesTo(One("firefox", signer: "Mozilla Corporation")),
      Is.EqualTo(RuleVerdict.Match)
    );

  /// <summary>
  /// <b>The one that matters.</b> A digest and a signer are read on request rather than every sample,
  /// so a rule keyed on one against a record nobody hashed is not a rule that failed to match. Calling
  /// it "no" would silently drop every hash rule on a table that never computes hashes, and the person
  /// would watch their rule do nothing with nothing to tell them why (PRD §72.3).
  /// </summary>
  [Test]
  public void AReadingNobodyTookIsNeitherYesNorNo() {
    Assert.Multiple(() => {
      Assert.That(new ProcessRule(RuleMatch.Hash, "abc").AppliesTo(One("x")), Is.EqualTo(RuleVerdict.Unknown));
      Assert.That(new ProcessRule(RuleMatch.Signer, "*").AppliesTo(One("x")), Is.EqualTo(RuleVerdict.Unknown));
      Assert.That(new ProcessRule(RuleMatch.Path, "*").AppliesTo(One("x")), Is.EqualTo(RuleVerdict.Unknown));
    });
  }

  /// <summary>And the collection says so, rather than reporting a bare "nothing applies".</summary>
  [Test]
  public void TheCollectionSaysWhenItCouldNotTell() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Hash, "abc"));

    Assert.That(rules.For(One("x"), out var couldNotTell), Is.Null);
    Assert.That(couldNotTell, Is.True, "a hash rule against an unhashed record is not a miss");

    Assert.That(rules.For(One("x", hash: "def"), out couldNotTell), Is.Null);
    Assert.That(couldNotTell, Is.False, "a hash that was read and did not match really is a miss");
  }

  /// <summary>
  /// A rule with no matcher or no pattern is a line nobody finished, not a rule matching everything.
  /// The difference is somebody's half-written note landing on every process on the machine.
  /// </summary>
  [Test]
  public void AnUnfinishedRuleMatchesNothingAndIsNotStored() {
    var rules = new ProcessRules();

    Assert.Multiple(() => {
      Assert.That(rules.Add(new(RuleMatch.None, "*")), Is.False, "no matcher");
      Assert.That(rules.Add(new(RuleMatch.Path, "")), Is.False, "no pattern");
      Assert.That(rules.Add(new(RuleMatch.Path, "   ")), Is.False, "whitespace is no pattern");
      Assert.That(rules.Count, Is.Zero);
      Assert.That(new ProcessRule(RuleMatch.None, "*").AppliesTo(One("x", path: "/a")), Is.EqualTo(RuleVerdict.NoMatch));
    });
  }

  /// <summary>First match wins, and the file's order is what decides it.</summary>
  [Test]
  public void TheFirstMatchWins() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Path, "/usr/bin/*", Note: "anything in bin"));
    rules.Add(new(RuleMatch.Path, "/usr/bin/firefox", Note: "the browser"));

    Assert.That(rules.For(One("firefox", path: "/usr/bin/firefox"))?.Note, Is.EqualTo("anything in bin"));
  }

  // --- the file ---------------------------------------------------------------------------------

  /// <summary>Everything on a rule survives being written and read back.</summary>
  [Test]
  public void ARuleSurvivesTheFile() {
    var rules = new ProcessRules();
    rules.Add(new(
      RuleMatch.CommandLine,
      "*--backup*",
      Note: "the nightly job",
      Colour: "#3060c0",
      Category: "housekeeping",
      ExpectedPublisher: "Example Ltd",
      PreferredPriority: 19,
      PreferredAffinity: "0-3",
      PreferredIoPriority: IoPriorityClass.Idle,
      AppliesScheduling: true
    ));

    var read = ProcessRules.Parse(rules.Save());
    Assert.That(read.Count, Is.EqualTo(1));

    var rule = read.Rules[0];
    Assert.Multiple(() => {
      Assert.That(rule.Match, Is.EqualTo(RuleMatch.CommandLine));
      Assert.That(rule.Pattern, Is.EqualTo("*--backup*"));
      Assert.That(rule.Note, Is.EqualTo("the nightly job"));
      Assert.That(rule.Colour, Is.EqualTo("#3060c0"));
      Assert.That(rule.Category, Is.EqualTo("housekeeping"));
      Assert.That(rule.ExpectedPublisher, Is.EqualTo("Example Ltd"));
      Assert.That(rule.PreferredPriority, Is.EqualTo(19));
      Assert.That(rule.PreferredAffinity, Is.EqualTo("0-3"));
      Assert.That(rule.PreferredIoPriority, Is.EqualTo(IoPriorityClass.Idle));
      Assert.That(rule.AppliesScheduling, Is.True);
    });
  }

  /// <summary>
  /// A line this cannot understand costs that line and not the file. Refusing the whole thing turns
  /// one typo into "all your rules have vanished", which is much the worse of the two failures.
  /// </summary>
  [Test]
  public void OneBadLineDoesNotCostTheRest() {
    var read = ProcessRules.Parse(
      "# a comment\n"
      + "path\t/usr/bin/a\tfirst\n"
      + "nonsense\twhatever\n"
      + "\n"
      + "name\tb\tsecond\n"
    );

    Assert.That(read.Count, Is.EqualTo(2));
    Assert.That(read.Rules[0].Note, Is.EqualTo("first"));
    Assert.That(read.Rules[1].Note, Is.EqualTo("second"));
  }

  /// <summary>
  /// A file written by a version without the last column must not start changing the machine because
  /// a field was missing. Anything that is not an explicit yes leaves it alone.
  /// </summary>
  [Test]
  public void AShortLineDoesNotOptIn() {
    var read = ProcessRules.Parse("path\t/usr/bin/a\tnote\t\t\t\t5\n");

    Assert.That(read.Count, Is.EqualTo(1));
    Assert.That(read.Rules[0].PreferredPriority, Is.EqualTo(5), "the priority is recorded");
    Assert.That(read.Rules[0].AppliesScheduling, Is.False, "and is not applied");
  }

  /// <summary>
  /// A tab inside a note would add a column and shift every field after it along by one, which reads
  /// as a rule about something else rather than as a broken line.
  /// </summary>
  [Test]
  public void ATabInsideAValueCannotShiftTheColumns() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Name, "x", Note: "before\tafter", Category: "keep"));

    var read = ProcessRules.Parse(rules.Save());
    Assert.That(read.Count, Is.EqualTo(1));
    Assert.That(read.Rules[0].Note, Is.EqualTo("before after"));
    Assert.That(read.Rules[0].Category, Is.EqualTo("keep"), "the column after it is still itself");
  }

  [Test]
  public void AnAbsentFileIsNoRulesRatherThanAFailure() {
    var path = Path.Combine(Path.GetTempPath(), $"procman-rules-{Guid.NewGuid():N}", "rules.tsv");
    Assert.That(SettingsStore.LoadRules(path).Count, Is.Zero);
  }

  [Test]
  public void TheFileGoesBesideTheSettings() {
    var settings = Path.Combine("somewhere", "else", "procman.conf");
    Assert.That(
      SettingsStore.RulesPathFor(settings),
      Is.EqualTo(Path.Combine("somewhere", "else", "rules.tsv"))
    );
  }

  [Test]
  public void WhatIsWrittenIsWhatIsRead() {
    var directory = Path.Combine(Path.GetTempPath(), $"procman-rules-{Guid.NewGuid():N}");
    var path = Path.Combine(directory, "rules.tsv");
    try {
      var rules = new ProcessRules();
      rules.Add(new(RuleMatch.Name, "backup", Note: "nightly"));

      Assert.That(SettingsStore.SaveRules(rules, path), Is.True);
      Assert.That(SettingsStore.LoadRules(path).Rules[0].Note, Is.EqualTo("nightly"));
    } finally {
      if (Directory.Exists(directory))
        Directory.Delete(directory, true);
    }
  }

  // --- applying them ----------------------------------------------------------------------------

  /// <summary>
  /// <b>Nothing happens unless the rule says so.</b> A rule carrying every preference there is, with
  /// the opt-in off, sends not one call.
  /// </summary>
  [Test]
  public void ARuleThatHasNotOptedInChangesNothing() {
    var rules = new ProcessRules();
    rules.Add(new(
      RuleMatch.Name,
      "backup",
      PreferredPriority: 19,
      PreferredAffinity: "0-1",
      PreferredIoPriority: IoPriorityClass.Idle,
      AppliesScheduling: false
    ));

    var actions = new Recording();
    var applied = new RuleApplier().Apply(rules, actions, Machine(("backup", null, null, null, null)));

    Assert.That(applied, Is.Zero);
    Assert.That(actions.Sent, Is.Empty);
  }

  /// <summary>And when it has, exactly the three it names are sent.</summary>
  [Test]
  public void ARuleThatHasOptedInSendsWhatItNames() {
    var rules = new ProcessRules();
    rules.Add(new(
      RuleMatch.Name,
      "backup",
      PreferredPriority: 19,
      PreferredAffinity: "0-1",
      PreferredIoPriority: IoPriorityClass.Idle,
      AppliesScheduling: true
    ));

    var actions = new Recording();
    var applier = new RuleApplier();
    Assert.That(applier.Apply(rules, actions, Machine(("backup", null, null, null, null))), Is.EqualTo(1));

    Assert.That(actions.Sent, Is.EqualTo(new[] { "priority 1000 19", "affinity 1000 3", "io 1000 Idle" }));
    Assert.That(applier.Log, Has.Count.EqualTo(3));
  }

  /// <summary>
  /// Once, and not every sample. A person who lowers a priority by hand after the rule ran has
  /// overruled it, and a program putting it back every second would be fighting them with no way to
  /// win.
  /// </summary>
  [Test]
  public void AProcessIsTouchedOnce() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Name, "backup", PreferredPriority: 19, AppliesScheduling: true));

    var actions = new Recording();
    var applier = new RuleApplier();
    var machine = Machine(("backup", null, null, null, null));

    applier.Apply(rules, actions, machine);
    applier.Apply(rules, actions, machine);
    applier.Apply(rules, actions, machine);

    Assert.That(actions.Sent, Has.Count.EqualTo(1));
  }

  /// <summary>
  /// A recycled pid is a new process and gets the rule applied again, rather than being mistaken for
  /// one already handled. The identity pair is what distinguishes them (PRD §8.2).
  /// </summary>
  [Test]
  public void ARecycledPidIsANewProcess() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Name, "backup", PreferredPriority: 19, AppliesScheduling: true));

    var actions = new Recording();
    var applier = new RuleApplier();

    var first = Machine(("backup", null, null, null, null));
    applier.Apply(rules, actions, first);

    // The same pid, started later: a different process wearing a number the kernel handed back.
    var second = Machine(999_999ul, ("backup", null, null, null, null));
    Assert.That(second.Processes[0].Pid, Is.EqualTo(first.Processes[0].Pid), "the same number");
    applier.Apply(rules, actions, second);

    Assert.That(actions.Sent, Has.Count.EqualTo(2));
  }

  /// <summary>An affinity nobody can parse sends no affinity, rather than sending an empty mask.</summary>
  [Test]
  public void AnAffinityThatCannotBeParsedIsNotSent() {
    var rules = new ProcessRules();
    rules.Add(new(RuleMatch.Name, "backup", PreferredAffinity: "nonsense", AppliesScheduling: true));

    var actions = new Recording();
    new RuleApplier().Apply(rules, actions, Machine(("backup", null, null, null, null)));

    Assert.That(actions.Sent, Is.Empty);
  }

  [Test]
  public void NoRulesIsNoWork() {
    var actions = new Recording();
    Assert.That(new RuleApplier().Apply(new(), actions, Machine(("a", null, null, null, null))), Is.Zero);
    Assert.That(actions.Sent, Is.Empty);
  }

  // --- the notation -----------------------------------------------------------------------------

  [TestCase("0", 0b1ul)]
  [TestCase("0-3", 0b1111ul)]
  [TestCase("0,2", 0b101ul)]
  [TestCase("0-1,4", 0b1_0011ul)]
  [TestCase("63", 1ul << 63)]
  public void AProcessorListBecomesAMask(string list, ulong expected) {
    Assert.That(CpuList.TryParseMask(list, out var mask), Is.True);
    Assert.That(mask, Is.EqualTo(expected));
  }

  /// <summary>
  /// Strict, unlike the parser that reads kernel files. A mistyped range in a rule must be refused:
  /// the alternative is an affinity meaning something other than what somebody wrote, applied to a
  /// running program.
  /// </summary>
  [TestCase("")]
  [TestCase("nonsense")]
  [TestCase("3-1")]
  [TestCase("64")]
  [TestCase("0-64")]
  [TestCase("-1")]
  [TestCase("0,,2")]
  [TestCase("0-")]
  public void AListThatIsNotOneIsRefused(string list)
    => Assert.That(CpuList.TryParseMask(list, out _), Is.False);

  [TestCase(0b1111ul, "0-3")]
  [TestCase(0b101ul, "0,2")]
  [TestCase(0b1_0011ul, "0-1,4")]
  public void AMaskIsShownTheWayTheKernelWritesIt(ulong mask, string expected)
    => Assert.That(CpuList.Describe(mask), Is.EqualTo(expected));

  /// <summary>Round trip, which is the only assertion that catches the two disagreeing.</summary>
  [TestCase("0-7,16-23")]
  [TestCase("1")]
  [TestCase("0,2,4,6")]
  public void TheNotationSurvivesBothWays(string list) {
    Assert.That(CpuList.TryParseMask(list, out var mask), Is.True);
    Assert.That(CpuList.Describe(mask), Is.EqualTo(list));
  }

}
