namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// What the kernel is doing about speculative store bypass for one task (PRD §21).
/// </summary>
/// <remarks>
/// <para>
/// The per-process half of the mitigation story. Windows reports its mitigations as a policy word per
/// process and Linux does not have one, but it does publish this: the state
/// <c>prctl(PR_GET_SPECULATION_CTRL, PR_SPEC_STORE_BYPASS)</c> would return, in words, in
/// <c>status</c>. It is a property of the task rather than of the machine — a sandbox that turned the
/// mitigation on for its renderers sits beside processes that did not — which is what makes it a
/// column rather than a line on the performance page.
/// </para>
/// <para>
/// Ordered by exposure, so that sorting the column puts the processes running unmitigated on top.
/// <see cref="Unknown"/> is zero for the usual reason, and <see cref="Unrecognised"/> sorts past
/// <see cref="Vulnerable"/> deliberately: a state this build has no name for might be anything, and
/// towards safe is the one direction a security field must never round.
/// </para>
/// </remarks>
public enum SpeculationState : byte {

  /// <summary>The kernel has no answer for this task — it wrote the word itself.</summary>
  Unknown = 0,

  /// <summary>This processor is not affected, so there is nothing to mitigate.</summary>
  NotVulnerable,

  /// <summary>Mitigated for everything on the machine; no task can turn it off.</summary>
  GloballyMitigated,

  /// <summary>Mitigated for this task, and locked: not even the task itself can lift it.</summary>
  ThreadForceMitigated,

  /// <summary>Mitigated for this task, which asked for it and could ask again.</summary>
  ThreadMitigated,

  /// <summary>Not mitigated for this task, though this task could turn it on.</summary>
  ThreadVulnerable,

  /// <summary>Not mitigated, and nothing here can change that.</summary>
  Vulnerable,

  /// <summary>A word this build has no name for. Reported rather than rounded to the nearest.</summary>
  Unrecognised,

}

/// <summary>
/// What the kernel is doing about indirect branch speculation for one task (PRD §21).
/// </summary>
/// <remarks>
/// The companion to <see cref="SpeculationState"/> and a separate line in <c>status</c> because it is
/// a separate control: a process may have asked for one and not the other. Ordered by exposure for
/// the same reason, and <see cref="Unsupported"/> is kept apart from <see cref="Unknown"/> because
/// the kernel spells them differently and means different things by them.
/// </remarks>
public enum IndirectBranchState : byte {

  /// <summary>The kernel has no answer for this task.</summary>
  Unknown = 0,

  /// <summary>This architecture has no such control at all.</summary>
  Unsupported,

  /// <summary>This processor is not affected.</summary>
  NotAffected,

  /// <summary>Indirect branch speculation is off, everywhere, permanently.</summary>
  AlwaysDisabled,

  /// <summary>Off for this task, and locked.</summary>
  ConditionalForceDisabled,

  /// <summary>Off for this task, which asked for it.</summary>
  ConditionalDisabled,

  /// <summary>On for this task, which could turn it off.</summary>
  ConditionalEnabled,

  /// <summary>On everywhere, and nothing here can change that.</summary>
  AlwaysEnabled,

  /// <summary>A word this build has no name for.</summary>
  Unrecognised,

}

/// <summary>
/// The hardware protections the kernel has switched on for a task (PRD §21).
/// </summary>
/// <remarks>
/// <para>
/// From <c>x86_Thread_features</c> in <c>status</c>. This is the nearest thing Linux has to the
/// per-process mitigation policy Windows reports as <c>cet</c>, and unlike that one it is a reading
/// rather than a policy: the bits say what is switched on for this task right now, not what was
/// requested for it. A process built with the CET markings still runs without a shadow stack unless
/// the loader turned one on, and this is the field that tells the two apart.
/// </para>
/// <para>
/// A flags enum rather than a word, because both features can be on at once and because
/// <see cref="Unnamed"/> has to be expressible: a kernel that prints a third feature must widen the
/// answer rather than have it silently dropped.
/// </para>
/// </remarks>
[Flags]
public enum ThreadSecurityFeatures : ulong {

  /// <summary>Nothing is switched on. A real answer, and the ordinary one.</summary>
  None = 0,

  /// <summary>A shadow stack: return addresses are checked against a copy the program cannot write.</summary>
  ShadowStack = 1,

  /// <summary>The program may write to its own shadow stack, which weakens the protection above.</summary>
  WriteableShadowStack = 2,

  /// <summary>The kernel named a feature this build has no name for.</summary>
  Unnamed = 1ul << 63,

}

/// <summary>
/// How hard the security module is holding a process to its profile (PRD §21).
/// </summary>
/// <remarks>
/// <para>
/// The part of an LSM label that is not the label. AppArmor writes its mode in brackets after the
/// profile name — <c>/usr/bin/foo (complain)</c> — and the difference between that and
/// <c>(enforce)</c> is the whole difference between a rule that is written down and a rule that is
/// applied. A column showing only the label cannot be sorted or filtered on it, which is the thing
/// somebody auditing a machine actually wants to do.
/// </para>
/// <para>
/// SELinux states no per-process mode: a context is four fields and none of them is an enforcement
/// setting, and the machine-wide <c>enforcing</c> flag is not a property of any one process. That is
/// <see cref="Unknown"/> here and renders as the mark for a concept the platform does not have,
/// rather than as a mode nobody claimed (PRD §5.3).
/// </para>
/// </remarks>
public enum LsmConfinementMode : byte {

  /// <summary>The label states no mode — an SELinux context, or a module that writes none.</summary>
  Unknown = 0,

  /// <summary>No profile applies to this process.</summary>
  Unconfined,

  /// <summary>The profile is evaluated and violations are logged, not refused.</summary>
  Complain,

  /// <summary>Violations ask somebody, rather than being refused outright.</summary>
  Prompt,

  /// <summary>The profile is applied: what it does not permit does not happen.</summary>
  Enforce,

  /// <summary>Applied, and a violation kills the process rather than failing the call.</summary>
  Kill,

  /// <summary>A mode this build has no name for — a stack of profiles in differing modes, typically.</summary>
  Unrecognised,

}
