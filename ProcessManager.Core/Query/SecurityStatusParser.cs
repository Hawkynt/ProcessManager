using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// The four security lines of <c>/proc/[pid]/status</c> that are words rather than numbers (PRD §21).
/// </summary>
/// <remarks>
/// <para>
/// In Core with no platform attribute, so a recorded <c>status</c> is decoded on every CI leg rather
/// than only on Linux (PRD §9.2). Every entry point takes the bytes of the line's value and returns a
/// state: nothing here allocates, because the caller is the sample loop and its budget is zero
/// (PRD §4).
/// </para>
/// <para>
/// Each table ends in an <em>unrecognised</em> answer rather than a fallback to the ordinary state. A
/// kernel that adds a word — and both of the speculation lines have gained words since they were
/// introduced — would otherwise have every process on the machine reported as being in whichever
/// state this build happened to guess, and a security column being confidently wrong is the failure
/// §72.3 exists to prevent.
/// </para>
/// </remarks>
public static class SecurityStatusParser {

  /// <summary>
  /// The <c>Speculation_Store_Bypass:</c> line.
  /// </summary>
  /// <remarks>
  /// The words are the ones <c>fs/proc/array.c</c> writes for each value of
  /// <c>prctl(PR_GET_SPECULATION_CTRL, PR_SPEC_STORE_BYPASS)</c>. Verified against that call rather
  /// than against the documentation: driving <c>PR_SET_SPECULATION_CTRL</c> through
  /// <c>PR_SPEC_DISABLE</c> and <c>PR_SPEC_FORCE_DISABLE</c> on a child and reading its
  /// <c>status</c> back produced "thread mitigated" and "thread force mitigated" respectively, and
  /// <c>PR_SPEC_DISABLE_NOEXEC</c> — which has no case of its own — produced the bare "vulnerable"
  /// the default branch writes.
  /// </remarks>
  public static SpeculationState StoreBypass(ReadOnlySpan<byte> value) {
    value = Trim(value);
    if (value.IsEmpty)
      return SpeculationState.Unrecognised;

    // Longest first: "thread vulnerable" and "vulnerable" would otherwise both be reached by a
    // suffix, and they are opposite findings about who chose the exposure.
    if (Is(value, "not vulnerable"u8))
      return SpeculationState.NotVulnerable;
    if (Is(value, "globally mitigated"u8))
      return SpeculationState.GloballyMitigated;
    if (Is(value, "thread force mitigated"u8))
      return SpeculationState.ThreadForceMitigated;
    if (Is(value, "thread mitigated"u8))
      return SpeculationState.ThreadMitigated;
    if (Is(value, "thread vulnerable"u8))
      return SpeculationState.ThreadVulnerable;
    if (Is(value, "vulnerable"u8))
      return SpeculationState.Vulnerable;
    if (Is(value, "unknown"u8))
      return SpeculationState.Unknown;

    return SpeculationState.Unrecognised;
  }

  /// <summary>
  /// The <c>SpeculationIndirectBranch:</c> line.
  /// </summary>
  /// <remarks>
  /// A separate control and so a separate line: a process may have asked for one mitigation and not
  /// the other, and this machine's processes do exactly that. Verified the same way — driving
  /// <c>PR_SPEC_INDIRECT_BRANCH</c> to <c>PR_SPEC_DISABLE</c> and <c>PR_SPEC_FORCE_DISABLE</c>
  /// produced "conditional disabled" and "conditional force disabled" while the store-bypass line
  /// beside it did not move.
  /// </remarks>
  public static IndirectBranchState IndirectBranch(ReadOnlySpan<byte> value) {
    value = Trim(value);
    if (value.IsEmpty)
      return IndirectBranchState.Unrecognised;

    if (Is(value, "not affected"u8))
      return IndirectBranchState.NotAffected;
    if (Is(value, "conditional force disabled"u8))
      return IndirectBranchState.ConditionalForceDisabled;
    if (Is(value, "conditional disabled"u8))
      return IndirectBranchState.ConditionalDisabled;
    if (Is(value, "conditional enabled"u8))
      return IndirectBranchState.ConditionalEnabled;
    if (Is(value, "always disabled"u8))
      return IndirectBranchState.AlwaysDisabled;
    if (Is(value, "always enabled"u8))
      return IndirectBranchState.AlwaysEnabled;
    if (Is(value, "unsupported"u8))
      return IndirectBranchState.Unsupported;
    if (Is(value, "unknown"u8))
      return IndirectBranchState.Unknown;

    return IndirectBranchState.Unrecognised;
  }

