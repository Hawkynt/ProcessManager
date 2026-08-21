using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Windows security columns of PRD §21 and the identity columns of §14 that come off the same
/// handle, held against the bit positions and the constants their structures actually define.
/// </summary>
/// <remarks>
/// <para>
/// Runs on every OS, and that is the point. The half of this work that calls Windows cannot be
/// exercised from here at all; the half that decides what a flags word <em>means</em> is portable
/// arithmetic, and it is where the mistakes that would survive a demo live — a bit off by one reads
/// as a mitigation that is off, which looks exactly like a mitigation that is off (PRD §9.4).
/// </para>
/// <para>
/// Every number below is transcribed from the structure Microsoft publishes for it, and the ones
/// that are not — the protection levels, which appear on no reference page — are marked as such
/// where they are defined.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WindowsSecurityTests {

  private static string Text(ProcessField wanted, ProcessRecord record)
    => FieldAccessor.Text(wanted, in record, null, 0);

  private static string? Raw(ProcessField wanted, ProcessRecord record)
    => FieldAccessor.RawText(wanted, in record);

  #region the mitigation policies

  /// <summary>
  /// A policy nobody could read must never render as a policy that is switched off.
  /// </summary>
  /// <remarks>
  /// The single most dangerous cell in this section. <c>GetProcessMitigationPolicy</c> needs
  /// <c>PROCESS_QUERY_INFORMATION</c>, which fails for most of another user's table, and a column
  /// that answered "off" there would report the machine as unhardened on the strength of not having
  /// been allowed to look (PRD §72.3).
  /// </remarks>
  [Test]
  public void APolicyNobodyCouldReadIsNotAPolicyThatIsOff() {
    var refused = new ProcessRecord {
      DepPolicy = Counter.NotPermitted,
      AslrPolicy = Counter.NotPermitted,
      ControlFlowGuardPolicy = Counter.NotPermitted,
      ShadowStackPolicy = Counter.NotPermitted,
      DynamicCodePolicy = Counter.NotPermitted,
      BinarySignaturePolicy = Counter.NotPermitted,
    };

    var placeholder = Humanize.Placeholder(UnknownReason.NotPermitted);
    foreach (var wanted in new[] {
      ProcessField.DataExecutionPrevention, ProcessField.AddressSpaceRandomisation,
      ProcessField.ControlFlowGuard, ProcessField.ShadowStackPolicy,
      ProcessField.ArbitraryCodeGuard, ProcessField.CodeIntegrityGuard,
    }) {
      Assert.That(Text(wanted, refused), Is.EqualTo(placeholder), wanted.ToString());
      Assert.That(Raw(wanted, refused), Is.Null, wanted.ToString());
      Assert.That(FieldAccessor.Number(wanted, in refused, null, 0), Is.Null, wanted.ToString());
    }

    // …while a policy that was read and had nothing set says so as a word, and exports one.
    var read = new ProcessRecord { AslrPolicy = Counter.Of(0ul) };
    Assert.That(Text(ProcessField.AddressSpaceRandomisation, read), Is.EqualTo("off"));
    Assert.That(Raw(ProcessField.AddressSpaceRandomisation, read), Is.EqualTo("off"));
  }

  /// <summary>
  /// <c>PROCESS_MITIGATION_DEP_POLICY</c>: bit 0 <c>Enable</c>, and a <c>BOOLEAN Permanent</c> that
  /// lives outside the flags word and is carried in bit 32.
  /// </summary>
  [TestCase(0ul, "off")]
  [TestCase(1ul, "on")]
  [TestCase(1ul | (1ul << 32), "on (permanent)")]
  // Permanent without Enable is not a state Windows produces, and if it ever appeared it would still
  // not be DEP being on — so the enable bit is what decides, not the flag beside it.
  [TestCase(1ul << 32, "off")]
  public void TheDepPolicyReadsItsEnableBitAndThePermanentFlagBesideIt(ulong flags, string expected)
    => Assert.That(Text(ProcessField.DataExecutionPrevention, new() { DepPolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  /// <summary>
  /// <c>PROCESS_MITIGATION_ASLR_POLICY</c>: bottom-up, force relocate, high entropy, no stripped
  /// images, in that order from bit 0.
  /// </summary>
  [TestCase(0ul, "off")]
  [TestCase(0b0001ul, "bottom-up")]
  [TestCase(0b0010ul, "force relocate")]
  [TestCase(0b0100ul, "high entropy")]
  [TestCase(0b1000ul, "no stripped images")]
  [TestCase(0b0101ul, "bottom-up, high entropy")]
  [TestCase(0b1111ul, "bottom-up, force relocate, high entropy, no stripped images")]
  public void TheAslrPolicyNamesEveryPartThatWasAskedFor(ulong flags, string expected)
    => Assert.That(Text(ProcessField.AddressSpaceRandomisation, new() { AslrPolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  /// <summary>
  /// <c>PROCESS_MITIGATION_CONTROL_FLOW_GUARD_POLICY</c>: enable, export suppression, strict mode,
  /// XFG, XFG audit.
  /// </summary>
  /// <remarks>
  /// Everything after the first bit is a qualifier on it: export suppression without CFG enabled is
  /// not a weaker CFG, it is no CFG, so the first bit is what decides whether the cell says anything
  /// at all.
  /// </remarks>
  [TestCase(0ul, "off")]
  [TestCase(0b00010ul, "off", Description = "export suppression without CFG is not CFG")]
  [TestCase(0b00001ul, "on")]
  [TestCase(0b00101ul, "on, strict")]
  [TestCase(0b00011ul, "on, export suppression")]
  [TestCase(0b01001ul, "on, XFG")]
  [TestCase(0b10001ul, "on, XFG audit")]
  public void TheControlFlowGuardPolicyQualifiesItsEnableBit(ulong flags, string expected)
    => Assert.That(Text(ProcessField.ControlFlowGuard, new() { ControlFlowGuardPolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  /// <summary>
  /// <c>PROCESS_MITIGATION_USER_SHADOW_STACK_POLICY</c>: ten named bits, of which the ones worth a
  /// column are enable, audit, strict mode, IP validation and blocking non-CET binaries.
  /// </summary>
  /// <remarks>
  /// Strict mode is an upgrade of the enable bit and replaces its word rather than being listed
  /// beside it: "on, strict" would read as two policies where there is one at two strengths.
  /// </remarks>
  [TestCase(0ul, "off")]
  [TestCase(1ul << 0, "on")]
  [TestCase((1ul << 0) | (1ul << 4), "strict")]
  [TestCase((1ul << 0) | (1ul << 1), "on, audit")]
  [TestCase((1ul << 0) | (1ul << 2), "on, IP validation")]
  [TestCase((1ul << 0) | (1ul << 5), "on, non-CET blocked")]
  public void TheShadowStackPolicyUpgradesRatherThanAccumulates(ulong flags, string expected)
    => Assert.That(Text(ProcessField.ShadowStackPolicy, new() { ShadowStackPolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  /// <summary>
  /// <c>PROCESS_MITIGATION_DYNAMIC_CODE_POLICY</c>: prohibit, thread opt-out, remote downgrade,
  /// audit.
  /// </summary>
  /// <remarks>
  /// Audit is its own state and not a weaker "on". Under it the process is not stopped from
  /// generating code at all, only watched doing it, and reporting that as "on" would claim a
  /// protection is in force while nothing is being prevented (PRD §5.3).
  /// </remarks>
  [TestCase(0ul, "off")]
  [TestCase(0b0001ul, "on")]
  [TestCase(0b1000ul, "audit")]
  [TestCase(0b0011ul, "on, thread opt-out")]
  [TestCase(0b0101ul, "on, remote downgrade")]
  public void TheDynamicCodePolicyKeepsAuditApartFromEnforcement(ulong flags, string expected)
    => Assert.That(Text(ProcessField.ArbitraryCodeGuard, new() { DynamicCodePolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  /// <summary>
  /// <c>PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY</c>: Microsoft only, store only, the opt-in that
  /// admits Microsoft and the store and the hardware labs, and the two audit bits.
  /// </summary>
  [TestCase(0ul, "off")]
  [TestCase(0b00001ul, "Microsoft")]
  [TestCase(0b00010ul, "store")]
  // MitigationOptIn admits a wider set than MicrosoftSignedOnly rather than a different one, so it
  // is named for what it admits and the narrower word is not repeated beside it.
  [TestCase(0b00101ul, "Microsoft/store/WHQL")]
  [TestCase(0b01001ul, "Microsoft, audit")]
  [TestCase(0b10010ul, "store, audit")]
  public void TheBinarySignaturePolicyNamesWhatItAdmits(ulong flags, string expected)
    => Assert.That(Text(ProcessField.CodeIntegrityGuard, new() { BinarySignaturePolicy = Counter.Of(flags) }), Is.EqualTo(expected));

  #endregion

  #region protection, AppContainer, emulation and the subsystem

  /// <summary>
  /// <c>PROTECTION_LEVEL_NONE</c> is <c>0xFFFFFFFE</c>, and nought is a real and high level.
  /// </summary>
  /// <remarks>
  /// Both halves of this are traps that would be invisible on a screen. Treating the sentinel as
  /// <c>-1</c> reports every unprotected process as protected at some unknown level; treating nought
  /// as "not protected" reports the WinTCB-light processes — which are the ones a reader is looking
  /// for — as ordinary. The numbers are the ones <c>winbase.h</c> defines and appear on no reference
  /// page, which is why they are pinned here.
  /// </remarks>
  [TestCase(0ul, "yes", "WinTCB (light)")]
  [TestCase(1ul, "yes", "Windows")]
  [TestCase(2ul, "yes", "Windows (light)")]
  [TestCase(3ul, "yes", "antimalware (light)")]
  [TestCase(4ul, "yes", "LSA (light)")]
  [TestCase(5ul, "yes", "WinTCB")]
  [TestCase(6ul, "yes", "codegen (light)")]
  [TestCase(7ul, "yes", "Authenticode")]
  [TestCase(8ul, "yes", "PPL app")]
  [TestCase(0xFFFF_FFFEul, "no", "none")]
  public void TheProtectionLevelIsNamedAndItsSentinelIsNotMinusOne(ulong level, string expectedProtected, string expectedName) {
    var record = new ProcessRecord { ProtectionLevel = Counter.Of(level) };
    Assert.That(Text(ProcessField.Protected, record), Is.EqualTo(expectedProtected));
    Assert.That(Text(ProcessField.ProtectionLevel, record), Is.EqualTo(expectedName));
    Assert.That(Raw(ProcessField.Protected, record), Is.EqualTo(expectedProtected));
  }

  /// <summary>A level this build does not know shows as its number rather than as the nearest word.</summary>
  [Test]
  public void AProtectionLevelThisBuildDoesNotKnowKeepsItsNumber() {
    var record = new ProcessRecord { ProtectionLevel = Counter.Of(9ul) };
    Assert.That(Text(ProcessField.ProtectionLevel, record), Is.EqualTo("0x9"));
    // Still protected: it is not the "none" sentinel, and that is the whole of what the first
    // column claims.
    Assert.That(Text(ProcessField.Protected, record), Is.EqualTo("yes"));
  }

  [Test]
  public void AProcessNobodyCouldOpenIsNeitherProtectedNorUnprotected() {
    var record = new ProcessRecord { ProtectionLevel = Counter.NotPermitted };
    var placeholder = Humanize.Placeholder(UnknownReason.NotPermitted);
    Assert.That(Text(ProcessField.Protected, record), Is.EqualTo(placeholder));
    Assert.That(Text(ProcessField.ProtectionLevel, record), Is.EqualTo(placeholder));
    Assert.That(FieldAccessor.Number(ProcessField.Protected, in record, null, 0), Is.Null);
  }

  [Test]
  public void AppContainerReadsAsAWordAndFiltersAsOne() {
    Assert.That(Text(ProcessField.AppContainer, new() { IsAppContainer = Counter.Of(1ul) }), Is.EqualTo("yes"));
    Assert.That(Text(ProcessField.AppContainer, new() { IsAppContainer = Counter.Of(0ul) }), Is.EqualTo("no"));
    Assert.That(Raw(ProcessField.AppContainer, new() { IsAppContainer = Counter.Of(1ul) }), Is.EqualTo("yes"));
  }

  /// <summary>
  /// <c>IsWow64Process2</c> reports <c>IMAGE_FILE_MACHINE_UNKNOWN</c> — nought — for a process that
  /// is not being translated, so nought is an answer here and not an empty cell.
  /// </summary>
  [TestCase(0x0000ul, "native")]
  [TestCase(0x014Cul, "x86")]
  [TestCase(0x8664ul, "x64")]
  [TestCase(0xAA64ul, "ARM64")]
  [TestCase(0xA641ul, "ARM64EC")]
  [TestCase(0x01C4ul, "ARM Thumb-2")]
  [TestCase(0x5064ul, "0x5064", Description = "a machine this build does not name keeps its number")]
  public void TheEmulatedMachineIsNamedAndNoughtMeansNative(ulong machine, string expected) {
    var record = new ProcessRecord { Emulation = Counter.Of(machine) };
    Assert.That(Text(ProcessField.Emulation, record), Is.EqualTo(expected));
    Assert.That(Raw(ProcessField.Emulation, record), Is.EqualTo(expected));
  }

  /// <summary>
  /// A Windows too old to have <c>IsWow64Process2</c> is a fact about the machine, and must not read
  /// as every process running natively.
  /// </summary>
  [Test]
  public void AWindowsWithoutTheCallSaysSoRatherThanSayingNative() {
    var record = new ProcessRecord { Emulation = Counter.NotSupported };
    Assert.That(Text(ProcessField.Emulation, record), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform)));
    Assert.That(Text(ProcessField.Emulation, record), Is.Not.EqualTo("native"));
  }

  /// <summary>The <c>IMAGE_SUBSYSTEM_*</c> values of the PE specification, by name.</summary>
  [TestCase(1ul, "native")]
  [TestCase(2ul, "GUI")]
  [TestCase(3ul, "console")]
  [TestCase(7ul, "POSIX")]
  [TestCase(9ul, "Windows CE")]
  [TestCase(10ul, "EFI application")]
  [TestCase(16ul, "boot application")]
  [TestCase(0x11ul, "0x11", Description = "one this build does not know keeps its number")]
  public void TheSubsystemIsNamedFromTheImagesOwnHeader(ulong subsystem, string expected) {
    var record = new ProcessRecord { Subsystem = Counter.Of(subsystem) };
    Assert.That(Text(ProcessField.Subsystem, record), Is.EqualTo(expected));
    Assert.That(Raw(ProcessField.Subsystem, record), Is.EqualTo(expected));
  }

  /// <summary>
  /// A file that is not a PE image has no subsystem, which is not the unknown subsystem.
  /// </summary>
  /// <remarks>
  /// <c>IMAGE_SUBSYSTEM_UNKNOWN</c> is a value a PE header can actually carry, so it has to keep its
  /// own word — and a process whose image is not a PE at all must therefore not be given it
  /// (PRD §72.3).
  /// </remarks>
  [Test]
  public void AnImageWithNoSubsystemIsNotTheUnknownSubsystem() {
    Assert.That(Text(ProcessField.Subsystem, new() { Subsystem = Counter.Of(0ul) }), Is.EqualTo("unknown"));
    Assert.That(
      Text(ProcessField.Subsystem, new() { Subsystem = Counter.NotSupported }),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  #endregion

  #region the version resource on a row

  /// <summary>
  /// A program that ships no version resource and a process nobody could look at are different
  /// cells.
  /// </summary>
  /// <remarks>
  /// The first is a finding about a great many programs — plenty of perfectly ordinary executables
  /// carry no version resource — and the second is a gap. Collapsing them would report the whole of
  /// another user's table as unversioned software (PRD §72.3).
  /// </remarks>
  [Test]
  public void NoVersionResourceAndNoPermissionAreDifferentCells() {
    var shipped = new ProcessRecord { ImageVersionReason = UnknownReason.None };
    var refused = new ProcessRecord { ImageVersionReason = UnknownReason.NotPermitted };
    var unasked = new ProcessRecord { ImageVersionReason = UnknownReason.NotSampledYet };

    foreach (var wanted in new[] {
      ProcessField.ImageDescription, ProcessField.ImageCompany, ProcessField.ImageProduct,
      ProcessField.ImageProductVersion, ProcessField.ImageFileVersion,
    }) {
      Assert.That(Text(wanted, shipped), Is.Empty, $"{wanted}: an image with no resource shows nothing");
      Assert.That(Text(wanted, refused), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotPermitted)), wanted.ToString());
      Assert.That(Text(wanted, unasked), Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)), wanted.ToString());
    }
  }

  [Test]
  public void TheVersionStringsAreShownAndExportedAsTheyWereWritten() {
    var record = new ProcessRecord {
      ImageDescription = "Windows Explorer",
      ImageCompany = "Microsoft Corporation",
      ImageProduct = "Microsoft® Windows® Operating System",
      // The form that is a string and not a number, which is why these are text fields.
      ImageProductVersion = "10.0.19041.1 (WinBuild.160101.0800)",
      ImageFileVersion = "10.0.19041.1",
    };

    Assert.That(Text(ProcessField.ImageDescription, record), Is.EqualTo("Windows Explorer"));
    Assert.That(Raw(ProcessField.ImageCompany, record), Is.EqualTo("Microsoft Corporation"));
    Assert.That(Raw(ProcessField.ImageProductVersion, record), Is.EqualTo("10.0.19041.1 (WinBuild.160101.0800)"));
    Assert.That(Raw(ProcessField.ImageFileVersion, record), Is.EqualTo("10.0.19041.1"));
  }

  #endregion

  /// <summary>
  /// None of these is a Linux idea, and the Linux probe must therefore leave none of them at the
  /// confident zero a default counter is.
  /// </summary>
  /// <remarks>
  /// The failure this guards against has already happened once in this program:
  /// <c>default(SystemCounters)</c> reported machines as having no free memory at all. Here it would
  /// report every process on a Linux box as running with no mitigations, no protection and no
  /// sandbox — which reads as a finding rather than as an absence.
  /// </remarks>
  [Test]
  public void OnLinuxNoneOfTheseIsAConfidentZero() {
    var fixtures = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new Platform.Linux.LinuxProbe(new() {
      ProcRoot = fixtures,
      PasswdPath = Path.Combine(fixtures, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);
    Assert.That(snapshot.ProcessCount, Is.GreaterThan(0));

    foreach (var process in snapshot.Processes) {
      foreach (var counter in new[] {
        process.ProtectionLevel, process.IsAppContainer, process.Emulation, process.Subsystem,
        process.DepPolicy, process.AslrPolicy, process.ControlFlowGuardPolicy,
        process.ShadowStackPolicy, process.DynamicCodePolicy, process.BinarySignaturePolicy,
        process.EventObjectCount, process.SemaphoreObjectCount, process.MutexObjectCount,
        process.SectionObjectCount, process.RegistryKeyCount,
        process.UserObjectCount, process.GdiObjectCount,
      })
        Assert.That(counter.HasValue, Is.False, "a Windows-only reading must not carry a Linux value");

      Assert.That(process.ImageDescription, Is.Null);
      Assert.That(process.ImageCompany, Is.Null);
      break;
    }
  }

}
