using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Windows mandatory integrity level (PRD §21), read out of a mandatory-label SID.
/// </summary>
/// <remarks>
/// Runs on every OS: the layout is documented and fixed, so the arithmetic is exercised against
/// hand-built structures rather than against a machine that happens to have tokens — the same reason
/// the bulk-query parser takes a span (PRD §9.4).
/// </remarks>
[TestFixture]
public sealed class WindowsTokenTests {

  /// <summary>
  /// A SID: revision, sub-authority count, six identifier-authority bytes, then that many
  /// little-endian 32-bit sub-authorities.
  /// </summary>
  private static byte[] Sid(byte identifierAuthority, params uint[] subAuthorities) {
    var bytes = new byte[8 + (subAuthorities.Length * 4)];
    bytes[0] = 1;
    bytes[1] = (byte)subAuthorities.Length;
    bytes[7] = identifierAuthority;
    for (var i = 0; i < subAuthorities.Length; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i * 4)), subAuthorities[i]);

    return bytes;
  }

  [TestCase(0x0000u, "untrusted")]
  [TestCase(0x1000u, "low")]
  [TestCase(0x2000u, "medium")]
  [TestCase(0x2100u, "medium+")]
  [TestCase(0x3000u, "high")]
  [TestCase(0x4000u, "system")]
  public void AWellKnownLevelIsReadAndNamed(uint level, string expected) {
    // S-1-16-<level>: identifier authority 16 is the mandatory label authority.
    var counter = TokenSid.IntegrityFromSid(Sid(16, level));
    Assert.That(counter.Value, Is.EqualTo((ulong)level));

    var record = new ProcessRecord { IntegrityLevel = counter };
    Assert.That(FieldAccessor.Text(ProcessField.Integrity, in record, null, 0), Is.EqualTo(expected));
  }

  /// <summary>
  /// A level Microsoft adds later must show as its number rather than being flattened into the
  /// nearest name we happen to know: "0x2800" is true and "medium" would not be.
  /// </summary>
  [Test]
  public void AnUnrecognisedLevelKeepsItsNumber() {
    var record = new ProcessRecord { IntegrityLevel = TokenSid.IntegrityFromSid(Sid(16, 0x2800)) };
    Assert.That(FieldAccessor.Text(ProcessField.Integrity, in record, null, 0), Is.EqualTo("0x2800"));
  }

  /// <summary>
  /// The level is the <em>last</em> sub-authority, so its position depends on the count byte.
  /// Reading a fixed offset gets the right answer for the one-sub-authority case and the wrong one
  /// for every other, which is exactly the shape of bug that survives a demo.
  /// </summary>
  [Test]
  public void TheLevelIsTheLastSubAuthorityWhateverTheirNumber() {
    Assert.That(TokenSid.IntegrityFromSid(Sid(16, 0x3000)).Value, Is.EqualTo(0x3000ul));
    Assert.That(TokenSid.IntegrityFromSid(Sid(16, 1, 0x3000)).Value, Is.EqualTo(0x3000ul));
    Assert.That(TokenSid.IntegrityFromSid(Sid(16, 1, 2, 3, 0x3000)).Value, Is.EqualTo(0x3000ul));
  }

  [Test]
  public void ATruncatedOrEmptySidIsRefusedRatherThanRead() {
    // Each of these would read past the end, or read a length that is not there at all.
    Assert.That(TokenSid.IntegrityFromSid([]).HasValue, Is.False);
    Assert.That(TokenSid.IntegrityFromSid([1, 1, 0, 0]).HasValue, Is.False, "shorter than a header");
    Assert.That(TokenSid.IntegrityFromSid(Sid(16)).HasValue, Is.False, "no sub-authorities");

    // A count byte claiming more sub-authorities than the buffer holds — the hostile case.
    var lying = Sid(16, 0x3000);
    lying[1] = 5;
    var counter = TokenSid.IntegrityFromSid(lying);
    Assert.That(counter.HasValue, Is.False);
    Assert.That(counter.Reason, Is.EqualTo(UnknownReason.CounterInvalid));
  }

  [Test]
  public void ElevationRendersAsAWordAndFiltersAsOne() {
    var elevated = new ProcessRecord { IsElevated = Counter.Of(1ul) };
    var not = new ProcessRecord { IsElevated = Counter.Of(0ul) };

    Assert.That(FieldAccessor.Text(ProcessField.Elevated, in elevated, null, 0), Is.EqualTo("yes"));
    Assert.That(FieldAccessor.Text(ProcessField.Elevated, in not, null, 0), Is.EqualTo("no"));
  }

  /// <summary>
  /// Integrity is a Windows concept; Linux confines processes with capabilities and LSMs instead.
  /// The Linux probe says so explicitly rather than leaving the counter at its default, which is a
  /// confident zero and would read as "untrusted" (PRD §72.3).
  /// </summary>
  [Test]
  public void OnLinuxIntegrityIsNotSupportedRatherThanUntrusted() {
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

    foreach (var process in snapshot.Processes) {
      Assert.That(process.IntegrityLevel.HasValue, Is.False);
      Assert.That(process.IntegrityLevel.Reason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
      Assert.That(
        FieldAccessor.Text(ProcessField.Integrity, in process, null, 0),
        Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
      );

      break;
    }
  }

}
