using System.Text.RegularExpressions;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What Windows says an ARM processor can do (PRD §46).
/// </summary>
/// <remarks>
/// The same problem the arm64 bit table has and the same answer: nobody working on this has a
/// Windows-on-ARM machine, so a wrong ordinal would produce a plausible feature list on a machine
/// none of us can look at. The ordinals are therefore held against a vendored copy of the
/// <c>PF_*</c> list rather than against anybody's memory.
/// </remarks>
[TestFixture]
public sealed class WindowsProcessorFeatureTests {

  private static Dictionary<uint, string> HeaderOrdinals() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "winnt-pf.h");
    var text = File.ReadAllText(path);
    var ordinals = new Dictionary<uint, string>();

    foreach (Match match in Regex.Matches(text, @"#define\s+(PF_\w+)\s+(\d+)"))
      ordinals[uint.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)] = match.Groups[1].Value;

    return ordinals;
  }

  [Test]
  public void TheVendoredListIsThereAndParses() =>
    Assert.That(HeaderOrdinals(), Has.Count.GreaterThan(80), "the vendored PF_ list did not parse");

  /// <summary>
  /// The test that earns its keep: every ordinal this program passes to
  /// <c>IsProcessorFeaturePresent</c> is checked against the name <c>winnt.h</c> gives it.
  /// </summary>
  [Test]
  public void EveryOrdinalWeAskAboutIsTheOneWindowsDefines() {
    var header = HeaderOrdinals();

    foreach (var (ordinal, name) in WindowsProcessorFeatures.KernelNames) {
      Assert.That(header.ContainsKey(ordinal), Is.True, $"PF ordinal {ordinal} ({name}) is not defined");
      Assert.That(header[ordinal], Is.EqualTo(name), $"PF ordinal {ordinal}");
    }
  }

  /// <summary>
  /// One question is asked at most once. A duplicated ordinal would put the same silicon on the page
  /// twice under two names, which reads as a processor with more in it than it has.
  /// </summary>
  [Test]
  public void NoOrdinalIsAskedAboutTwice() {
    var seen = new HashSet<uint>();

    foreach (var (ordinal, name) in WindowsProcessorFeatures.KernelNames)
      Assert.That(seen.Add(ordinal), Is.True, $"{name} is asked about twice");
  }

  private static List<string> Names(params uint[] present) {
    var set = new HashSet<uint>(present);
    var names = new List<string>();
    foreach (var feature in WindowsProcessorFeatures.Decode(set.Contains))
      names.Add(feature.Name);

    return names;
  }

  /// <summary>A Snapdragon-shaped part: ARMv8 with the crypto extension, CRC32 and the atomics.</summary>
  [Test]
  public void AWindowsOnArmShapeDecodes() {
    var names = Names(19, 29, 30, 31, 34, 43, 67);

    Assert.That(names, Does.Contain("ASIMD (NEON)"));
    Assert.That(names, Does.Contain("CRC32"));
    Assert.That(names, Does.Contain("LSE atomics"));
    Assert.That(names, Does.Contain("DOTPROD"));
    Assert.That(names, Does.Contain("FP16"));
    Assert.That(names, Does.Not.Contain("SVE"), "no Windows-on-ARM part ships it yet");
    Assert.That(names, Does.Not.Contain("SME"));
  }

  /// <summary>
  /// Windows asks one question about a group of four instructions the architecture makes one option,
  /// so the row names all four rather than claiming four readings from one bit.
  /// </summary>
  [Test]
  public void TheCryptoExtensionIsOneAnswerAndSaysSo() {
    var names = Names(30);

    Assert.That(names, Has.Count.EqualTo(1));
    Assert.That(names[0], Does.Contain("AES").And.Contains("PMULL").And.Contains("SHA1").And.Contains("SHA2"));
  }

  /// <summary>
  /// The names match the Linux table's wherever the two describe the same silicon, so a reader
  /// comparing a Windows tablet against a Linux board is comparing like with like (PRD §5.1).
  /// </summary>
  [Test]
  public void TheNamesAreTheOnesTheLinuxTableUses() {
    var linux = new HashSet<string>(StringComparer.Ordinal);
    foreach (var feature in ArmFeatures.Decode(ulong.MaxValue, ulong.MaxValue))
      linux.Add(feature.Name);

    foreach (var shared in new[] { "ASIMD (NEON)", "SVE", "SVE2", "SVE2p1", "SME", "SME2", "I8MM", "BF16", "EBF16", "DOTPROD", "JSCVT", "LRCPC", "CRC32", "SHA3", "SHA512", "SVE-AES", "SVE-PMULL", "SVE-BITPERM", "SVE-SHA3", "SVE-SM4", "SVE-I8MM", "SVE-F32MM", "SVE-F64MM", "SVE-BF16", "FP16", "LSE atomics" })
      Assert.That(linux, Does.Contain(shared), $"{shared} is spelled differently on the two tables");
  }

  [Test]
  public void AProcessorThatAnswersNoToEverythingReportsNothing() =>
    Assert.That(WindowsProcessorFeatures.Decode(_ => false), Is.Empty);

  /// <summary>
  /// And one that answers yes to everything reports every row exactly once, which is the check that
  /// the table has no entry the decoder skips.
  /// </summary>
  [Test]
  public void AProcessorThatAnswersYesToEverythingReportsEveryRow() =>
    Assert.That(WindowsProcessorFeatures.Decode(_ => true), Has.Count.EqualTo(WindowsProcessorFeatures.KernelNames.Count));

}
