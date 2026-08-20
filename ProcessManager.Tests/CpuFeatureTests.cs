using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What the processor says it can do (PRD §46).
/// </summary>
/// <remarks>
/// The decoding is a pure function of the register values, so this runs on every CI leg — including
/// the ARM one, which has no <c>CPUID</c> instruction at all. Recording a machine's registers is
/// also the only way to test a feature nobody's laptop has.
/// </remarks>
[TestFixture]
public sealed class CpuFeatureTests {

  private readonly record struct Leaf(int Number, int SubLeaf, int Eax, int Ebx, int Ecx, int Edx);

  /// <summary>
  /// An Intel Core i9-11950H, Tiger Lake-H, read off the machine this was written on.
  /// </summary>
  /// <remarks>
  /// Every flag asserted below was cross-checked against the same machine's <c>/proc/cpuinfo</c>,
  /// which is an independent decoder of the same silicon — two of the apparent disagreements turned
  /// out to be the kernel spelling things differently (<c>avx512_vbmi2</c>, and <c>user_shstk</c>
  /// for what Intel calls CET_SS).
  /// </remarks>
  private static readonly Leaf[] _TigerLake = [
    new(unchecked((int)0x00000000), 0, unchecked((int)0x0000001B), unchecked((int)0x756E6547), unchecked((int)0x6C65746E), unchecked((int)0x49656E69)),
    new(unchecked((int)0x00000001), 0, unchecked((int)0x000806D1), unchecked((int)0x06100800), unchecked((int)0x7FFAFBFF), unchecked((int)0xBFEBFBFF)),
    new(unchecked((int)0x00000007), 0, unchecked((int)0x00000002), unchecked((int)0xF3BFA7EB), unchecked((int)0x18C07FDE), unchecked((int)0xFC100510)),
    new(unchecked((int)0x00000007), 1, unchecked((int)0x00000000), unchecked((int)0x00000000), unchecked((int)0x00000000), unchecked((int)0x00040000)),
    new(unchecked((int)0x80000000), 0, unchecked((int)0x80000008), unchecked((int)0x00000000), unchecked((int)0x00000000), unchecked((int)0x00000000)),
    new(unchecked((int)0x80000001), 0, unchecked((int)0x00000000), unchecked((int)0x00000000), unchecked((int)0x00000121), unchecked((int)0x2C100800)),
    new(unchecked((int)0x80000002), 0, unchecked((int)0x68743131), unchecked((int)0x6E654720), unchecked((int)0x746E4920), unchecked((int)0x52286C65)),
    new(unchecked((int)0x80000003), 0, unchecked((int)0x6F432029), unchecked((int)0x54286572), unchecked((int)0x6920294D), unchecked((int)0x31312D39)),
    new(unchecked((int)0x80000004), 0, unchecked((int)0x48303539), unchecked((int)0x32204020), unchecked((int)0x4730362E), unchecked((int)0x00007A48)),
  ];

  /// <summary>
  /// Answers from a recorded set, and — like the silicon — returns the last values it had for a leaf
  /// nobody recorded rather than zeros.
  /// </summary>
  /// <remarks>
  /// That last part is the point of the stub. A real processor leaves stale register contents behind
  /// for an unsupported leaf, so a decoder that does not check the maximum leaf first invents
  /// features. A stub that helpfully returned zeros would hide exactly that bug.
  /// </remarks>
  private static CpuFeatures.Reader From(Leaf[] leaves) {
    var last = (0, 0, 0, 0);
    return (leaf, subLeaf) => {
      foreach (var recorded in leaves)
        if (recorded.Number == leaf && recorded.SubLeaf == subLeaf)
          return last = (recorded.Eax, recorded.Ebx, recorded.Ecx, recorded.Edx);

      return last;
    };
  }

  private static List<string> Names(Leaf[] leaves) {
    var names = new List<string>();
    foreach (var feature in CpuFeatures.Decode(From(leaves)))
      names.Add(feature.Name);

    return names;
  }

  #region a real processor

  [Test]
  public void TheVendorAndTheBrandComeOutOfTheRegisters() {
    Assert.That(CpuFeatures.Vendor(From(_TigerLake)), Is.EqualTo("GenuineIntel"));
    Assert.That(CpuFeatures.Brand(From(_TigerLake)), Is.EqualTo("11th Gen Intel(R) Core(TM) i9-11950H @ 2.60GHz"));
  }

  [Test]
  public void TheInstructionSetsThisProcessorHasAreReported() {
    var names = Names(_TigerLake);

    foreach (var expected in new[] { "SSE4.2", "AVX", "AVX2", "FMA", "AVX-512F", "AVX-512VL", "AVX-512VBMI2" })
      Assert.That(names, Does.Contain(expected), expected);
  }

