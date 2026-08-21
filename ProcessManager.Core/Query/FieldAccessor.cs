using System.Globalization;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Query;

/// <summary>
/// Reads one field out of a process: as text to display, as a number to compare, and as an ordering.
/// </summary>
/// <remarks>
/// The single place any field is turned into anything. Both front-ends render through
/// <see cref="Text"/>, the view sorts through <see cref="Compare"/>, and the filter compares through
/// <see cref="Number"/> — so a value reads the same in the window and in the terminal, and sorting by
/// a column can never disagree with what that column shows (PRD §5.1).
/// </remarks>
public static class FieldAccessor {

  /// <summary>
  /// What the field shows, including the reason when it shows no value (PRD §72.3).
  /// </summary>
  /// <param name="delta">
  /// May be <see langword="null"/> before a second sample exists, in which case every derived field
  /// reads as "not sampled yet" rather than as zero.
  /// </param>
  public static string Text(ProcessField field, in ProcessRecord process, SnapshotDelta? delta, int index) {
    switch (field) {
      case ProcessField.Name: return process.Name;
      case ProcessField.Pid: return process.Pid.ToString(CultureInfo.InvariantCulture);
      case ProcessField.PidHex: return "0x" + process.Pid.ToString("X", CultureInfo.InvariantCulture);
      case ProcessField.ParentPid:
        return process.ParentPid > 0 ? process.ParentPid.ToString(CultureInfo.InvariantCulture) : "—";
      // Not "unknown": a parent that is not in this sample has exited and the process has been
      // reparented, which is a fact about the tree rather than a gap in what could be read.
      case ProcessField.ParentName: return process.ParentName ?? "—";
      case ProcessField.UserName:
        return process.UserName ?? Humanize.Placeholder(UnknownReason.NotPermitted);
      case ProcessField.State: return Humanize.State(process.State);

      case ProcessField.CpuPercent: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuPercentPerCore: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuPercentDelta: return Humanize.SignedPercent(Rated(delta, index, field));
      case ProcessField.MemoryPercent: return Humanize.Percent(Rated(delta, index, field));
      case ProcessField.CpuTime: return Humanize.Duration(process.CpuTimeNs);
      case ProcessField.UserTime: return Humanize.Duration(process.UserTimeNs);
      case ProcessField.KernelTime: return Humanize.Duration(process.KernelTimeNs);
      case ProcessField.ProportionalSet: return Humanize.Bytes(process.ProportionalBytes);
      case ProcessField.ProportionalSwap: return Humanize.Bytes(process.ProportionalSwapBytes);
      case ProcessField.FileBackedSet: return Humanize.Bytes(process.FileBackedBytes);
      case ProcessField.SharedSet: return Humanize.Bytes(process.SharedResidentBytes);
      case ProcessField.ShareableWorkingSet: return Humanize.Bytes(ShareableWorkingSet(in process));
      case ProcessField.StackBytes: return Humanize.Bytes(process.StackBytes);
      case ProcessField.LastCpu:
        // -1 is the platform declining to say, not processor number minus one.
        return process.LastCpu >= 0
          ? process.LastCpu.ToString(CultureInfo.InvariantCulture)
          : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

      case ProcessField.SchedulingClass:
        // Unknown is a stat line that stopped short or a platform that has no such notion — not the
        // ordinary class, which is what a nought here would claim (PRD §5.3).
        return process.SchedulingPolicy == SchedulingPolicy.Unknown
          ? Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)
          : Humanize.SchedulingPolicy(process.SchedulingPolicy);
      case ProcessField.CpuAffinity:
        return process.CpuAffinity ?? Humanize.Placeholder(process.CpuAffinityReason);
      case ProcessField.CpuThrottled: return Humanize.Count(process.ThrottledPeriods);

      case ProcessField.CyclesDelta: return Humanize.Rate(Rated(delta, index, field));
      case ProcessField.ContextSwitchesDelta: return Humanize.Rate(Rated(delta, index, field));
      case ProcessField.PageFaultsDelta: return Humanize.Rate(Rated(delta, index, field));

      case ProcessField.PrivateBytes: return Humanize.Bytes(process.PrivateBytes);
      case ProcessField.PrivateBytesDelta: return Humanize.SignedBytesPerSecond(Rated(delta, index, field));
      case ProcessField.PrivateWorkingSet: return Humanize.Bytes(process.PrivateWorkingSetBytes);
      case ProcessField.WorkingSetBytes: return Humanize.Bytes(process.WorkingSetBytes);
      case ProcessField.PeakWorkingSet: return Humanize.Bytes(process.PeakWorkingSetBytes);
      case ProcessField.VirtualBytes: return Humanize.Bytes(process.VirtualBytes);
      case ProcessField.PeakVirtualBytes: return Humanize.Bytes(process.PeakVirtualBytes);
      case ProcessField.PagedPool: return Humanize.Bytes(process.PagedPoolBytes);
      case ProcessField.PeakPagedPool: return Humanize.Bytes(process.PeakPagedPoolBytes);
      case ProcessField.NonPagedPool: return Humanize.Bytes(process.NonPagedPoolBytes);
      case ProcessField.PeakNonPagedPool: return Humanize.Bytes(process.PeakNonPagedPoolBytes);
      case ProcessField.Swap: return Humanize.Bytes(process.SwapBytes);

      case ProcessField.IoTotalRate:
      case ProcessField.ReadBytesPerSecond:
      case ProcessField.WriteBytesPerSecond:
        return Humanize.BytesPerSecond(Rated(delta, index, field));

      case ProcessField.ReadOperations: return Humanize.Count(process.ReadOperations);
      case ProcessField.WriteOperations: return Humanize.Count(process.WriteOperations);
      case ProcessField.OtherOperations: return Humanize.Count(process.OtherOperations);
      case ProcessField.ReadOperationsDelta:
      case ProcessField.WriteOperationsDelta:
        return Humanize.Rate(Rated(delta, index, field));

      case ProcessField.BlockIoWait: return Humanize.Duration(process.BlockIoWaitNs);
      case ProcessField.IoPriority: return IoPriorityText(in process) ?? Humanize.Placeholder(process.IoPriorityValue.Reason);

      case ProcessField.GpuPercent:
      case ProcessField.GpuEnginePercent:
      case ProcessField.GpuGraphicsPercent:
      case ProcessField.GpuComputePercent:
      case ProcessField.GpuCopyPercent:
      case ProcessField.GpuEncodePercent:
      case ProcessField.GpuDecodePercent:
        return Humanize.Percent(Rated(delta, index, field));

      case ProcessField.GpuDedicatedMemory: return Humanize.Bytes(process.GpuDedicatedBytes);
      case ProcessField.GpuSharedMemory: return Humanize.Bytes(process.GpuSharedBytes);
      case ProcessField.GpuTotalMemory: return Humanize.Bytes(GpuTotalMemory(in process));
      case ProcessField.GpuDedicatedMemoryDelta: return Humanize.SignedBytesPerSecond(Rated(delta, index, field));
      case ProcessField.GpuAdapter: return process.GpuAdapter ?? Humanize.Placeholder(process.GpuAdapterReason);
      case ProcessField.GpuEngineName: return GpuEngineName(delta, index);

      case ProcessField.Elevated: return YesNo(process.IsElevated);
      case ProcessField.Integrity:
        return process.IntegrityLevel.HasValue
          ? IntegrityName(process.IntegrityLevel.Value)
          : Humanize.Placeholder(process.IntegrityLevel.Reason);

      // PRD §21. Protected is derived from the level rather than read separately: there is one
      // reading and two questions, and "is anything keeping other processes out" is answered by the
      // same word that says which class of signer granted it.
      case ProcessField.Protected: return YesNo(IsProtected(process.ProtectionLevel));
      case ProcessField.ProtectionLevel:
        return process.ProtectionLevel.HasValue
          ? ProtectionLevelName(process.ProtectionLevel.Value)
          : Humanize.Placeholder(process.ProtectionLevel.Reason);
      case ProcessField.AppContainer: return YesNo(process.IsAppContainer);

      case ProcessField.DataExecutionPrevention: return Mitigation(process.DepPolicy, MitigationPolicy.Dep);
      case ProcessField.AddressSpaceRandomisation: return Mitigation(process.AslrPolicy, MitigationPolicy.Aslr);
      case ProcessField.ControlFlowGuard: return Mitigation(process.ControlFlowGuardPolicy, MitigationPolicy.ControlFlowGuard);
      case ProcessField.ShadowStackPolicy: return Mitigation(process.ShadowStackPolicy, MitigationPolicy.ShadowStack);
      case ProcessField.ArbitraryCodeGuard: return Mitigation(process.DynamicCodePolicy, MitigationPolicy.DynamicCode);
      case ProcessField.CodeIntegrityGuard: return Mitigation(process.BinarySignaturePolicy, MitigationPolicy.BinarySignature);

      case ProcessField.NoNewPrivileges: return YesNo(process.NoNewPrivileges);
      case ProcessField.Seccomp:
        if (!process.SeccompMode.HasValue)
          return Humanize.Placeholder(process.SeccompMode.Reason);

        return process.SeccompMode.Value switch {
          0 => "off",
          1 => "strict",
          2 => "filter",
          _ => "?",
        };

      case ProcessField.SeccompFilters: return Humanize.Count(process.SeccompFilters);

      case ProcessField.Capabilities: return Names(process.EffectiveCapabilities);
      case ProcessField.PermittedCapabilities: return Names(process.PermittedCapabilities);
      case ProcessField.InheritableCapabilities: return Names(process.InheritableCapabilities);
      case ProcessField.BoundingCapabilities: return Names(process.BoundingCapabilities);
      case ProcessField.AmbientCapabilities: return Names(process.AmbientCapabilities);
      case ProcessField.CapabilitiesHex:
        return process.EffectiveCapabilities.HasValue
          ? LinuxCapabilities.Hex(process.EffectiveCapabilities.Value)
          : Humanize.Placeholder(process.EffectiveCapabilities.Reason);

      case ProcessField.SecurityContext:
        return process.SecurityContext ?? Humanize.Placeholder(process.SecurityContextReason);
      case ProcessField.ConfinementMode:
        return process.ConfinementMode.HasValue
          ? Humanize.ConfinementMode((LsmConfinementMode)process.ConfinementMode.Value)
          : Humanize.Placeholder(process.ConfinementMode.Reason);

      case ProcessField.SpeculationStoreBypass:
        return process.SpeculationStoreBypass.HasValue
          ? Humanize.SpeculationState((SpeculationState)process.SpeculationStoreBypass.Value)
          : Humanize.Placeholder(process.SpeculationStoreBypass.Reason);
      case ProcessField.SpeculationIndirectBranch:
        return process.SpeculationIndirectBranch.HasValue
          ? Humanize.IndirectBranchState((IndirectBranchState)process.SpeculationIndirectBranch.Value)
          : Humanize.Placeholder(process.SpeculationIndirectBranch.Reason);
      case ProcessField.ThreadFeatures:
        // "none" is the answer for nearly every process there is, and it is an answer: the line was
        // read and no protection is switched on. The line being absent is the placeholder instead.
        return process.ThreadFeatures.HasValue
          ? Humanize.ThreadFeatures((ThreadSecurityFeatures)process.ThreadFeatures.Value)
          : Humanize.Placeholder(process.ThreadFeatures.Reason);
      case ProcessField.Umask: return Humanize.Umask(process.Umask);
      case ProcessField.TracerPid:
        // Zero is nobody, and saying so beats a column of noughts that look like unfilled cells.
        if (!process.TracerPid.HasValue)
          return Humanize.Placeholder(process.TracerPid.Reason);

        return process.TracerPid.Value == 0
          ? "none"
          : process.TracerPid.Value.ToString(CultureInfo.InvariantCulture);

      case ProcessField.ImageSha256:
        return process.ImageSha256 ?? Humanize.Placeholder(process.ImageHashReason);
      case ProcessField.ImageSha1:
        return process.ImageSha1 ?? Humanize.Placeholder(process.ImageHashReason);

      // "not packaged" is a finding and reads as one; the placeholder is kept for the cases where
      // nobody looked or nobody was allowed to (PRD §72.3).
      case ProcessField.Package:
        return process.Package.Text ?? Humanize.Placeholder(process.Package.Reason);
      case ProcessField.ApplicationId:
        return process.Package.ApplicationId
          ?? (process.Package.WasChecked ? "—" : Humanize.Placeholder(process.Package.Reason));
      case ProcessField.ApplicationName: return ApplicationName(in process) ?? Humanize.Placeholder(process.ApplicationNameReason);
      case ProcessField.PackageStatus:
        // "Not checked" is not a verdict about the package, it is the absence of one — verification
        // is opt-in and nobody asked. Spelling it out in the column reads as a finding, and it also
        // put the column at odds with the export, which writes nothing for it: a field that shows a
        // value and exports an empty cell is exactly what §103's invariant exists to catch, and it
        // caught this.
        return process.PackageStatus == SignatureStatus.NotChecked
          ? Humanize.Placeholder(UnknownReason.NotSampledYet)
          : process.PackageStatus.Text();
      case ProcessField.TrustChain:
        // Its own reason rather than the one above it. "Unsigned" here is a finding — somebody read
        // the package's entry and nothing had signed it — so an absent answer must not borrow the
        // word: a packaging system that records no signature at all is n/a, and a column nobody
        // switched on is "…" (PRD §72.3).
        return process.TrustChain == SignatureStatus.NotChecked
          ? Humanize.Placeholder(process.TrustChainReason)
          : process.TrustChain.Text();
      case ProcessField.Reputation:
        // Always this, and the field exists to say it out loud. There is no provider to ask, none
        // ships, and nothing about the executable leaves the machine — so the honest mark is the one
        // that means "this program has not built it" rather than "your machine cannot do it"
        // (PRD §3, §70, §97).
        return Humanize.Placeholder(UnknownReason.NotImplementedHere);
      case ProcessField.Runtime:
        return process.Runtime == ProcessRuntime.Unknown
          ? Humanize.Placeholder(process.RuntimeReason)
          : process.Runtime.Text();
      case ProcessField.ImageCreated:
        // Nought ticks is the absence of a time, not the first of January in the year one. The
        // exporter already reads it that way and wrote an empty cell while the column was showing
        // "0001-01-01", which is how the two came to disagree — and a filesystem without a birth
        // time is the ordinary case rather than a rare one, so this is what most rows would show.
        return process.ImageCreatedUtcTicks.TryGetValue(out var created) && created > 0
          ? new DateTime((long)created, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
          : Humanize.Placeholder(
              process.ImageCreatedUtcTicks.HasValue
                ? UnknownReason.NotSupportedOnPlatform
                : process.ImageCreatedUtcTicks.Reason);

      // PRD §14. Null and a reason rather than an empty cell, because "this program ships no version
      // resource" is a finding about a great many programs and "nobody asked" is not the same thing.
      case ProcessField.ImageDescription:
        return process.ImageDescription ?? Humanize.Placeholder(process.ImageVersionReason);
      case ProcessField.ImageCompany:
        return process.ImageCompany ?? Humanize.Placeholder(process.ImageVersionReason);
      case ProcessField.ImageProduct:
        return process.ImageProduct ?? Humanize.Placeholder(process.ImageVersionReason);
      case ProcessField.ImageProductVersion:
        return process.ImageProductVersion ?? Humanize.Placeholder(process.ImageVersionReason);
      case ProcessField.ImageFileVersion:
        return process.ImageFileVersion ?? Humanize.Placeholder(process.ImageVersionReason);
      case ProcessField.Subsystem:
        return process.Subsystem.HasValue
          ? SubsystemName(process.Subsystem.Value)
          : Humanize.Placeholder(process.Subsystem.Reason);
      case ProcessField.Emulation:
        return process.Emulation.HasValue
          ? EmulationName(process.Emulation.Value)
          : Humanize.Placeholder(process.Emulation.Reason);

      case ProcessField.PrivilegeChanged: return YesNo(PrivilegeChanged(in process));
      case ProcessField.EffectiveUserName:
        return process.EffectiveUserName ?? Humanize.Placeholder(UnknownReason.NotPermitted);
      case ProcessField.UserId: return Id(process.UserId);
      case ProcessField.EffectiveUserId: return Id(process.EffectiveUserId);
      case ProcessField.SavedUserId: return Id(process.SavedUserId);
      case ProcessField.FilesystemUserId: return Id(process.FilesystemUserId);
      case ProcessField.GroupId: return Id(process.GroupId);
      case ProcessField.EffectiveGroupId: return Id(process.EffectiveGroupId);
      case ProcessField.SavedGroupId: return Id(process.SavedGroupId);
      case ProcessField.FilesystemGroupId: return Id(process.FilesystemGroupId);
      case ProcessField.SupplementaryGroups:
        // The empty string is a real answer — a process in no supplementary group at all, which is
        // every kernel thread — so it says so rather than leaving a cell that reads like a hole.
        return process.SupplementaryGroups is { } groups
          ? (groups.Length > 0 ? groups : "none")
          : Humanize.Placeholder(process.SupplementaryGroupsReason);

      case ProcessField.ThreadCount: return process.ThreadCount.ToString(CultureInfo.InvariantCulture);
      case ProcessField.HandleCount: return Humanize.Count(process.HandleCount);
      case ProcessField.SocketCount: return Humanize.Count(process.SocketCount);
      case ProcessField.FileCount: return Humanize.Count(process.FileCount);
      case ProcessField.PipeCount: return Humanize.Count(process.PipeCount);
      case ProcessField.EventObjectCount: return Humanize.Count(process.EventObjectCount);
      case ProcessField.SemaphoreObjectCount: return Humanize.Count(process.SemaphoreObjectCount);
      case ProcessField.MutexObjectCount: return Humanize.Count(process.MutexObjectCount);
      case ProcessField.SectionObjectCount: return Humanize.Count(process.SectionObjectCount);
      case ProcessField.RegistryKeyCount: return Humanize.Count(process.RegistryKeyCount);
      case ProcessField.UserObjectCount: return Humanize.Count(process.UserObjectCount);
      case ProcessField.GdiObjectCount: return Humanize.Count(process.GdiObjectCount);
      case ProcessField.DescriptorTableSize: return Humanize.Count(process.DescriptorTableSize);
      case ProcessField.TcpConnectionCount: return Humanize.Count(process.TcpSocketCount);
      case ProcessField.UdpSocketCount: return Humanize.Count(process.UdpSocketCount);
      case ProcessField.ListeningSocketCount: return Humanize.Count(process.ListeningSocketCount);
      case ProcessField.RemoteEndpointCount: return Humanize.Count(process.RemoteEndpointCount);
      case ProcessField.Priority: return process.Priority.ToString(CultureInfo.InvariantCulture);
      case ProcessField.Nice: return process.Nice.ToString(CultureInfo.InvariantCulture);
      case ProcessField.Terminal: return Humanize.Terminal(process.TerminalDevice);
      case ProcessField.ExecutableName: return ExecutableName(in process);
      case ProcessField.ContainerId: return Humanize.ContainerId(process.ContainerPath) ?? "—";
      case ProcessField.UniqueSet: return Humanize.Bytes(process.UniqueBytes);
      case ProcessField.SessionId:
        return process.SessionId >= 0 ? process.SessionId.ToString(CultureInfo.InvariantCulture) : "—";
      case ProcessField.StartTime:
        return process.StartTimeUtcTicks > 0
          ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
          : "—";
      case ProcessField.Container: return process.ContainerPath ?? "—";
      case ProcessField.ImagePath: return process.ImagePath ?? "—";
      case ProcessField.CommandLine: return process.CommandLine ?? string.Empty;

      // The graphs are drawn, not written. Asking for their text is a caller bug, but returning
      // empty is friendlier than throwing in a render loop.
      case ProcessField.CpuHistory:
      case ProcessField.MemoryHistory:
      case ProcessField.IoHistory:
      default:
        return string.Empty;
    }
  }

