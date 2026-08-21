using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The word-valued security lines of <c>status</c> (PRD §21), decoded in Core so that this runs on
/// every CI leg rather than only on Linux (PRD §9.2).
/// </summary>
/// <remarks>
/// The state tables are held against the kernel's own <c>fs/proc/array.c</c>: seven strings for the
/// store-bypass switch and eight for the indirect-branch one, which do not have the same vocabulary
/// and do not even agree on which word means "this kernel cannot say". Five of the seven and three
/// of the eight were reproduced on the machine this was written on by driving
/// <c>prctl(PR_SET_SPECULATION_CTRL)</c> on a child and reading its <c>status</c> back.
/// </remarks>
[TestFixture]
public sealed class SecurityStatusParserTests {

  private static ReadOnlySpan<byte> Bytes(string text) => Encoding.ASCII.GetBytes(text);

  #region speculative store bypass

  [TestCase("\tunknown", SpeculationState.Unknown)]
  [TestCase("\tnot vulnerable", SpeculationState.NotVulnerable)]
  [TestCase("\tthread force mitigated", SpeculationState.ThreadForceMitigated)]
  [TestCase("\tthread mitigated", SpeculationState.ThreadMitigated)]
  [TestCase("\tthread vulnerable", SpeculationState.ThreadVulnerable)]
  [TestCase("\tglobally mitigated", SpeculationState.GloballyMitigated)]
  [TestCase("\tvulnerable", SpeculationState.Vulnerable)]
  public void EveryStoreBypassStateTheKernelCanWriteIsRead(string value, SpeculationState expected)
    => Assert.That(SecurityStatusParser.StoreBypass(Bytes(value)), Is.EqualTo(expected));

  /// <summary>
  /// "vulnerable" is a suffix of "thread vulnerable" and the two are opposite findings: one is a
  /// process that could turn the mitigation on and has not, the other is a machine where nothing
  /// can. A parser matching on a suffix — or on the first word — would report the second as the
  /// first, and no other column on the row would contradict it.
  /// </summary>
  [Test]
  public void ThreadVulnerableIsNotReadAsVulnerable() {
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\tthread vulnerable")), Is.EqualTo(SpeculationState.ThreadVulnerable));
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\tvulnerable")), Is.EqualTo(SpeculationState.Vulnerable));
    Assert.That(
      SecurityStatusParser.StoreBypass(Bytes("\tthread mitigated")),
      Is.Not.EqualTo(SecurityStatusParser.StoreBypass(Bytes("\tthread force mitigated")))
    );
  }

  /// <summary>
  /// A kernel that adds a word must not have every process on the machine reported as being in
  /// whichever state this build happened to guess. Unrecognised is its own answer, and it sorts past
  /// vulnerable because a state nobody can name might be anything (PRD §72.3).
  /// </summary>
  [Test]
  public void AStateThisBuildHasNoNameForIsSaidToBeUnrecognised() {
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\tsomething new")), Is.EqualTo(SpeculationState.Unrecognised));
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\t")), Is.EqualTo(SpeculationState.Unrecognised));
    Assert.That(SpeculationState.Unrecognised, Is.GreaterThan(SpeculationState.Vulnerable));
  }

  /// <summary>
  /// Sorting the column is the reason it has an order: the exposed rows must come to the top of a
  /// descending sort, ahead of every process that is mitigated one way or another.
  /// </summary>
  [Test]
  public void TheStatesAreOrderedByExposure() {
    Assert.That(SpeculationState.NotVulnerable, Is.LessThan(SpeculationState.GloballyMitigated));
    Assert.That(SpeculationState.GloballyMitigated, Is.LessThan(SpeculationState.ThreadForceMitigated));
    Assert.That(SpeculationState.ThreadForceMitigated, Is.LessThan(SpeculationState.ThreadMitigated));
    Assert.That(SpeculationState.ThreadMitigated, Is.LessThan(SpeculationState.ThreadVulnerable));
    Assert.That(SpeculationState.ThreadVulnerable, Is.LessThan(SpeculationState.Vulnerable));

    // Zero is unknown, so a record nobody filled cannot claim the processor is not affected.
    Assert.That((byte)SpeculationState.Unknown, Is.Zero);
  }

