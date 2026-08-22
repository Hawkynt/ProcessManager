using System.Text.RegularExpressions;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What an ARM processor can do (PRD §46).
/// </summary>
/// <remarks>
/// Nobody here has ARM hardware, which is exactly why these matter: a wrong bit index produces a
/// plausible feature list on a machine none of us can look at. The table is therefore held against
/// the kernel's own header rather than against anybody's memory — and doing that caught two bits
/// that were wrong on the first pass.
/// </remarks>
[TestFixture]
public sealed class ArmFeatureTests {

  /// <summary>
  /// <c>arch/arm64/include/uapi/asm/hwcap.h</c>, taken from the kernel and kept beside the tests.
  /// </summary>
  /// <remarks>
  /// These assignments are an ABI between the kernel and userspace and never change once made, so a
  /// vendored copy cannot go stale in the way a vendored implementation would. It can only gain
  /// entries, which is why the test checks that every bit we claim is in the header rather than that
  /// every bit in the header is claimed.
  /// </remarks>
  private static Dictionary<(int Word, int Index), string> KernelBits() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "arm64-hwcap.h");
    var text = File.ReadAllText(path);
    var bits = new Dictionary<(int, int), string>();

    foreach (Match match in Regex.Matches(text, @"#define\s+(HWCAP2?)_(\w+)\s+\(?1(?:UL)?\s*<<\s*(\d+)\)?")) {
      var word = match.Groups[1].Value == "HWCAP" ? 1 : 2;
      bits[(word, int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture))] = match.Groups[2].Value;
    }

    return bits;
  }

  [Test]
  public void TheHeaderIsThereAndParses() =>
    Assert.That(KernelBits(), Has.Count.GreaterThan(60), "the vendored hwcap.h did not parse");

  /// <summary>
  /// The test that earns its keep. Every bit this program reads is checked against the kernel's own
  /// definition of it — <c>MTE3</c> and <c>SME</c> were both transcribed wrongly from memory, and
  /// nothing short of ARM hardware or this would have found them.
  /// </summary>
  [Test]
  public void EveryBitWeReadIsTheBitTheKernelDefines() {
    var kernel = KernelBits();

    foreach (var (word, index, name) in ArmFeatures.KernelNames) {
      Assert.That(kernel.ContainsKey((word, index)), Is.True, $"HWCAP{(word == 2 ? "2" : string.Empty)} bit {index} ({name}) is not defined by the kernel");
      Assert.That(kernel[(word, index)], Is.EqualTo(name), $"HWCAP{(word == 2 ? "2" : string.Empty)} bit {index}");
    }
  }

  #region decoding

  private static List<string> Names(ulong hwcap, ulong hwcap2) {
    var names = new List<string>();
    foreach (var feature in ArmFeatures.Decode(hwcap, hwcap2))
      names.Add(feature.Name);

    return names;
  }

  /// <summary>Bit 0 and bit 1 — the two every arm64 part has, and the floor of the ABI.</summary>
  [Test]
  public void TheBaselineIsFloatingPointAndNeon() {
    var names = Names(0b11, 0);

    Assert.That(names, Does.Contain("FP"));
    Assert.That(names, Does.Contain("ASIMD (NEON)"), "the same thing everybody still calls NEON");
    Assert.That(names, Has.Count.EqualTo(2));
  }

  /// <summary>
  /// An Apple M1 under Linux: the crypto extensions, the atomics, half precision and the
  /// JavaScript conversion instruction, and no SVE at all.
  /// </summary>
  [Test]
  public void AnAppleSiliconShapeDecodes() {
    // FP, ASIMD, AES, PMULL, SHA1, SHA2, CRC32, ATOMICS, FPHP, ASIMDHP, CPUID, ASIMDRDM, JSCVT,
    // FCMA, LRCPC, DCPOP, SHA3, ASIMDDP, SHA512, ASIMDFHM, DIT, USCAT, ILRCPC, FLAGM, SB, PACA, PACG
    var hwcap = 0ul;
    foreach (var bit in new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 20, 21, 23, 24, 25, 26, 27, 29, 30, 31 })
      hwcap |= 1ul << bit;

    var names = Names(hwcap, 0);

    Assert.That(names, Does.Contain("AES"));
    Assert.That(names, Does.Contain("SHA512"));
    Assert.That(names, Does.Contain("LSE atomics"));
    Assert.That(names, Does.Contain("PACA"), "pointer authentication");
    Assert.That(names, Does.Not.Contain("SVE"), "Apple's cores have none");
    Assert.That(names, Does.Not.Contain("MTE"));
  }

  /// <summary>A Graviton-shaped part: SVE and SVE2 present, and the newer matrix extensions not.</summary>
  [Test]
  public void AServerShapeWithSveDecodes() {
    var names = Names(1ul << 22, (1ul << 1) | (1ul << 13) | (1ul << 14));

    Assert.That(names, Does.Contain("SVE"));
    Assert.That(names, Does.Contain("SVE2"));
    Assert.That(names, Does.Contain("I8MM"));
    Assert.That(names, Does.Contain("BF16"));
    Assert.That(names, Does.Not.Contain("SME"));
  }

  /// <summary>
  /// Bit 23 of HWCAP2 is SME and bit 22 is MTE3. Both were wrong the first time, and a machine with
  /// neither must report neither — a shifted table would claim one of them here.
  /// </summary>
  [Test]
  public void TheBitsThatWereWrongTheFirstTimeStayRight() {
    Assert.That(Names(0, 1ul << 23), Does.Contain("SME"));
    Assert.That(Names(0, 1ul << 22), Does.Contain("MTE3"));
    Assert.That(Names(0, 1ul << 23), Does.Not.Contain("MTE3"));
    Assert.That(Names(0, 1ul << 22), Does.Not.Contain("SME"));
  }

  /// <summary>Bit 63 is real and is the one an off-by-one on a signed shift would lose.</summary>
  [Test]
  public void TheTopBitOfTheSecondWordIsRead() =>
    Assert.That(Names(0, 1ul << 63), Does.Contain("POE"));

  [Test]
  public void AKernelThatReportsNothingYieldsNothing() =>
    Assert.That(ArmFeatures.Decode(0, 0), Is.Empty);

  #endregion

  #region 32-bit arm, whose words are a different word entirely (PRD §46)

  /// <summary>
  /// <c>arch/arm/include/uapi/asm/hwcap.h</c>, vendored beside the arm64 one for the same reason.
  /// </summary>
  private static Dictionary<(int Word, int Index), string> Kernel32Bits() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "arm-hwcap.h");
    var text = File.ReadAllText(path);
    var bits = new Dictionary<(int, int), string>();

    foreach (Match match in Regex.Matches(text, @"#define\s+(HWCAP2?)_(\w+)\s+\(?1(?:UL)?\s*<<\s*(\d+)\)?")) {
      var word = match.Groups[1].Value == "HWCAP" ? 1 : 2;
      bits[(word, int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture))] = match.Groups[2].Value;
    }

    return bits;
  }

  [Test]
  public void The32BitHeaderIsThereAndParses() =>
    Assert.That(Kernel32Bits(), Has.Count.GreaterThan(30), "the vendored arm hwcap.h did not parse");

  /// <summary>
  /// The same test the arm64 table gets, and it earns its keep the same way: nobody here has a
  /// 32-bit ARM machine either.
  /// </summary>
  [Test]
  public void Every32BitBitWeReadIsTheBitTheKernelDefines() {
    var kernel = Kernel32Bits();

    foreach (var (word, index, name) in ArmFeatures.Arm32KernelNames) {
      Assert.That(kernel.ContainsKey((word, index)), Is.True, $"HWCAP{(word == 2 ? "2" : string.Empty)} bit {index} ({name}) is not defined by the kernel");
      Assert.That(kernel[(word, index)], Is.EqualTo(name), $"HWCAP{(word == 2 ? "2" : string.Empty)} bit {index}");
    }
  }

  private static List<string> Names32(ulong hwcap, ulong hwcap2) {
    var names = new List<string>();
    foreach (var feature in ArmFeatures.DecodeArm32(hwcap, hwcap2))
      names.Add(feature.Name);

    return names;
  }

  /// <summary>
  /// The defect the two tables exist to prevent. Bit 1 is ASIMD on arm64 and half-word loads on
  /// 32-bit ARM; bit 12 is NEON on 32-bit ARM and the rounding-double-multiply instruction on arm64.
  /// Decoding one architecture's words with the other's table produces a full, plausible and
  /// entirely wrong list — there is no bit for a check to fail on.
  /// </summary>
  [Test]
  public void TheTwoArchitecturesDoNotShareTheirBits() {
    Assert.That(Names32(1ul << 12, 0), Does.Contain("NEON"));
    Assert.That(Names(1ul << 12, 0), Does.Not.Contain("NEON"));
    Assert.That(Names(1ul << 1, 0), Does.Contain("ASIMD (NEON)"));
    Assert.That(Names32(1ul << 1, 0), Does.Contain("Half-word loads"));
  }

  /// <summary>A Cortex-A72-shaped part: NEON, VFPv4, the divides, LPAE, and the crypto extension.</summary>
  [Test]
  public void AnArmv7ShapeDecodes() {
    var hwcap = 0ul;
    foreach (var bit in new[] { 0, 1, 2, 4, 6, 7, 11, 12, 13, 15, 16, 17, 18, 19, 20, 21 })
      hwcap |= 1ul << bit;

    var names = Names32(hwcap, 0b1_1111);

    Assert.That(names, Does.Contain("NEON"));
    Assert.That(names, Does.Contain("VFPv4"));
    Assert.That(names, Does.Contain("IDIVA"));
    Assert.That(names, Does.Contain("IDIVT"));
    Assert.That(names, Does.Contain("LPAE"));
    Assert.That(names, Does.Contain("AES"));
    Assert.That(names, Does.Contain("CRC32"));
    Assert.That(names, Does.Not.Contain("SSBS"), "bit 6 of the second word, and this part has none");
  }

  /// <summary>
  /// The four the 32-bit table gained late — the same instructions AArch64 has, on a word that had
  /// almost run out of room. An off-by-one at the top of the word loses I8MM and reports nothing
  /// wrong.
  /// </summary>
  [Test]
  public void TheLateAdditionsAtTheTopOfTheFirstWordAreRead() {
    Assert.That(Names32(1ul << 24, 0), Does.Contain("DOTPROD"));
    Assert.That(Names32(1ul << 25, 0), Does.Contain("ASIMD-FHM"));
    Assert.That(Names32(1ul << 26, 0), Does.Contain("BF16"));
    Assert.That(Names32(1ul << 27, 0), Does.Contain("I8MM"));
  }

  [Test]
  public void A32BitKernelThatReportsNothingYieldsNothing() =>
    Assert.That(ArmFeatures.DecodeArm32(0, 0), Is.Empty);

  #endregion

  #region who made the core

  /// <summary>
  /// The implementer codes are the initials of the companies in ASCII — 0x41 is 'A' for Arm, 0x51 is
  /// 'Q' for Qualcomm — which is a joke that outlived its author and is now an ABI.
  /// </summary>
  [Test]
  public void TheImplementerIsNamed() {
    Assert.That(ArmFeatures.Implementer(0x41_00_00_00), Is.EqualTo("Arm"));
    Assert.That(ArmFeatures.Implementer(0x61_00_00_00), Is.EqualTo("Apple"));
    Assert.That(ArmFeatures.Implementer(0x51_00_00_00), Is.EqualTo("Qualcomm"));
    Assert.That(ArmFeatures.Implementer(0xC0_00_00_00), Is.EqualTo("Ampere"));
  }

  /// <summary>
  /// A new implementer appears every few years. Naming one wrongly is worse than not naming it, so
  /// an unknown code is reported as the code.
  /// </summary>
  [Test]
  public void AnImplementerNobodyKnowsIsNotGuessedAt() {
    Assert.That(ArmFeatures.Implementer(0x99_00_00_00), Is.Null);
    Assert.That(ArmFeatures.Signature(0x99_0F_0F_00), Does.Contain("0x99"));
  }

  /// <summary>
  /// A Neoverse N1 reads 0x413fd0c1: Arm, part 0xd0c, variant 3, revision 1. The fields overlap in
  /// ways an off-by-four in a shift would hide, so the whole value is decoded at once.
  /// </summary>
  [Test]
  public void TheSignatureUnpacksEveryField() {
    var signature = ArmFeatures.Signature(0x413fd0c1);

    Assert.That(signature, Does.Contain("Arm"));
    Assert.That(signature, Does.Contain("0xd0c"));
    Assert.That(signature, Does.Contain("variant 3"));
    Assert.That(signature, Does.Contain("revision 1"));
  }

  [Test]
  public void AMachineWithNoIdentificationRegisterHasNoSignature() =>
    Assert.That(ArmFeatures.Signature(0), Is.Null);

  #endregion

}