  /// <summary>
  /// The field as a plain number, for filtering and for sorting.
  /// </summary>
  /// <returns>
  /// <see langword="null"/> when the field has no number at all — either because it is text, or
  /// because this platform does not report it. A filter must treat those two the same way: a process
  /// whose value is unknown does not match <c>&gt; 0</c>, and it does not match <c>== 0</c> either.
  /// </returns>
  public static double? Number(ProcessField field, in ProcessRecord process, SnapshotDelta? delta, int index) {
    switch (field) {
      case ProcessField.Pid:
      case ProcessField.PidHex: return process.Pid;
      case ProcessField.ParentPid: return process.ParentPid;
      case ProcessField.State: return (byte)process.State;
      case ProcessField.LastCpu: return process.LastCpu >= 0 ? process.LastCpu : null;
      case ProcessField.ThreadCount: return process.ThreadCount;
      // The class as its own kernel number, so sorting groups the real-time tasks together. Unknown
      // has no number at all, which keeps it out of both "== other" and "!= other".
      case ProcessField.SchedulingClass:
        return process.SchedulingPolicy == SchedulingPolicy.Unknown ? null : (byte)process.SchedulingPolicy;
      case ProcessField.CpuThrottled: return Number(process.ThrottledPeriods);
      case ProcessField.Priority: return process.Priority;
      case ProcessField.Nice: return process.Nice;
      case ProcessField.UniqueSet: return Number(process.UniqueBytes);
      case ProcessField.SessionId: return process.SessionId;
      case ProcessField.StartTime: return process.StartTimeUtcTicks;
      case ProcessField.ImageCreated: return Number(process.ImageCreatedUtcTicks);
      // The verdict as its own identity, so that sorting groups the rows that failed a check
      // together. "Not checked" has no number at all, which keeps a filter from matching it as if
      // it were an answer.
      case ProcessField.PackageStatus:
        return process.PackageStatus == SignatureStatus.NotChecked ? null : (byte)process.PackageStatus;
      case ProcessField.TrustChain:
        return process.TrustChain == SignatureStatus.NotChecked ? null : (byte)process.TrustChain;
      // No number at all, and deliberately: an unasked question must not sort or filter as though it
      // had an answer, however consistent that answer would be.
      case ProcessField.Reputation: return null;
      case ProcessField.Runtime:
        return process.Runtime == ProcessRuntime.Unknown ? null : (byte)process.Runtime;

      case ProcessField.Elevated: return Number(process.IsElevated);
      case ProcessField.Integrity: return Number(process.IntegrityLevel);
      // The subsystem and the emulated machine as their own identities, so sorting groups the
      // console programs together and brings the translated processes to the top of the table —
      // which is the only reason anybody sorts either column.
      case ProcessField.Subsystem: return Number(process.Subsystem);
      case ProcessField.Emulation: return Number(process.Emulation);
      case ProcessField.Protected: return Number(IsProtected(process.ProtectionLevel));
      case ProcessField.ProtectionLevel: return Number(process.ProtectionLevel);
      case ProcessField.AppContainer: return Number(process.IsAppContainer);
      // The policy words as the numbers they are, so that a filter can be handed the exact word a
      // configuration set and the rows carrying it come back. Nothing sums them: a bitfield is a
      // set, not a quantity.
      case ProcessField.DataExecutionPrevention: return Number(process.DepPolicy);
      case ProcessField.AddressSpaceRandomisation: return Number(process.AslrPolicy);
      case ProcessField.ControlFlowGuard: return Number(process.ControlFlowGuardPolicy);
      case ProcessField.ShadowStackPolicy: return Number(process.ShadowStackPolicy);
      case ProcessField.ArbitraryCodeGuard: return Number(process.DynamicCodePolicy);
      case ProcessField.CodeIntegrityGuard: return Number(process.BinarySignaturePolicy);
      case ProcessField.Seccomp: return Number(process.SeccompMode);
      case ProcessField.SeccompFilters: return Number(process.SeccompFilters);
      case ProcessField.NoNewPrivileges: return Number(process.NoNewPrivileges);
      // The states as their own ordinals, which are ordered by exposure: sorting the column
      // descending brings the unmitigated processes to the top, which is the only reason anybody
      // sorts a mitigation column.
      case ProcessField.SpeculationStoreBypass: return Number(process.SpeculationStoreBypass);
      case ProcessField.SpeculationIndirectBranch: return Number(process.SpeculationIndirectBranch);
      case ProcessField.ThreadFeatures: return Number(process.ThreadFeatures);
      case ProcessField.ConfinementMode: return Number(process.ConfinementMode);
      // The mask as the number it is, so "umask < 0022" finds the processes withholding less than
      // the machine's default.
      case ProcessField.Umask: return Number(process.Umask);
      case ProcessField.TracerPid: return Number(process.TracerPid);
      case ProcessField.DescriptorTableSize: return Number(process.DescriptorTableSize);
      // The mask as a magnitude. Not a quantity anybody adds up, but ordering by it groups the
      // privileged rows together, which is what sorting a security column is for.
      case ProcessField.Capabilities:
      case ProcessField.CapabilitiesHex: return Number(process.EffectiveCapabilities);
      case ProcessField.PermittedCapabilities: return Number(process.PermittedCapabilities);
      case ProcessField.InheritableCapabilities: return Number(process.InheritableCapabilities);
      case ProcessField.BoundingCapabilities: return Number(process.BoundingCapabilities);
      case ProcessField.AmbientCapabilities: return Number(process.AmbientCapabilities);

      case ProcessField.PrivilegeChanged: return Number(PrivilegeChanged(in process));
      // -1 is "nobody told us", not user minus one, so it has no number at all and a filter cannot
      // match it either way (PRD §3.4).
      case ProcessField.UserId: return process.UserId >= 0 ? process.UserId : null;
      case ProcessField.EffectiveUserId: return process.EffectiveUserId >= 0 ? process.EffectiveUserId : null;
      case ProcessField.SavedUserId: return process.SavedUserId >= 0 ? process.SavedUserId : null;
      case ProcessField.FilesystemUserId: return process.FilesystemUserId >= 0 ? process.FilesystemUserId : null;
      case ProcessField.GroupId: return process.GroupId >= 0 ? process.GroupId : null;
      case ProcessField.EffectiveGroupId: return process.EffectiveGroupId >= 0 ? process.EffectiveGroupId : null;
      case ProcessField.SavedGroupId: return process.SavedGroupId >= 0 ? process.SavedGroupId : null;
      case ProcessField.FilesystemGroupId: return process.FilesystemGroupId >= 0 ? process.FilesystemGroupId : null;

      case ProcessField.CpuTime: return Number(process.CpuTimeNs);
      case ProcessField.UserTime: return Number(process.UserTimeNs);
      case ProcessField.KernelTime: return Number(process.KernelTimeNs);
      case ProcessField.ProportionalSet: return Number(process.ProportionalBytes);
      case ProcessField.ProportionalSwap: return Number(process.ProportionalSwapBytes);
      case ProcessField.FileBackedSet: return Number(process.FileBackedBytes);
      case ProcessField.SharedSet: return Number(process.SharedResidentBytes);
      case ProcessField.ShareableWorkingSet: return Number(ShareableWorkingSet(in process));
      case ProcessField.StackBytes: return Number(process.StackBytes);
      case ProcessField.ReadOperations: return Number(process.ReadOperations);
      case ProcessField.WriteOperations: return Number(process.WriteOperations);
      case ProcessField.OtherOperations: return Number(process.OtherOperations);
      case ProcessField.BlockIoWait: return Number(process.BlockIoWaitNs);
      // The packed value the kernel returns, which orders by class and then by level inside it —
      // so sorting groups the real-time requesters together and puts the idle ones at the far end.
      // A filter reads better through the words, which RawText carries.
      case ProcessField.IoPriority: return Number(process.IoPriorityValue);
      case ProcessField.PrivateBytes: return Number(process.PrivateBytes);
      case ProcessField.PrivateWorkingSet: return Number(process.PrivateWorkingSetBytes);
      case ProcessField.WorkingSetBytes: return Number(process.WorkingSetBytes);
      case ProcessField.PeakWorkingSet: return Number(process.PeakWorkingSetBytes);
      case ProcessField.VirtualBytes: return Number(process.VirtualBytes);
      case ProcessField.PeakVirtualBytes: return Number(process.PeakVirtualBytes);
      case ProcessField.PagedPool: return Number(process.PagedPoolBytes);
      case ProcessField.PeakPagedPool: return Number(process.PeakPagedPoolBytes);
      case ProcessField.NonPagedPool: return Number(process.NonPagedPoolBytes);
      case ProcessField.PeakNonPagedPool: return Number(process.PeakNonPagedPoolBytes);
      case ProcessField.Swap: return Number(process.SwapBytes);
      case ProcessField.HandleCount: return Number(process.HandleCount);
      case ProcessField.SocketCount: return Number(process.SocketCount);
      case ProcessField.FileCount: return Number(process.FileCount);
      case ProcessField.PipeCount: return Number(process.PipeCount);
      case ProcessField.EventObjectCount: return Number(process.EventObjectCount);
      case ProcessField.SemaphoreObjectCount: return Number(process.SemaphoreObjectCount);
      case ProcessField.MutexObjectCount: return Number(process.MutexObjectCount);
      case ProcessField.SectionObjectCount: return Number(process.SectionObjectCount);
      case ProcessField.RegistryKeyCount: return Number(process.RegistryKeyCount);
      case ProcessField.UserObjectCount: return Number(process.UserObjectCount);
      case ProcessField.GdiObjectCount: return Number(process.GdiObjectCount);
      case ProcessField.TcpConnectionCount: return Number(process.TcpSocketCount);
      case ProcessField.UdpSocketCount: return Number(process.UdpSocketCount);
      case ProcessField.ListeningSocketCount: return Number(process.ListeningSocketCount);
      case ProcessField.RemoteEndpointCount: return Number(process.RemoteEndpointCount);

      case ProcessField.GpuDedicatedMemory: return Number(process.GpuDedicatedBytes);
      case ProcessField.GpuSharedMemory: return Number(process.GpuSharedBytes);
      case ProcessField.GpuTotalMemory: return Number(GpuTotalMemory(in process));
      // The engine sorts by its own identity, so grouping a table by it is one click. Unknown has no
      // number at all, which keeps it out of both "== compute" and "!= compute".
      case ProcessField.GpuEngineName: {
        var engine = delta?.BusiestGpuEngine(index) ?? GpuEngine.Unknown;
        return engine == GpuEngine.Unknown ? null : (byte)engine;
      }

      case ProcessField.GpuPercent:
      case ProcessField.GpuEnginePercent:
      case ProcessField.GpuGraphicsPercent:
      case ProcessField.GpuComputePercent:
      case ProcessField.GpuCopyPercent:
      case ProcessField.GpuEncodePercent:
      case ProcessField.GpuDecodePercent:
      case ProcessField.GpuDedicatedMemoryDelta:
      case ProcessField.ReadOperationsDelta:
      case ProcessField.WriteOperationsDelta:
      case ProcessField.CpuPercent:
      case ProcessField.CpuPercentPerCore:
      case ProcessField.CpuPercentDelta:
      case ProcessField.MemoryPercent:
      case ProcessField.CyclesDelta:
      case ProcessField.ContextSwitchesDelta:
      case ProcessField.PageFaultsDelta:
      case ProcessField.PrivateBytesDelta:
      case ProcessField.IoTotalRate:
      case ProcessField.ReadBytesPerSecond:
      case ProcessField.WriteBytesPerSecond: {
        var rate = Rated(delta, index, field);
        return rate.HasValue ? rate.Value : null;
      }

      default: return null;
    }
  }