  #endregion

  #region indirect branch

  [TestCase("\tunknown", IndirectBranchState.Unknown)]
  [TestCase("\tunsupported", IndirectBranchState.Unsupported)]
  [TestCase("\tnot affected", IndirectBranchState.NotAffected)]
  [TestCase("\tconditional force disabled", IndirectBranchState.ConditionalForceDisabled)]
  [TestCase("\tconditional disabled", IndirectBranchState.ConditionalDisabled)]
  [TestCase("\tconditional enabled", IndirectBranchState.ConditionalEnabled)]
  [TestCase("\talways enabled", IndirectBranchState.AlwaysEnabled)]
  [TestCase("\talways disabled", IndirectBranchState.AlwaysDisabled)]
  public void EveryIndirectBranchStateTheKernelCanWriteIsRead(string value, IndirectBranchState expected)
    => Assert.That(SecurityStatusParser.IndirectBranch(Bytes(value)), Is.EqualTo(expected));

  /// <summary>
  /// The two lines do not share a vocabulary, and where they use the same word they mean different
  /// things by it. The kernel that cannot answer writes "unknown" on one line and "unsupported" on
  /// the other, and "unknown" is the fallback case on the second and an explicit one on the first —
  /// so one table for both would be wrong in both directions.
  /// </summary>
  [Test]
  public void TheTwoLinesAreNotTheSameVocabulary() {
    Assert.That(SecurityStatusParser.IndirectBranch(Bytes("\tthread vulnerable")), Is.EqualTo(IndirectBranchState.Unrecognised));
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\tconditional enabled")), Is.EqualTo(SpeculationState.Unrecognised));
    Assert.That(SecurityStatusParser.StoreBypass(Bytes("\tnot affected")), Is.EqualTo(SpeculationState.Unrecognised));
  }

  [Test]
  public void ConditionalDisabledIsNotReadAsConditionalForceDisabled() {
    Assert.That(
      SecurityStatusParser.IndirectBranch(Bytes("\tconditional disabled")),
      Is.EqualTo(IndirectBranchState.ConditionalDisabled)
    );
    Assert.That(
      SecurityStatusParser.IndirectBranch(Bytes("\tconditional force disabled")),
      Is.EqualTo(IndirectBranchState.ConditionalForceDisabled)
    );
  }

  #endregion

  #region thread features

  /// <summary>
  /// The kernel writes each token with a trailing space and no separator logic, so the value is
  /// "shstk wrss " rather than "shstk,wrss" and an empty value is a line that ends at its tab.
  /// </summary>
  [Test]
  public void TheFeatureTokensAreReadWithTheTrailingSpaceTheKernelWrites() {
    Assert.That(SecurityStatusParser.ThreadFeatures(Bytes("\t")), Is.EqualTo(ThreadSecurityFeatures.None));
    Assert.That(SecurityStatusParser.ThreadFeatures(Bytes("\tshstk ")), Is.EqualTo(ThreadSecurityFeatures.ShadowStack));
    Assert.That(
      SecurityStatusParser.ThreadFeatures(Bytes("\twrss ")),
      Is.EqualTo(ThreadSecurityFeatures.WriteableShadowStack)
    );
    Assert.That(
      SecurityStatusParser.ThreadFeatures(Bytes("\tshstk wrss ")),
      Is.EqualTo(ThreadSecurityFeatures.ShadowStack | ThreadSecurityFeatures.WriteableShadowStack)
    );
  }