  /// <summary>
  /// Tiger Lake has no AMX and no FP16 — those arrived with Sapphire Rapids. A decoder that reads
  /// the wrong bit would claim them, so the absences are asserted as carefully as the presences.
  /// </summary>
  [Test]
  public void TheInstructionSetsItDoesNotHaveAreNotReported() {
    var names = Names(_TigerLake);

    foreach (var absent in new[] { "AMX-TILE", "AMX-INT8", "AMX-BF16", "AVX-512FP16", "AVX-512BF16" })
      Assert.That(names, Does.Not.Contain(absent), absent);
  }

  [Test]
  public void TheCryptographyInstructionsAreReported() {
    var names = Names(_TigerLake);

    foreach (var expected in new[] { "AES-NI", "SHA", "PCLMULQDQ", "VAES", "GFNI", "RDRAND", "RDSEED" })
      Assert.That(names, Does.Contain(expected), expected);
  }

  [Test]
  public void TheHardeningFeaturesAreReported() {
    var names = Names(_TigerLake);

    foreach (var expected in new[] { "SMEP", "SMAP", "UMIP", "PKU", "CET-SS", "CET-IBT", "NX", "SSBD" })
      Assert.That(names, Does.Contain(expected), expected);
  }

  /// <summary>
  /// This machine is bare metal, so it supports virtualisation and is not itself virtualised. The
  /// two are different bits and are constantly confused.
  /// </summary>
  [Test]
  public void SupportingVirtualisationIsNotTheSameAsBeingVirtualised() {
    var names = Names(_TigerLake);

    Assert.That(names, Does.Contain("VMX"));
    Assert.That(names, Does.Not.Contain("hypervisor"));
    Assert.That(names, Does.Not.Contain("SVM"), "that is AMD's");
  }

  [Test]
  public void FeaturesCarryTheKindTheyAreGroupedBy() {
    var kinds = new Dictionary<string, CpuFeatureKind>(StringComparer.Ordinal);
    foreach (var feature in CpuFeatures.Decode(From(_TigerLake)))
      kinds[feature.Name] = feature.Kind;

    Assert.That(kinds["AVX2"], Is.EqualTo(CpuFeatureKind.InstructionSet));
    Assert.That(kinds["AES-NI"], Is.EqualTo(CpuFeatureKind.Cryptography));
    Assert.That(kinds["SMEP"], Is.EqualTo(CpuFeatureKind.Security));
    Assert.That(kinds["VMX"], Is.EqualTo(CpuFeatureKind.Virtualisation));
  }

  /// <summary>
  /// The encoding is the awkward part: family and model are each split across two fields, and an
  /// Intel Core reports family 6 model 13 until the extended model's 8 is shifted in to make 141.
  /// Cross-checked against the same machine's /proc/cpuinfo, which says exactly that.
  /// </summary>
  [Test]
  public void TheSignatureIsAssembledFromItsSplitFields() {
    var signature = CpuFeatures.Signature(From(_TigerLake));

    Assert.That(signature, Is.Not.Null);
    Assert.That(signature!.Value.Family, Is.EqualTo(6));
    Assert.That(signature.Value.Model, Is.EqualTo(141), "13 plus the extended model, not 13");
    Assert.That(signature.Value.Stepping, Is.EqualTo(1));
  }

  /// <summary>
  /// Family 15 is the only one where the extended family is added rather than ignored — a Pentium 4
  /// reporting family 15 and extended family 0 is family 15, and an old Opteron is family 21.
  /// </summary>
  [Test]
  public void FamilyFifteenAddsItsExtendedFamily() {
    var opteron = new[] {
      new Leaf(0, 0, 1, 0, 0, 0),
      // family 0xF, extended family 0x6, model 0x1, extended model 0x0
      new Leaf(1, 0, (0x6 << 20) | (0xF << 8) | (0x1 << 4) | 0x2, 0, 0, 0),
    };

    var signature = CpuFeatures.Signature(From(opteron));

    Assert.That(signature!.Value.Family, Is.EqualTo(21));
    Assert.That(signature.Value.Stepping, Is.EqualTo(2));
  }

  #endregion

  #region processors that answer less