  /// <summary>The field as raw text, for substring and regular-expression filtering.</summary>
  /// <remarks>
  /// Deliberately not <see cref="Text"/>: a filter must match what the value <em>is</em>, not how it
  /// was abbreviated for a column. Searching for a path should not fail because the column showed
  /// an em dash, and searching "1024" should not match a cell that reads "1.0K".
  /// </remarks>
  /// <param name="delta">
  /// Needed by the few fields whose text is derived rather than stored — the busiest GPU engine is
  /// named by comparing rates, and there is nowhere in a single sample for that name to live. Left
  /// out by a caller that has no delta, which then reads those fields as having no text, the same
  /// answer they give before a second sample exists.
  /// </param>
  public static string? RawText(
    ProcessField field,
    in ProcessRecord process,
    SnapshotDelta? delta = null,
    int index = 0
  ) => field switch {
    ProcessField.Name => process.Name,
    ProcessField.ParentName => process.ParentName,
    ProcessField.UserName => process.UserName,
    ProcessField.ImagePath => process.ImagePath,
    ProcessField.CommandLine => process.CommandLine,
    ProcessField.Container => process.ContainerPath,
    ProcessField.ContainerId => Humanize.ContainerId(process.ContainerPath),
    ProcessField.ExecutableName => process.ImagePath is { Length: > 0 } ? ExecutableName(in process) : null,
    ProcessField.Terminal => process.TerminalDevice == 0 ? null : Humanize.Terminal(process.TerminalDevice),
    ProcessField.State => Humanize.State(process.State),
    // The security states are matched by the word they show, so "elevated:yes" reads the way it
    // would be said aloud. The numeric form still works, because Number covers them too.
    ProcessField.Elevated => Word(process.IsElevated),
    ProcessField.Integrity => process.IntegrityLevel.HasValue ? IntegrityName(process.IntegrityLevel.Value) : null,
    ProcessField.NoNewPrivileges => Word(process.NoNewPrivileges),
    // The words these columns show, so "protected:yes" and "cig:microsoft" read the way they would
    // be said. Textual at all because the exporter asks only for raw text on a field of state kind,
    // and a state that renders a word and exports an empty cell is what §103's invariant catches.
    ProcessField.Protected => Word(IsProtected(process.ProtectionLevel)),
    ProcessField.ProtectionLevel => process.ProtectionLevel.HasValue
      ? ProtectionLevelName(process.ProtectionLevel.Value)
      : null,
    ProcessField.AppContainer => Word(process.IsAppContainer),
    ProcessField.DataExecutionPrevention => MitigationText(process.DepPolicy, MitigationPolicy.Dep),
    ProcessField.AddressSpaceRandomisation => MitigationText(process.AslrPolicy, MitigationPolicy.Aslr),
    ProcessField.ControlFlowGuard => MitigationText(process.ControlFlowGuardPolicy, MitigationPolicy.ControlFlowGuard),
    ProcessField.ShadowStackPolicy => MitigationText(process.ShadowStackPolicy, MitigationPolicy.ShadowStack),
    ProcessField.ArbitraryCodeGuard => MitigationText(process.DynamicCodePolicy, MitigationPolicy.DynamicCode),
    ProcessField.CodeIntegrityGuard => MitigationText(process.BinarySignaturePolicy, MitigationPolicy.BinarySignature),
    ProcessField.Seccomp => process.SeccompMode.HasValue
      ? process.SeccompMode.Value switch { 0 => "off", 1 => "strict", 2 => "filter", _ => null }
      : null,
    ProcessField.SecurityContext => process.SecurityContext,
    // The kernel's own words, so that "spec.ssb:vulnerable" reads the way it would be said and
    // matches both of the states that contain it. Textual at all because the exporter asks only for
    // raw text on a field of state kind, and a state that renders a word and exports an empty cell
    // is exactly what §103's invariant catches.
    ProcessField.SpeculationStoreBypass => process.SpeculationStoreBypass.HasValue
      ? Humanize.SpeculationState((SpeculationState)process.SpeculationStoreBypass.Value)
      : null,
    ProcessField.SpeculationIndirectBranch => process.SpeculationIndirectBranch.HasValue
      ? Humanize.IndirectBranchState((IndirectBranchState)process.SpeculationIndirectBranch.Value)
      : null,
    ProcessField.ThreadFeatures => process.ThreadFeatures.HasValue
      ? Humanize.ThreadFeatures((ThreadSecurityFeatures)process.ThreadFeatures.Value)
      : null,
    ProcessField.ConfinementMode => process.ConfinementMode.HasValue
      ? Humanize.ConfinementMode((LsmConfinementMode)process.ConfinementMode.Value)
      : null,
    // The four octal digits, because that is how somebody has the number: read off a unit file, a
    // shell profile or the output of umask itself.
    ProcessField.Umask => process.Umask.HasValue ? Humanize.Umask(process.Umask) : null,
    // The hex as it is, so a filter can be handed a digest from somewhere else and match on it.
    ProcessField.ImageSha256 => process.ImageSha256,
    ProcessField.ImageSha1 => process.ImageSha1,
    // The package as it would be said: "package:coreutils" matches, and so does a filter for the
    // version somebody read off a bug report.
    ProcessField.Package => process.Package.Text,
    ProcessField.ApplicationId => process.Package.ApplicationId,
    // The findings as well as the names, the way the package column exports "not packaged": a cell
    // that reads "none" on screen and empty in a file is the seam §103's invariant exists to catch.
    ProcessField.ApplicationName => ApplicationName(in process),
    // The vocabulary of §70 and no synonym of it, so "package.status:Unsigned" is the same word the
    // column shows. Not checked has no text, so it matches neither that nor its negation.
    ProcessField.PackageStatus => process.PackageStatus == SignatureStatus.NotChecked
      ? null
      : process.PackageStatus.Text(),
    // The same vocabulary answering a different question, so "trust.chain:Unsigned" finds the
    // packages nobody signed while "package.status:Unsigned" finds the files nothing claims.
    ProcessField.TrustChain => process.TrustChain == SignatureStatus.NotChecked
      ? null
      : process.TrustChain.Text(),
    ProcessField.Runtime => process.Runtime == ProcessRuntime.Unknown ? null : process.Runtime.Text(),
    // PRD §14. The five version-resource strings as they are, so a filter can be handed a company
    // name or a version read off a bug report and match on it. Textual at all because without it the
    // column would render a value and the export would write an empty cell, which is precisely the
    // seam §103's invariant exists to catch.
    ProcessField.ImageDescription => process.ImageDescription,
    ProcessField.ImageCompany => process.ImageCompany,
    ProcessField.ImageProduct => process.ImageProduct,
    ProcessField.ImageProductVersion => process.ImageProductVersion,
    ProcessField.ImageFileVersion => process.ImageFileVersion,
    // The words the columns show, so "subsystem:console" and "emulation:native" read the way they
    // would be said aloud. The numeric forms still work, because Number covers both.
    ProcessField.Subsystem => process.Subsystem.HasValue ? SubsystemName(process.Subsystem.Value) : null,
    ProcessField.Emulation => process.Emulation.HasValue ? EmulationName(process.Emulation.Value) : null,
    // The kernel's own spelling, which is what "sched.class:SCHED_FIFO" is written as and what chrt
    // prints. Unknown has no text, so it matches neither that nor its negation.
    ProcessField.SchedulingClass => process.SchedulingPolicy == SchedulingPolicy.Unknown
      ? null
      : Humanize.SchedulingPolicy(process.SchedulingPolicy),
    ProcessField.CpuAffinity => process.CpuAffinity,
    // The words ionice prints, so "io.priority:idle" is the filter somebody would actually type.
    // Textual at all because the exporter asks only for raw text on a field of state kind, and a
    // state that renders a word and exports an empty cell is what §103's invariant catches.
    ProcessField.IoPriority => IoPriorityText(in process),
    ProcessField.GpuAdapter => process.GpuAdapter,
    ProcessField.GpuEngineName => delta?.BusiestGpuEngine(index) is { } engine and not GpuEngine.Unknown
      ? EngineName(engine)
      : null,
    ProcessField.PrivilegeChanged => Word(PrivilegeChanged(in process)),
    ProcessField.EffectiveUserName => process.EffectiveUserName,
    // Empty is a real answer and null is not one, so the two must not collapse: a filter for a group
    // must miss a process that is in none, rather than miss one nobody could read.
    ProcessField.SupplementaryGroups => process.SupplementaryGroups,
    // The names, not the mask: "caps:cap_net_admin" is the question somebody actually has, and a
    // substring search over sixteen hex digits answers nothing. The raw form is its own field.
    // Textual at all because without this the column rendered a value on screen and exported an
    // empty cell — the exporter asks only RawText for a field of textual kind.
    ProcessField.Capabilities => Words(process.EffectiveCapabilities),
    ProcessField.PermittedCapabilities => Words(process.PermittedCapabilities),
    ProcessField.InheritableCapabilities => Words(process.InheritableCapabilities),
    ProcessField.BoundingCapabilities => Words(process.BoundingCapabilities),
    ProcessField.AmbientCapabilities => Words(process.AmbientCapabilities),
    ProcessField.CapabilitiesHex => process.EffectiveCapabilities.HasValue
      ? LinuxCapabilities.Hex(process.EffectiveCapabilities.Value)
      : null,
    ProcessField.PidHex => "0x" + process.Pid.ToString("X", CultureInfo.InvariantCulture),
    ProcessField.Pid => process.Pid.ToString(CultureInfo.InvariantCulture),
    ProcessField.ParentPid => process.ParentPid.ToString(CultureInfo.InvariantCulture),
    _ => null,
  };

