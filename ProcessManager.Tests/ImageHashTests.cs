using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The digests of the image a process is running (PRD §21, §70).
/// </summary>
/// <remarks>
/// The reference values are coreutils'. If this program ever disagrees with <c>sha256sum</c>, this
/// program is wrong.
/// <para>
/// <b>A hash is not a verdict.</b> Nothing here says whether an image is signed, trusted or known —
/// those are three other questions and this program never lets one stand in for another (§70).
/// </para>
/// </remarks>
[TestFixture]
public sealed class ImageHashTests {

  private string _directory = string.Empty;

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), $"procman image hash {Guid.NewGuid():N}");
    Directory.CreateDirectory(this._directory);
  }

  [TearDown]
  public void RemoveDirectory() {
    if (Directory.Exists(this._directory))
      Directory.Delete(this._directory, recursive: true);
  }

  private string Write(string name, string content) {
    var path = Path.Combine(this._directory, name);
    File.WriteAllText(path, content);
    return path;
  }

  [Test]
  public void BothDigestsAreTheOnesCoreutilsWouldPrint() {
    var path = this.Write("program", "procman");
    var digest = FileDigest.Of(path);

    Assert.Multiple(() => {
      Assert.That(digest.Sha256, Is.EqualTo("b42407086de28a30a5f8dd23825999cb1da0a8582332adccf7bca8ed9f9577ad"));
      Assert.That(digest.Sha1, Is.EqualTo("ccbfdde2ddbbbae1399d155fd209aaa4a5ee669c"));
      Assert.That(digest.Why, Is.EqualTo(UnknownReason.None));
    });
  }

  /// <summary>
  /// Read in one pass over the bytes, which matters for an image rather than for this fixture: the
  /// cost of hashing is the disk, and reading 300 MB twice to answer two questions about the same
  /// bytes would double the only expensive part.
  /// </summary>
  [Test]
  public void AFileLongerThanOneBufferHashesTheWholeOfItself() {
    var content = new string('x', 300_000);
    var path = this.Write("large", content);
    var digest = FileDigest.Of(path);

    using var expected256 = System.Security.Cryptography.SHA256.Create();
    using var expected1 = System.Security.Cryptography.SHA1.Create();
    var bytes = System.Text.Encoding.UTF8.GetBytes(content);

    Assert.That(digest.Sha256, Is.EqualTo(Convert.ToHexStringLower(expected256.ComputeHash(bytes))));
    Assert.That(digest.Sha1, Is.EqualTo(Convert.ToHexStringLower(expected1.ComputeHash(bytes))));
  }

  [Test]
  public void AnEmptyFileHashesToTheEmptyDigestsRatherThanToNothing() {
    var digest = FileDigest.Of(this.Write("empty", string.Empty));

    Assert.That(digest.Sha256, Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
    Assert.That(digest.Sha1, Is.EqualTo("da39a3ee5e6b4b0d3255bfef95601890afd80709"));
  }

  /// <summary>
  /// An image replaced since the process started is gone, not refused: telling the reader to find
  /// more privilege would be the one piece of advice that cannot help them (PRD §72.3).
  /// </summary>
  [Test]
  public void AnImageThatIsNoLongerThereSaysSoAndInTheRightWords() {
    var digest = FileDigest.Of(Path.Combine(this._directory, "replaced"));

    Assert.That(digest.Sha256, Is.Null);
    Assert.That(digest.Sha1, Is.Null);
    Assert.That(digest.Why, Is.EqualTo(UnknownReason.SourceGone));
  }

  [Test]
  public void NoPathAtAllIsNotAHashOfNothing() {
    var digest = FileDigest.Of(null);

    Assert.That(digest.Sha256, Is.Null);
    Assert.That(digest.Why, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
  }

  #region as a column (PRD §5.1)

  [Test]
  public void TheDigestsRenderAndExportAsThemselves() {
    var record = default(ProcessRecord);
    record.Name = "test";
    record.ImageSha256 = "b42407086de28a30a5f8dd23825999cb1da0a8582332adccf7bca8ed9f9577ad";
    record.ImageSha1 = "ccbfdde2ddbbbae1399d155fd209aaa4a5ee669c";

    Assert.That(FieldAccessor.Text(ProcessField.ImageSha256, in record, null, 0), Is.EqualTo(record.ImageSha256));
    Assert.That(FieldAccessor.RawText(ProcessField.ImageSha1, in record), Is.EqualTo(record.ImageSha1));
  }

  /// <summary>
  /// Not hashed and hashed to nothing are different answers, and the column shows which (§72.3).
  /// </summary>
  [Test]
  public void ANotComputedDigestIsAReasonRatherThanAnEmptyCell() {
    var record = default(ProcessRecord);
    record.Name = "test";
    record.ImageHashReason = UnknownReason.NotSampledYet;

    Assert.That(
      FieldAccessor.Text(ProcessField.ImageSha256, in record, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet))
    );
    Assert.That(FieldAccessor.RawText(ProcessField.ImageSha256, in record), Is.Null);
  }

  [Test]
  public void TheDigestsAreSpelledTheWayThePrdNamesThem() {
    Assert.Multiple(() => {
      Assert.That(FieldRegistry.TryParse("hash.sha256", out var sha256), Is.True);
      Assert.That(sha256, Is.EqualTo(ProcessField.ImageSha256));

      Assert.That(FieldRegistry.TryParse("hash.sha1", out var sha1), Is.True);
      Assert.That(sha1, Is.EqualTo(ProcessField.ImageSha1));

      // On demand only, which is what keeps a column whose cost is the size of a file out of every
      // default set there is (PRD §5.4, §21).
      Assert.That(FieldRegistry.Get(ProcessField.ImageSha256).Cost, Is.EqualTo(FieldCost.High));
      Assert.That(FieldRegistry.Get(ProcessField.ImageSha1).Cost, Is.EqualTo(FieldCost.High));
    });
  }

  #endregion

  #region asking for it (PRD §5.4)

  [Test]
  public void NamingEitherDigestIsWhatAsksForTheRead() {
    Assert.Multiple(() => {
      Assert.That(Parse("--columns=name,hash.sha256").WantsImageHashes, Is.True);
      Assert.That(Parse("--columns=name,hash.sha1").WantsImageHashes, Is.True);
      Assert.That(Parse("--filter=hash.sha256:b424070").WantsImageHashes, Is.True);
      Assert.That(Parse("--columns=name,path").WantsImageHashes, Is.False);
    });
  }

  /// <summary>
  /// Nobody asking leaves the reason as "not sampled", never as an image that hashes to nothing.
  /// </summary>
  [Test]
  public void TheProbeHashesNothingUntilItIsAskedTo() {
    var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new LinuxProbe(new() {
      ProcRoot = root,
      PasswdPath = Path.Combine(root, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    foreach (var process in snapshot.Processes) {
      Assert.That(process.ImageSha256, Is.Null);
      Assert.That(process.ImageHashReason, Is.EqualTo(UnknownReason.NotSampledYet));
    }
  }

  /// <summary>
  /// A process with no image to hash — a kernel thread, and every process in a recorded tree, whose
  /// <c>exe</c> link was not captured — reports that there is nothing rather than a digest of
  /// nothing.
  /// </summary>
  [Test]
  public void AProcessWithNoImageIsNotAnImageThatHashesToNothing() {
    var root = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "proc-desktop");
    using var probe = new LinuxProbe(new() {
      ProcRoot = root,
      PasswdPath = Path.Combine(root, "passwd"),
      ClockTicksPerSecond = 100,
      PageSize = 4096,
      EffectiveUserId = 0,
      ReadImageHashes = true,
    });

    var snapshot = new SystemSnapshot();
    probe.Sample(snapshot);

    foreach (var process in snapshot.Processes) {
      Assert.That(process.ImageSha256, Is.Null);
      Assert.That(process.ImageHashReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
    }
  }

  private static Hawkynt.ProcessManager.App.CommandLineOptions Parse(string argument)
    => Hawkynt.ProcessManager.App.CommandLineOptions.Parse([argument], null);

  #endregion

}