  /// <summary>
  /// A protection this build cannot name is still a protection. Dropping it would under-report
  /// exactly the thing the column exists to report, which is the one direction a security field must
  /// never round.
  /// </summary>
  [Test]
  public void AFeatureThisBuildHasNoNameForIsStillReported() {
    Assert.That(SecurityStatusParser.ThreadFeatures(Bytes("\tibt ")), Is.EqualTo(ThreadSecurityFeatures.Unnamed));
    Assert.That(
      SecurityStatusParser.ThreadFeatures(Bytes("\tshstk ibt ")),
      Is.EqualTo(ThreadSecurityFeatures.ShadowStack | ThreadSecurityFeatures.Unnamed)
    );
    Assert.That(Humanize.ThreadFeatures(ThreadSecurityFeatures.Unnamed), Is.EqualTo("unnamed"));
  }

  /// <summary>
  /// "none" is a real answer and the ordinary one — most processes on most machines have no shadow
  /// stack — and it reads as a finding rather than as a hole.
  /// </summary>
  [Test]
  public void NoFeaturesReadsAsAnAnswerRatherThanAsAGap() {
    Assert.That(Humanize.ThreadFeatures(ThreadSecurityFeatures.None), Is.EqualTo("none"));
    Assert.That(Humanize.ThreadFeatures(ThreadSecurityFeatures.ShadowStack), Is.EqualTo("shstk"));
    Assert.That(
      Humanize.ThreadFeatures(ThreadSecurityFeatures.ShadowStack | ThreadSecurityFeatures.WriteableShadowStack),
      Is.EqualTo("shstk,wrss")
    );
  }

  #endregion

  #region the confinement mode

  [TestCase("/usr/bin/foo (enforce)", LsmConfinementMode.Enforce)]
  [TestCase("/usr/bin/foo (complain)", LsmConfinementMode.Complain)]
  [TestCase("snap.chromium.chromium (kill)", LsmConfinementMode.Kill)]
  [TestCase("thing (prompt)", LsmConfinementMode.Prompt)]
  [TestCase("thing (unconfined)", LsmConfinementMode.Unconfined)]
  [TestCase("unconfined", LsmConfinementMode.Unconfined)]
  public void AnAppArmorLabelStatesItsMode(string label, LsmConfinementMode expected)
    => Assert.That(SecurityStatusParser.ConfinementMode(label), Is.EqualTo(expected));

  /// <summary>
  /// An SELinux context is four colon-separated fields and none of them is an enforcement setting;
  /// the machine-wide <c>enforcing</c> flag is not a property of any one process. Inventing a mode
  /// for it would be the false equivalence §5.3 forbids, so there is none and the column says so.
  /// </summary>
  [TestCase("unconfined_u:unconfined_r:unconfined_t:s0-s0:c0.c1023")]
  [TestCase("system_u:system_r:init_t:s0")]
  [TestCase("")]
  [TestCase(null)]
  public void AnSeLinuxContextStatesNoMode(string? label)
    => Assert.That(SecurityStatusParser.ConfinementMode(label), Is.EqualTo(LsmConfinementMode.Unknown));

  /// <summary>
  /// A stack of profiles in differing modes writes something this table does not name. Saying so is
  /// better than picking whichever half of the stack happens to be listed first.
  /// </summary>
  [Test]
  public void AModeThisBuildHasNoNameForIsNotGuessedAt() {
    Assert.That(
      SecurityStatusParser.ConfinementMode("a//&b (mixed)"),
      Is.EqualTo(LsmConfinementMode.Unrecognised)
    );

    // And a stacked label that does state one mode still states it.
    Assert.That(
      SecurityStatusParser.ConfinementMode("a//&b (enforce)"),
      Is.EqualTo(LsmConfinementMode.Enforce)
    );
  }

  /// <summary>
  /// The brackets are the mode's, not the profile's. A profile whose own name ends in a bracketed
  /// word this table happens to know would be misread by a parser that did not require the brackets
  /// to be last — this checks the one that is.
  /// </summary>
  [Test]
  public void ALabelWithNoBracketsHasNoMode() {
    Assert.That(SecurityStatusParser.ConfinementMode("/usr/bin/foo"), Is.EqualTo(LsmConfinementMode.Unknown));
    Assert.That(SecurityStatusParser.ConfinementMode("(enforce) /usr/bin/foo"), Is.EqualTo(LsmConfinementMode.Unknown));
  }

  #endregion

}