  /// <summary>
  /// Orders two rows by one field. Text compares case-insensitively; numbers compare numerically;
  /// a value that is unknown sorts below every known one, whichever direction is chosen.
  /// </summary>
  public static int Compare(
    ProcessField field,
    in ProcessRecord a,
    int indexA,
    in ProcessRecord b,
    int indexB,
    SnapshotDelta? delta
  ) {
    switch (field) {
      case ProcessField.Name:
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ParentName:
        return string.Compare(a.ParentName, b.ParentName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.UserName:
        return string.Compare(a.UserName, b.UserName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.EffectiveUserName:
        return string.Compare(a.EffectiveUserName, b.EffectiveUserName, StringComparison.OrdinalIgnoreCase);
      case ProcessField.SupplementaryGroups:
        return string.Compare(a.SupplementaryGroups, b.SupplementaryGroups, StringComparison.Ordinal);
      // Ordinal, so "0-15" and "15" sort apart rather than both compared as the numbers they are
      // not: an affinity list is a set, and the only order it has is its spelling.
      case ProcessField.CpuAffinity:
        return string.Compare(a.CpuAffinity, b.CpuAffinity, StringComparison.Ordinal);
      case ProcessField.CommandLine:
        return string.Compare(a.CommandLine, b.CommandLine, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImagePath:
        return string.Compare(a.ImagePath, b.ImagePath, StringComparison.OrdinalIgnoreCase);
      case ProcessField.Container:
        return string.Compare(a.ContainerPath, b.ContainerPath, StringComparison.OrdinalIgnoreCase);
      // Ordinal, which groups identical images together — the only ordering a digest has, and the
      // one that makes "which of these are the same binary" one click.
      case ProcessField.ImageSha256:
        return string.Compare(a.ImageSha256, b.ImageSha256, StringComparison.Ordinal);
      case ProcessField.ImageSha1:
        return string.Compare(a.ImageSha1, b.ImageSha1, StringComparison.Ordinal);
      // By the text, which groups every process of one package together — the point of sorting a
      // provenance column at all.
      case ProcessField.Package:
        return string.Compare(a.Package.Text, b.Package.Text, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ApplicationId:
        return string.Compare(a.Package.ApplicationId, b.Package.ApplicationId, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ApplicationName:
        return string.Compare(ApplicationName(in a), ApplicationName(in b), StringComparison.OrdinalIgnoreCase);
      // By the text, which groups every file of one publisher or one product together — the point of
      // sorting a provenance column at all. Not by version order: "10.0.19041.1 (WinBuild…)" is a
      // string a publisher typed and has no arithmetic in it.
      case ProcessField.ImageDescription:
        return string.Compare(a.ImageDescription, b.ImageDescription, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImageCompany:
        return string.Compare(a.ImageCompany, b.ImageCompany, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImageProduct:
        return string.Compare(a.ImageProduct, b.ImageProduct, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImageProductVersion:
        return string.Compare(a.ImageProductVersion, b.ImageProductVersion, StringComparison.OrdinalIgnoreCase);
      case ProcessField.ImageFileVersion:
        return string.Compare(a.ImageFileVersion, b.ImageFileVersion, StringComparison.OrdinalIgnoreCase);
    }

    var left = Number(field, in a, delta, indexA);
    var right = Number(field, in b, delta, indexB);
    if (left is null)
      return right is null ? 0 : -1;
    if (right is null)
      return 1;

    return left.Value.CompareTo(right.Value);
  }

  private static double? Number(Counter counter) => counter.HasValue ? counter.Value : null;

  /// <summary>
  /// The well-known mandatory integrity levels, by name.
  /// </summary>
  /// <remarks>
  /// A level Microsoft adds later shows as its number rather than being flattened into the nearest
  /// name we happen to know — "0x2800" is a true statement and "medium" would not be.
  /// </remarks>
  private static string IntegrityName(ulong level) => level switch {
    0x0000 => "untrusted",
    0x1000 => "low",
    0x2000 => "medium",
    0x2100 => "medium+",
    0x3000 => "high",
    0x4000 => "system",
    0x5000 => "protected",
    _ => "0x" + level.ToString("x", CultureInfo.InvariantCulture),
  };

  #region the Windows mitigation policies (PRD §21)

  /// <summary>Which <c>PROCESS_MITIGATION_*</c> structure a stored flags word came out of.</summary>
  private enum MitigationPolicy : byte {
    Dep,
    Aslr,
    ControlFlowGuard,
    ShadowStack,
    DynamicCode,
    BinarySignature,
  }

  /// <summary>
  /// One mitigation policy as the words its bits stand for, or the reason there is no reading.
  /// </summary>
  /// <remarks>
  /// "off" is a real answer and the ordinary one for most of these on most processes — the policy
  /// was read and nothing in it was asked for — and it is emphatically not the same cell as a
  /// process this user may not open. That distinction is the whole reason these are counters
  /// (PRD §72.3).
  /// </remarks>
  private static string Mitigation(Counter policy, MitigationPolicy kind)
    => MitigationText(policy, kind) ?? Humanize.Placeholder(policy.Reason);

  /// <summary>The same, without a placeholder, for filtering and for export.</summary>
  private static string? MitigationText(Counter policy, MitigationPolicy kind) {
    if (!policy.TryGetValue(out var flags))
      return null;

    var words = kind switch {
      MitigationPolicy.Dep => Dep(flags),
      MitigationPolicy.Aslr => Aslr(flags),
      MitigationPolicy.ControlFlowGuard => ControlFlowGuard(flags),
      MitigationPolicy.ShadowStack => ShadowStack(flags),
      MitigationPolicy.DynamicCode => DynamicCode(flags),
      MitigationPolicy.BinarySignature => BinarySignature(flags),
      _ => null,
    };

    return words is { Length: > 0 } ? words : "off";
  }

  /// <summary>
  /// Every bit position below is out of the <c>PROCESS_MITIGATION_*</c> structure of the same name
  /// in <c>winnt.h</c>, as Microsoft's own reference pages print it, and not out of anybody's
  /// memory. Each structure is a union of a <c>DWORD Flags</c> with a bitfield, so bit 0 is the
  /// first member the page lists and the numbering follows the order on the page.
  /// </summary>
  /// <remarks>
  /// <c>PROCESS_MITIGATION_DEP_POLICY</c> is the one structure of the six that is not just the word:
  /// it carries a <c>BOOLEAN Permanent</c> after the union, which the probe keeps in bit 32 — above
  /// everything the word itself can occupy.
  /// </remarks>
  private static string Dep(ulong flags) {
    if ((flags & 1) == 0)
      return string.Empty;

    // Permanent is the interesting half: DEP that cannot be turned off again is a stronger statement
    // than DEP that happens to be on at the moment somebody looked.
    return (flags & (1ul << 32)) != 0 ? "on (permanent)" : "on";
  }

  private static string Aslr(ulong flags) {
    var words = new List<string>(4);
    if ((flags & (1 << 0)) != 0) words.Add("bottom-up");
    if ((flags & (1 << 1)) != 0) words.Add("force relocate");
    if ((flags & (1 << 2)) != 0) words.Add("high entropy");
    if ((flags & (1 << 3)) != 0) words.Add("no stripped images");
    return string.Join(", ", words);
  }

  private static string ControlFlowGuard(ulong flags) {
    if ((flags & (1 << 0)) == 0)
      return string.Empty;

    var words = new List<string>(4) { "on" };
    if ((flags & (1 << 1)) != 0) words.Add("export suppression");
    if ((flags & (1 << 2)) != 0) words.Add("strict");
    if ((flags & (1 << 3)) != 0) words.Add("XFG");
    else if ((flags & (1 << 4)) != 0) words.Add("XFG audit");
    return string.Join(", ", words);
  }

  private static string ShadowStack(ulong flags) {
    var words = new List<string>(4);
    if ((flags & (1 << 0)) != 0)
      // Strict is an upgrade of the same thing rather than a second thing, so it replaces the word
      // rather than being listed beside it: "on, strict" would read as two policies.
      words.Add((flags & (1 << 4)) != 0 ? "strict" : "on");

    if ((flags & (1 << 1)) != 0) words.Add("audit");
    if ((flags & (1 << 2)) != 0) words.Add("IP validation");
    if ((flags & (1 << 5)) != 0) words.Add("non-CET blocked");
    return string.Join(", ", words);
  }

  private static string DynamicCode(ulong flags) {
    // Audit is its own state and not a weaker "on": the process is not actually stopped from
    // generating code, it is only watched doing it, and reporting that as "on" would say a
    // protection is in force when nothing is being prevented (PRD §5.3).
    if ((flags & (1 << 0)) == 0)
      return (flags & (1 << 3)) != 0 ? "audit" : string.Empty;

    var words = new List<string>(3) { "on" };
    if ((flags & (1 << 1)) != 0) words.Add("thread opt-out");
    if ((flags & (1 << 2)) != 0) words.Add("remote downgrade");
    return string.Join(", ", words);
  }

  private static string BinarySignature(ulong flags) {
    var words = new List<string>(3);
    // MitigationOptIn is Microsoft plus the store plus the hardware labs, which is a wider set than
    // MicrosoftSignedOnly rather than a different one, so it is named for what it admits.
    if ((flags & (1 << 2)) != 0) words.Add("Microsoft/store/WHQL");
    else if ((flags & (1 << 0)) != 0) words.Add("Microsoft");

    if ((flags & (1 << 1)) != 0) words.Add("store");
    if ((flags & ((1 << 3) | (1 << 4))) != 0) words.Add("audit");
    return string.Join(", ", words);
  }

  /// <summary>
  /// Whether anything is keeping other processes out of this one.
  /// </summary>
  /// <remarks>
  /// <c>PROTECTION_LEVEL_NONE</c> is <c>0xFFFFFFFE</c> and not <c>-1</c>, and nought is
  /// <c>PROTECTION_LEVEL_WINTCB_LIGHT</c> — a real and rather high level. Both of those are why this
  /// is written out rather than inlined as a comparison against zero somewhere.
  /// </remarks>
  private static Counter IsProtected(Counter level)
    => level.TryGetValue(out var value) ? Counter.Of(value == _PROTECTION_LEVEL_NONE ? 0ul : 1ul) : level;

  private const ulong _PROTECTION_LEVEL_NONE = 0xFFFF_FFFE;

  /// <summary>
  /// The <c>PROTECTION_LEVEL_*</c> values by name.
  /// </summary>
  /// <remarks>
  /// The numbers are the ones <c>winbase.h</c> defines. They are not on any of Microsoft's reference
  /// pages — the page for the structure prints the constant names and no values at all — so they
  /// were taken from the header rather than from the documentation, which is stated here because it
  /// is the weakest-sourced constant in this file. A level this build does not know shows as its
  /// number, the same rule the integrity level follows.
  /// </remarks>
  private static string ProtectionLevelName(ulong level) => level switch {
    0 => "WinTCB (light)",
    1 => "Windows",
    2 => "Windows (light)",
    3 => "antimalware (light)",
    4 => "LSA (light)",
    5 => "WinTCB",
    6 => "codegen (light)",
    7 => "Authenticode",
    8 => "PPL app",
    _PROTECTION_LEVEL_NONE => "none",
    0xFFFF_FFFF => "same",
    _ => "0x" + level.ToString("x", CultureInfo.InvariantCulture),
  };

  #endregion

  /// <summary>
  /// The <c>IMAGE_SUBSYSTEM_*</c> values by name (PRD §14).
  /// </summary>
  /// <remarks>
  /// The numbers are the "Windows Subsystem" table of Microsoft's PE format specification, not
  /// anybody's memory of it. A subsystem this build does not know shows as its number rather than
  /// being flattened into the nearest name there is — "0x11" is a true statement and "console" would
  /// not be, which is the same rule the integrity level follows above.
  /// </remarks>
  private static string SubsystemName(ulong subsystem) => subsystem switch {
    0 => "unknown",
    1 => "native",
    2 => "GUI",
    3 => "console",
    5 => "OS/2",
    7 => "POSIX",
    8 => "native Windows",
    9 => "Windows CE",
    10 => "EFI application",
    11 => "EFI boot driver",
    12 => "EFI runtime driver",
    13 => "EFI ROM",
    14 => "Xbox",
    16 => "boot application",
    _ => "0x" + subsystem.ToString("x", CultureInfo.InvariantCulture),
  };

  /// <summary>
  /// Which instruction set a process is being translated from, by the name of the machine
  /// (PRD §14).
  /// </summary>
  /// <remarks>
  /// Nought is <c>IMAGE_FILE_MACHINE_UNKNOWN</c>, which is what <c>IsWow64Process2</c> reports for a
  /// process that is <em>not</em> being translated — so it is a real answer here and the ordinary
  /// one, and it says "native" rather than leaving a cell that reads like a hole (PRD §72.3). The
  /// numbers are the PE format specification's machine-type table.
  /// </remarks>
  private static string EmulationName(ulong machine) => machine switch {
    0x0000 => "native",
    0x014C => "x86",
    0x01C0 => "ARM",
    0x01C4 => "ARM Thumb-2",
    0x0200 => "Itanium",
    0x8664 => "x64",
    0xA641 => "ARM64EC",
    0xAA64 => "ARM64",
    _ => "0x" + machine.ToString("x4", CultureInfo.InvariantCulture),
  };

  /// <summary>A capability mask by name, or the reason there is none.</summary>
  private static string Names(Counter mask)
    => mask.HasValue ? LinuxCapabilities.Describe(mask.Value) : Humanize.Placeholder(mask.Reason);

  /// <summary>
  /// The same for filtering: never a placeholder, and never abbreviated.
  /// </summary>
  /// <remarks>
  /// A filter must match what the value is rather than how the column shortened it, so a search for
  /// <c>cap_sys_module</c> cannot miss the root processes that hold it precisely because they hold
  /// every capability there is and the column says "all".
  /// </remarks>
  private static string? Words(Counter mask)
    => mask.HasValue ? LinuxCapabilities.List(mask.Value) : null;

  /// <summary>A numeric identity, or the mark for the platform declining to say who it is.</summary>
  /// <remarks>
  /// -1 is the whole point: zero is root, so rendering an unfilled id as its number would name the
  /// superuser for every process on a platform that does not report the quartet at all.
  /// <para>
  /// An <see langword="int"/> cannot carry <em>why</em> the way a <see cref="Counter"/> does, so the
  /// one reason it can give is the one that is nearly always true — the quartet is a Unix idea and
  /// the platforms that have no answer are the ones that have no such thing. A
  /// <c>hidepid</c> mount, where the file exists and this user may not read it, would be better
  /// served by the other mark; that is the cost of holding these as plain numbers, and the
  /// alternative is eight more counters in a struct that is refreshed a thousand times a second.
  /// </para>
  /// </remarks>
  private static string Id(int id) => id >= 0
    ? id.ToString(CultureInfo.InvariantCulture)
    : Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

  /// <summary>
  /// Whether the process is running as somebody other than whoever started it.
  /// </summary>
  /// <remarks>
  /// Real against effective, for the user and for the group: a set-group-ID binary is the same kind
  /// of thing as a set-user-ID one and hiding it under a field named for the more famous half would
  /// be the false equivalence §5.3 forbids. Unknown when either half is, because "they are the same"
  /// is a claim and an absence is not evidence for it.
  /// </remarks>
  private static Counter PrivilegeChanged(in ProcessRecord process) {
    if (process.UserId < 0 || process.EffectiveUserId < 0)
      return Counter.NotSupported;

    if (process.UserId != process.EffectiveUserId)
      return Counter.Of(1ul);

    if (process.GroupId < 0 || process.EffectiveGroupId < 0)
      return Counter.NotSupported;

    return Counter.Of(process.GroupId != process.EffectiveGroupId ? 1ul : 0ul);
  }

  /// <summary>A yes/no counter as the word, or the reason there is no answer.</summary>
  private static string YesNo(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : Humanize.Placeholder(counter.Reason);

  /// <summary>The same, but <see langword="null"/> rather than a placeholder, for filtering.</summary>
  private static string? Word(Counter counter)
    => counter.HasValue ? (counter.Value != 0 ? "yes" : "no") : null;

  /// <summary>
  /// The file that is running, which is not always what the process calls itself.
  /// </summary>
  /// <remarks>
  /// A process can rename itself and many do — a browser's helpers, anything using an interpreter,
  /// anything that rewrote its own argv. The name is what it claims; this is what it is.
  /// </remarks>
  private static string ExecutableName(in ProcessRecord process) {
    if (process.ImagePath is not { Length: > 0 } path)
      return "—";

    var slash = path.LastIndexOf('/');
    if (slash < 0)
      slash = path.LastIndexOf('\\');

    return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
  }

  /// <summary>
  /// The application a process is, as text, or <see langword="null"/> when there is no answer to
  /// give (PRD §14).
  /// </summary>
  /// <remarks>
  /// Three of the four cases are answers and only the fourth is a hole. "none" is the machine
  /// having no desktop entry for this program, which is true of most of a process table; "several"
  /// is more than one application starting it and nothing saying which. Both are findings and both
  /// are written the same way in the column, in a filter and in an exported file, so that no reader
  /// and no spreadsheet sees a different one (PRD §72.3).
  /// </remarks>
  private static string? ApplicationName(in ProcessRecord process) {
    if (process.ApplicationName is { Length: > 0 } name)
      return name;

    if (process.ApplicationNameAmbiguous)
      return "several";

    return process.ApplicationNameReason == UnknownReason.None ? "none" : null;
  }

  /// <summary>
  /// The resident memory another process could be mapping too, or the reason there is none
  /// (PRD §16).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The file-backed and shared halves added, wherever both are known — which on Linux is every
  /// process with a <c>status</c> that could be read, because <c>VmRSS</c> is <c>RssAnon</c> plus
  /// <c>RssFile</c> plus <c>RssShmem</c> by construction. <b>Not</b> the working set less its
  /// private part, although that is the same quantity and is what Windows computes: the working set
  /// on Linux comes out of <c>stat</c> and the three halves out of <c>status</c>, two files read a
  /// few microseconds apart and without a lock, so the subtraction disagrees with the sum of the two
  /// columns beside it by however much the process allocated in between. Measured on pid 1 of the
  /// machine this was written on: 11,235,328 by subtraction against 11,317,248 by addition, eighty
  /// kilobytes of drift in a row where all four numbers are on screen together.
  /// </para>
  /// <para>
  /// The subtraction is the fall-back and only that, for a platform that reports a working set and a
  /// private working set and no breakdown between them. A private set larger than the working set is
  /// arithmetic that did not survive the same drift, and that is
  /// <see cref="UnknownReason.CounterInvalid"/> rather than a nought — which would read as "this
  /// process shares nothing", the commonest thing a process is not.
  /// </para>
  /// </remarks>
  private static Counter ShareableWorkingSet(in ProcessRecord process) {
    var fileBacked = process.FileBackedBytes;
    var shared = process.SharedResidentBytes;
    if (fileBacked.HasValue && shared.HasValue)
      return Counter.Of(fileBacked.Value + shared.Value);

    var resident = process.WorkingSetBytes;
    if (!resident.HasValue)
      return resident;

    var privately = process.PrivateWorkingSetBytes;
    if (!privately.HasValue)
      return privately;

    return resident.Value >= privately.Value
      ? Counter.Of(resident.Value - privately.Value)
      : Counter.Unknown(UnknownReason.CounterInvalid);
  }

  /// <summary>
  /// The I/O scheduling class in the words <c>ionice</c> prints, or <see langword="null"/> when
  /// nobody asked and nobody could tell (PRD §17).
  /// </summary>
  /// <remarks>
  /// "default" is a reading and by far the commonest one: it is the kernel saying nothing has been
  /// set for this process and the nice value decides. Which is exactly why the counter is kept
  /// packed rather than unpacked into the record — <see cref="Model.IoPriorityClass.None"/> is what
  /// a struct nobody filled would already say, and the two must not be the same cell (PRD §72.3).
  /// </remarks>
  private static string? IoPriorityText(in ProcessRecord process)
    => process.IoPriorityValue.TryGetValue(out var packed)
      ? Model.IoPriority.Unpack((int)packed).ToString()
      : null;

  #region graphics (PRD §19)

  /// <summary>
  /// Dedicated and shared adapter memory together, or the reason there is no total.
  /// </summary>
  /// <remarks>
  /// A card that reports only one of the two still has a total, and it is the half that is known:
  /// an integrated part has no dedicated memory to report and a discrete one under NVML publishes no
  /// system-memory figure, so insisting on both would leave the column empty on every machine there
  /// is. Only when neither is known is there nothing to add, and the reason travels from whichever
  /// half was asked first.
  /// </remarks>
  private static Counter GpuTotalMemory(in ProcessRecord process) {
    var dedicated = process.GpuDedicatedBytes;
    var shared = process.GpuSharedBytes;
    if (!dedicated.HasValue && !shared.HasValue)
      return dedicated;

    return Counter.Of(dedicated.GetValueOrDefault() + shared.GetValueOrDefault());
  }

  /// <summary>
  /// The busiest engine's name, or why there is none.
  /// </summary>
  /// <remarks>
  /// An engine of <see cref="GpuEngine.Unknown"/> beside a real percentage means the process is
  /// using none of the adapter, and an empty cell says that better than a word would. Beside a
  /// percentage that is itself unknown it means nobody could tell, and the cell carries that reason
  /// rather than pretending the process is idle (PRD §72.3).
  /// </remarks>
  private static string GpuEngineName(SnapshotDelta? delta, int index) {
    if (delta is null)
      return Humanize.Placeholder(UnknownReason.NotSampledYet);

    var busiest = delta.GpuEnginePercent(index);
    if (!busiest.HasValue)
      return Humanize.Placeholder(busiest.Reason);

    var engine = delta.BusiestGpuEngine(index);
    return engine == GpuEngine.Unknown ? string.Empty : EngineName(engine);
  }

  private static string EngineName(GpuEngine engine) => engine switch {
    GpuEngine.Graphics => "3D",
    GpuEngine.Compute => "compute",
    GpuEngine.Copy => "copy",
    GpuEngine.Encode => "encode",
    GpuEngine.Decode => "decode",
    _ => string.Empty,
  };

  #endregion

  private static Rate Rated(SnapshotDelta? delta, int index, ProcessField field) {
    if (delta is null)
      return Rate.NotSampledYet;

    return field switch {
      ProcessField.CpuPercent => delta.CpuPercent(index),
      ProcessField.MemoryPercent => delta.MemoryPercent(index),
      ProcessField.CpuPercentPerCore => delta.CpuPercentPerCore(index),
      ProcessField.CpuPercentDelta => delta.CpuPercentDelta(index),
      ProcessField.CyclesDelta => delta.CyclesPerSecond(index),
      ProcessField.ContextSwitchesDelta => delta.ContextSwitchesPerSecond(index),
      ProcessField.PageFaultsDelta => delta.PageFaultsPerSecond(index),
      ProcessField.PrivateBytesDelta => delta.PrivateBytesDelta(index),
      ProcessField.IoTotalRate => delta.IoTotalBytesPerSecond(index),
      ProcessField.ReadBytesPerSecond => delta.ReadBytesPerSecond(index),
      ProcessField.WriteBytesPerSecond => delta.WriteBytesPerSecond(index),
      ProcessField.ReadOperationsDelta => delta.ReadOperationsPerSecond(index),
      ProcessField.WriteOperationsDelta => delta.WriteOperationsPerSecond(index),
      ProcessField.GpuPercent => delta.GpuPercent(index),
      ProcessField.GpuEnginePercent => delta.GpuEnginePercent(index),
      ProcessField.GpuGraphicsPercent => delta.GpuGraphicsPercent(index),
      ProcessField.GpuComputePercent => delta.GpuComputePercent(index),
      ProcessField.GpuCopyPercent => delta.GpuCopyPercent(index),
      ProcessField.GpuEncodePercent => delta.GpuEncodePercent(index),
      ProcessField.GpuDecodePercent => delta.GpuDecodePercent(index),
      ProcessField.GpuDedicatedMemoryDelta => delta.GpuDedicatedBytesDelta(index),
      _ => Rate.NotSampledYet,
    };
  }

}