  /// <summary>
  /// The <c>x86_Thread_features:</c> line — the hardware protections switched on for the task.
  /// </summary>
  /// <remarks>
  /// Space-separated tokens with a trailing space, and an empty value where the task has none. Empty
  /// is a real answer and the ordinary one: pid 1 on the machine this was written on has no shadow
  /// stack, while a binary built <c>-fcf-protection=full -mshstk</c> and run with the loader tunable
  /// that enables it reports <c>shstk</c>. The line itself is absent on any architecture that is not
  /// x86 and on kernels before it was added, and that absence is unknown rather than
  /// <see cref="ThreadSecurityFeatures.None"/> — "nothing is switched on" and "nobody could tell" are
  /// opposite statements about a mitigation.
  /// </remarks>
  public static ThreadSecurityFeatures ThreadFeatures(ReadOnlySpan<byte> value) {
    var features = ThreadSecurityFeatures.None;
    var rest = Trim(value);
    while (!rest.IsEmpty) {
      var end = rest.IndexOf((byte)' ');
      var token = end < 0 ? rest : rest[..end];
      rest = end < 0 ? default : Trim(rest[(end + 1)..]);
      if (token.IsEmpty)
        continue;

      if (Is(token, "shstk"u8))
        features |= ThreadSecurityFeatures.ShadowStack;
      else if (Is(token, "wrss"u8))
        features |= ThreadSecurityFeatures.WriteableShadowStack;
      else
        // Not dropped. A protection this build has no name for is still a protection, and a column
        // that silently omitted it would under-report exactly the thing it exists to report.
        features |= ThreadSecurityFeatures.Unnamed;
    }

    return features;
  }

  /// <summary>
  /// The confinement mode an LSM label states, from the label itself (PRD §21).
  /// </summary>
  /// <remarks>
  /// <para>
  /// AppArmor writes <c>/usr/bin/foo (enforce)</c>; the profile is the label and the bracketed word
  /// is how hard it is being applied. An SELinux context — four colon-separated fields — states no
  /// mode at all, and neither does Smack, so both answer <see cref="LsmConfinementMode.Unknown"/>
  /// and the column shows the mark for a concept the platform does not have rather than inventing
  /// one (PRD §5.3).
  /// </para>
  /// <para>
  /// A string rather than bytes because the label has already been decoded by the time anybody asks;
  /// this runs once per process only when the LSM column was named, not once per line of
  /// <c>status</c>.
  /// </para>
  /// </remarks>
  public static LsmConfinementMode ConfinementMode(string? label) {
    if (string.IsNullOrEmpty(label))
      return LsmConfinementMode.Unknown;

    var text = label.AsSpan().Trim();

    // What AppArmor writes for a process no profile applies to. No brackets, and a real answer.
    if (text.Equals("unconfined", StringComparison.Ordinal))
      return LsmConfinementMode.Unconfined;

    if (text.Length < 3 || text[^1] != ')')
      return LsmConfinementMode.Unknown;

    var open = text.LastIndexOf('(');
    if (open < 0)
      return LsmConfinementMode.Unknown;

    var mode = text[(open + 1)..^1].Trim();
    if (mode.Equals("enforce", StringComparison.Ordinal))
      return LsmConfinementMode.Enforce;
    if (mode.Equals("complain", StringComparison.Ordinal))
      return LsmConfinementMode.Complain;
    if (mode.Equals("kill", StringComparison.Ordinal))
      return LsmConfinementMode.Kill;
    if (mode.Equals("prompt", StringComparison.Ordinal))
      return LsmConfinementMode.Prompt;
    if (mode.Equals("unconfined", StringComparison.Ordinal))
      return LsmConfinementMode.Unconfined;

    // A stack of profiles in differing modes writes something this does not name. Saying so is
    // better than picking whichever half of the stack is listed first.
    return LsmConfinementMode.Unrecognised;
  }

  private static bool Is(ReadOnlySpan<byte> value, ReadOnlySpan<byte> word) => value.SequenceEqual(word);

  private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value) => value.Trim(" \t\r\n"u8);

}
