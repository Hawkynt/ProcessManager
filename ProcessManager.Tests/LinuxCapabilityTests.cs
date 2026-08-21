using System.Globalization;
using System.Text.RegularExpressions;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a Linux capability mask grants, by name (PRD §21).
/// </summary>
/// <remarks>
/// Forty-one names transcribed by hand, every one of which is a claim about what a process is
/// allowed to do to the machine. Getting one wrong does not fail anything — it produces a plausible
/// list with <c>cap_sys_module</c> where <c>cap_sys_rawio</c> belongs, and nobody reading a column
/// can tell. So the table is held against the kernel's own header rather than against anybody's
/// memory, the same way the ARM feature bits are (PRD §9.2).
/// </remarks>
[TestFixture]
public sealed class LinuxCapabilityTests {

  /// <summary>
  /// <c>uapi/linux/capability.h</c>, taken from the kernel and kept beside the tests.
  /// </summary>
  /// <remarks>
  /// The numbers are an ABI: a capability cannot be renumbered without breaking every file on every
  /// disk that carries a capability set, so a vendored copy cannot go stale in the way a vendored
  /// implementation would. It can only gain entries, which is why the test checks that every bit we
  /// name is the bit the kernel names rather than that every bit the kernel has is named.
  /// </remarks>
  private static Dictionary<int, string> KernelBits() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "linux-capability.h");
    var text = File.ReadAllText(path);
    var bits = new Dictionary<int, string>();

    foreach (Match match in Regex.Matches(text, @"#define\s+CAP_([A-Z0-9_]+)\s+(\d+)\s*$", RegexOptions.Multiline))
      bits[int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)] = "cap_" + match.Groups[1].Value.ToLowerInvariant();

    return bits;
  }

  [Test]
  public void TheHeaderIsThereAndParses() =>
    Assert.That(KernelBits(), Has.Count.GreaterThan(35), "the vendored capability.h did not parse");

  /// <summary>
  /// The test that earns its keep: every name this program prints is the kernel's own name for that
  /// bit, in the kernel's own order.
  /// </summary>
  [Test]
  public void EveryNameWeUseIsTheNameTheKernelDefines() {
    var kernel = KernelBits();
    var ours = LinuxCapabilities.KernelNames;

    for (var bit = 0; bit < ours.Count; ++bit) {
      Assert.That(kernel.ContainsKey(bit), Is.True, $"bit {bit} ({ours[bit]}) is not defined by the kernel");
      Assert.That(kernel[bit], Is.EqualTo(ours[bit]), $"capability bit {bit}");
    }
  }

  /// <summary>
  /// The header defines <c>CAP_LAST_CAP</c> as an alias for whichever capability is newest, and the
  /// table must reach it — a table that stops one short silently under-reports the privilege the
  /// kernel added most recently, which is the one nobody has a habit of checking.
  /// </summary>
  [Test]
  public void TheTableReachesTheKernelsLastCapability() {
    var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "linux-capability.h");
    var text = File.ReadAllText(path);
    var last = Regex.Match(text, @"#define\s+CAP_LAST_CAP\s+CAP_([A-Z0-9_]+)");

    Assert.That(last.Success, Is.True, "the header does not define CAP_LAST_CAP");
    Assert.That(
      LinuxCapabilities.Name(LinuxCapabilities.HighestNamedBit),
      Is.EqualTo("cap_" + last.Groups[1].Value.ToLowerInvariant())
    );
  }

  #region decoding

  /// <summary>
  /// The masks a real machine writes. <c>000001ffffffffff</c> is what <c>/proc/1/status</c> says on
  /// any ordinary system, and <c>capsh --decode</c> answers with all forty-one names.
  /// </summary>
  [Test]
  public void TheFullSetIsEveryCapabilityTheKernelHas() {
    Assert.That(LinuxCapabilities.FullSet, Is.EqualTo(0x000001ffffffffffUL));
    Assert.That(LinuxCapabilities.Decode(LinuxCapabilities.FullSet), Has.Count.EqualTo(41));
    Assert.That(LinuxCapabilities.Describe(LinuxCapabilities.FullSet), Is.EqualTo("all"));
  }

  [Test]
  public void AnEmptyMaskIsAnAnswerRatherThanNothing() {
    Assert.That(LinuxCapabilities.Decode(0), Is.Empty);
    Assert.That(LinuxCapabilities.Describe(0), Is.EqualTo("none"));
  }

  /// <summary>
  /// Two bits in the middle of the word, checked against <c>capsh --decode=0x0000000000003000</c>,
  /// which answers <c>cap_net_admin,cap_net_raw</c>.
  /// </summary>
  [Test]
  public void APartialMaskDecodesToTheKernelToolsAnswer() =>
    Assert.That(LinuxCapabilities.Describe(0x3000), Is.EqualTo("cap_net_admin,cap_net_raw"));

  /// <summary>
  /// The bit a shell inherits on a systemd desktop: <c>0x0000000800000000</c>, which
  /// <c>capsh --decode</c> reads as <c>cap_wake_alarm</c> and an off-by-one would read as
  /// <c>cap_syslog</c> or <c>cap_block_suspend</c> — both entirely plausible.
  /// </summary>
  [Test]
  public void TheBitsEitherSideOfTheMiddleAreNotShifted() {
    Assert.That(LinuxCapabilities.Describe(1ul << 34), Is.EqualTo("cap_syslog"));
    Assert.That(LinuxCapabilities.Describe(1ul << 35), Is.EqualTo("cap_wake_alarm"));
    Assert.That(LinuxCapabilities.Describe(1ul << 36), Is.EqualTo("cap_block_suspend"));
  }

  /// <summary>Bit 0 and the last bit — the two an off-by-one at either end would lose.</summary>
  [Test]
  public void TheFirstAndLastBitsAreBothRead() {
    Assert.That(LinuxCapabilities.Describe(1), Is.EqualTo("cap_chown"));
    Assert.That(LinuxCapabilities.Describe(1ul << 40), Is.EqualTo("cap_checkpoint_restore"));
  }

  /// <summary>
  /// A kernel newer than this build. <c>capsh</c> prints an unnamed bit as its number and so does
  /// this: dropping it would quietly report less privilege than the process holds, which is the one
  /// direction a security field must never round.
  /// </summary>
  [Test]
  public void ABitNoKernelHadYetIsReportedAsItsNumber() {
    Assert.That(LinuxCapabilities.Name(41), Is.Null);
    Assert.That(LinuxCapabilities.Describe(0x0000060000000000), Is.EqualTo("41,42"));
    Assert.That(LinuxCapabilities.Describe((1ul << 40) | (1ul << 41)), Is.EqualTo("cap_checkpoint_restore,41"));
  }

  /// <summary>
  /// A process holding everything but one bit is listed rather than called "all" — that missing
  /// capability is the whole story of a hardened service, and rounding it up would erase it.
  /// </summary>
  [Test]
  public void AlmostEverythingIsNotAll() {
    var withoutModuleLoading = LinuxCapabilities.FullSet & ~(1ul << 16);
    var text = LinuxCapabilities.Describe(withoutModuleLoading);

    Assert.That(text, Is.Not.EqualTo("all"));
    Assert.That(text, Does.Not.Contain("cap_sys_module"));
    Assert.That(text, Does.Contain("cap_sys_rawio"));
  }

  /// <summary>Sixteen digits and a prefix, so the value pastes straight into <c>capsh --decode</c>.</summary>
  [Test]
  public void TheRawFormIsTheOneTheKernelToolsAccept() {
    Assert.That(LinuxCapabilities.Hex(0), Is.EqualTo("0x0000000000000000"));
    Assert.That(LinuxCapabilities.Hex(LinuxCapabilities.FullSet), Is.EqualTo("0x000001ffffffffff"));
  }

  #endregion

}