  /// <summary>
  /// The bug the stub is shaped to catch: a processor whose maximum basic leaf is 1 leaves stale
  /// registers behind when leaf 7 is asked for, so a decoder that reads it anyway invents every
  /// feature that happened to be in those registers.
  /// </summary>
  [Test]
  public void ALeafBeyondTheMaximumIsNotRead() {
    var ancient = new[] {
      // Maximum basic leaf 1, and no extended leaves at all.
      new Leaf(0, 0, 1, unchecked((int)0x756E6547), unchecked((int)0x6C65746E), unchecked((int)0x49656E69)),
      new Leaf(1, 0, 0, 0, 0, 1 << 25),
    };

    var names = Names(ancient);

    Assert.That(names, Does.Contain("SSE"));
    foreach (var invented in new[] { "AVX2", "AVX-512F", "SMEP", "NX", "64-bit" })
      Assert.That(names, Does.Not.Contain(invented), invented);
  }

  /// <summary>
  /// An extended leaf is 0x80000001, which is negative as a signed int. Comparing it signed against
  /// the maximum makes every extended leaf look supported.
  /// </summary>
  [Test]
  public void AnExtendedLeafIsComparedUnsigned() {
    var noExtended = new[] {
      new Leaf(0, 0, 7, unchecked((int)0x756E6547), unchecked((int)0x6C65746E), unchecked((int)0x49656E69)),
      new Leaf(1, 0, 0, 0, 0, 0),
      new Leaf(7, 0, 0, 0, 0, 0),
      // Says it supports no extended leaves, while the registers still hold leaf 7's values.
      new Leaf(unchecked((int)0x80000000), 0, 0, 0, 0, 0),
    };

    Assert.That(Names(noExtended), Does.Not.Contain("NX"));
    Assert.That(CpuFeatures.Brand(From(noExtended)), Is.Null);
  }

  [Test]
  public void AProcessorThatAnswersNothingIsNotAnError() {
    var silent = new[] { new Leaf(0, 0, 0, 0, 0, 0) };

    Assert.That(CpuFeatures.Decode(From(silent)), Is.Empty);
    Assert.That(CpuFeatures.Vendor(From(silent)), Is.Null);
    Assert.That(CpuFeatures.Brand(From(silent)), Is.Null);
  }

  /// <summary>A guest is told it is one, and that is the single most useful bit on the page.</summary>
  [Test]
  public void AVirtualMachineIsRecognised() {
    var guest = new[] {
      new Leaf(0, 0, 1, unchecked((int)0x756E6547), unchecked((int)0x6C65746E), unchecked((int)0x49656E69)),
      new Leaf(1, 0, 0, 0, 1 << 31, 0),
    };

    Assert.That(Names(guest), Does.Contain("hypervisor"));
  }

  #endregion

  /// <summary>
  /// Whatever this machine is, asking it must not throw — and on a machine with no CPUID the answer
  /// is nothing rather than an exception.
  /// </summary>
  [Test]
  public void AskingThisMachineIsSafeWhateverItIs() {
    Assert.That(() => CpuId.Features, Throws.Nothing);
    if (!CpuId.IsSupported)
      Assert.That(CpuId.Features, Is.Empty);
  }

  /// <summary>
  /// A report built from a recorded machine must describe that machine. Reading CPUID where the rows
  /// are rendered made a fixture replay show this laptop's feature list beside a fixture's core
  /// count, which is two machines in one table (PRD §9.4).
  /// </summary>
  [Test]
  public void TheReportDescribesTheHostItWasGivenAndNotTheOneItRunsOn() {
    var snapshot = new Model.SystemSnapshot();
    snapshot.PrepareProcesses(0);

    var recorded = new Model.HostInfo {
      CpuModel = "Recorded CPU",
      CpuSignature = "family 6, model 1, stepping 0",
      CpuFeatures = [new("MADE-UP-ISA", CpuFeatureKind.InstructionSet)],
    };

    foreach (var section in PerformanceReport.Build(recorded, snapshot)) {
      if (section.Title != "Processor")
        continue;

      var rows = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (var row in section.Rows)
        rows[row.Label] = row.Value;

      Assert.That(rows["Model"], Is.EqualTo("Recorded CPU"));
      Assert.That(rows["Signature"], Is.EqualTo("family 6, model 1, stepping 0"));
      Assert.That(rows["Instruction sets"], Is.EqualTo("MADE-UP-ISA"));
      return;
    }

    Assert.Fail("no Processor section");
  }

  /// <summary>A machine with nothing to say about a kind gets no line, rather than an empty one.</summary>
  [Test]
  public void AKindWithNoFeaturesGetsNoLineAtAll() {
    var snapshot = new Model.SystemSnapshot();
    snapshot.PrepareProcesses(0);

    foreach (var section in PerformanceReport.Build(new(), snapshot)) {
      if (section.Title != "Processor")
        continue;

      foreach (var row in section.Rows)
        Assert.That(row.Label, Is.Not.EqualTo("Instruction sets"));

      return;
    }

    Assert.Fail("no Processor section");
  }

}
