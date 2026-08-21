using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// What a file on disk is, for the properties of an executable or a module (PRD §25.3, §25.6).
/// </summary>
[TestFixture]
public sealed class FileFactsTests {

  private string _directory = string.Empty;

  [SetUp]
  public void CreateDirectory() {
    this._directory = Path.Combine(Path.GetTempPath(), $"procman file facts {Guid.NewGuid():N}");
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
  public void TheHashIsTheOneSha256sumWouldPrint() {
    // The reference value, computed by sha256sum rather than by this program. If ours ever disagrees
    // with the coreutils tool, ours is wrong.
    var path = this.Write("program", "procman");

    Assert.That(FileDigest.Of(path).Sha256, Is.EqualTo("b42407086de28a30a5f8dd23825999cb1da0a8582332adccf7bca8ed9f9577ad"));
  }

  [Test]
  public void AnEmptyFileHashesToTheEmptyDigestRatherThanToNothing() {
    var path = this.Write("empty", string.Empty);

    Assert.That(FileDigest.Of(path).Sha256, Is.EqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));
    Assert.That(FileDigest.Of(path).Reason, Is.Null, "a file of no bytes was still read");
  }

  /// <summary>
  /// A file that is not there produces the reason, never a hash of nothing. The two would otherwise
  /// be the same cell (PRD §72.3).
  /// </summary>
  [Test]
  public void AMissingFileGivesTheReasonAndNoDigest() {
    var digest = FileDigest.Of(Path.Combine(this._directory, "not here"));

    Assert.That(digest.Sha256, Is.Null);
    Assert.That(digest.Reason, Is.Not.Null);
    Assert.That(digest.Display, Is.EqualTo(digest.Reason));
  }

  [Test]
  public void TheDigestIsShownInGroupsAPersonCanCompare() {
    var path = this.Write("program", "procman");
    var display = FileDigest.Of(path).Display;

    Assert.That(display.Split(' '), Has.Length.EqualTo(8));
    Assert.That(display.Replace(" ", string.Empty, StringComparison.Ordinal), Is.EqualTo(FileDigest.Of(path).Sha256));
  }

  [Test]
  public void TheFactsAreTheFileSystemsOwnAnswers() {
    var path = this.Write("program", "procman");
    var facts = FileFacts.Describe(path);

    Assert.Multiple(() => {
      Assert.That(facts.Exists, Is.True);
      Assert.That(facts.SizeBytes, Is.EqualTo(7));
      Assert.That(facts.Reason, Is.Null);
      Assert.That(facts.ModifiedUtc, Is.Not.Null);
    });
  }

  [Test]
  [Platform("Linux")]
  // NUnit's attribute above decides whether this runs; the analyser needs telling separately, or the
  // Windows leg fails to compile a test it would never have executed.
  [System.Runtime.Versioning.SupportedOSPlatform("linux")]
  public void ThePermissionsReadTheWayLsWritesThem() {
    var path = this.Write("program", "procman");
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

    Assert.That(FileFacts.Describe(path).Permissions, Is.EqualTo("rwxr-----"));
  }

  [Test]
  public void AMissingFileSaysSoRatherThanReportingZeroBytes() {
    var facts = FileFacts.Describe(Path.Combine(this._directory, "not here"));

    Assert.That(facts.Exists, Is.False);
    Assert.That(facts.Reason, Is.Not.Null);
    Assert.That(FileFactsFormatting.Size(in facts), Is.EqualTo(facts.Reason), "the reason, not a zero");
  }

}

/// <summary>
/// Handing a folder or a page to the session's own opener (PRD §25.3).
/// </summary>
[TestFixture]
public sealed class DesktopOpenTests {

  [Test]
  [Platform("Linux")]
  public void RevealingAFileOpensTheFolderItIsIn() {
    var request = DesktopOpen.Reveal("/usr/bin/env");

    Assert.That(request, Is.Not.Null);
    Assert.That(request!.FileName, Is.EqualTo("xdg-open"));
    Assert.That(request.Arguments, Is.EqualTo(new[] { "/usr/bin" }));
  }

  [Test]
  public void ThereIsNothingToRevealWithoutAPath() {
    Assert.That(DesktopOpen.Reveal(null), Is.Null);
    Assert.That(DesktopOpen.Reveal("   "), Is.Null);
  }

  /// <summary>
  /// Only http and https are handed to the opener. Anything else is a scheme the desktop will map to
  /// a program of its own choosing, and this is not the place to hand an arbitrary one an argument.
  /// </summary>
  [Test]
  public void OnlyWebSchemesAreOpened() {
    Assert.That(DesktopOpen.Browse("file:///etc/shadow"), Is.Null);
    Assert.That(DesktopOpen.Browse("ssh://somewhere"), Is.Null);
    Assert.That(DesktopOpen.Browse("not a url"), Is.Null);
  }

  /// <summary>
  /// A process may call itself anything at all, including something containing an ampersand — which
  /// unescaped would end the query and start a parameter of its own.
  /// </summary>
  [Test]
  [Platform("Linux")]
  public void ASearchTermIsEscapedRatherThanPastedIn() {
    var request = DesktopOpen.Search("weird & name");

    Assert.That(request, Is.Not.Null);
    Assert.That(request!.Arguments[0], Does.Contain("weird%20%26%20name"));
    Assert.That(request.Arguments[0], Does.StartWith($"https://{DesktopOpen.SearchEngine}/"));
  }

  [Test]
  public void ThereIsNothingToSearchForWithoutATerm() {
    Assert.That(DesktopOpen.Search(null), Is.Null);
    Assert.That(DesktopOpen.Search(" "), Is.Null);
  }

}
