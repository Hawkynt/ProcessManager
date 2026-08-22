using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Where a running image came from, and whether it is still what its package shipped (PRD §14, §70).
/// </summary>
/// <remarks>
/// <para>
/// The <c>pacman</c> fixture is a recording: the <c>desc</c>, <c>files</c> and <c>mtree</c> of the
/// installed <c>which</c> package, copied off the machine this was written on, gzip and all. The
/// <c>dpkg</c> and Flatpak fixtures are not recordings — there is no <c>dpkg</c> and no Flatpak on
/// that machine — and are written from the formats upstream documents: <c>deb-md5sums(5)</c> for
/// the digest list and <c>flatpak-metadata(5)</c> for the keys of <c>.flatpak-info</c>. Which of
/// the two a test rests on matters, so each says so.
/// </para>
/// <para>
/// Everything here runs on every CI leg. The parsers have no platform attribute and the reader is
/// pointed at a fixture directory, so a Windows or macOS leg exercises the same code the Linux one
/// does (PRD §9.1, §9.2).
/// </para>
/// </remarks>
[TestFixture]
public sealed class PackageProvenanceTests {

  private static string ArchRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "packages-arch");

  private static string DebianRoot
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "packages-debian");

  private static string SandboxFixture(string name)
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "packages-sandbox", name);

  #region pacman's own files

  [Test]
  public void APacmanDescriptionCarriesTheNameTheVersionAndHowItWasValidated() {
    var desc = File.ReadAllBytes(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "desc"));
    var description = PacmanLocalDatabase.ReadDescription(desc);

    Assert.Multiple(() => {
      Assert.That(description.Name, Is.EqualTo("which"));
      Assert.That(description.Version, Is.EqualTo("2.25-1"));
      // The only record of a signature anywhere on the machine: pacman checked one at install time
      // and remembered that it did.
      Assert.That(description.Validation, Is.EqualTo(PacmanLocalDatabase.Validation.Signature));
    });
  }

  /// <summary>
  /// The word decides a verdict, so each of them is held against what libalpm writes.
  /// </summary>
  [TestCase("pgp", PacmanLocalDatabase.Validation.Signature)]
  [TestCase("sha256", PacmanLocalDatabase.Validation.Checksum)]
  [TestCase("md5", PacmanLocalDatabase.Validation.Checksum)]
  [TestCase("none", PacmanLocalDatabase.Validation.None)]
  [TestCase("something-new", PacmanLocalDatabase.Validation.Unknown)]
  public void EachValidationWordMeansWhatItSays(string word, PacmanLocalDatabase.Validation expected) {
    var desc = Encoding.UTF8.GetBytes($"%NAME%\nx\n\n%VALIDATION%\n{word}\n");
    Assert.That(PacmanLocalDatabase.ReadDescription(desc).Validation, Is.EqualTo(expected));
  }

  /// <summary>
  /// §31 asks a mapped image for a description and a company, and an ELF has neither: there is no
  /// version resource in the format. What a Linux machine publishes about a file is what the
  /// database that installed it publishes about its package, and these are the two lines it is in.
  /// </summary>
  [Test]
  public void APacmanDescriptionAlsoCarriesWhatThePackageIsAndWhoBuiltIt() {
    var desc = File.ReadAllBytes(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "desc"));
    var description = PacmanLocalDatabase.ReadDescription(desc);

    Assert.Multiple(() => {
      Assert.That(description.Summary, Is.EqualTo("A utility to show the full path of commands"));
      Assert.That(description.Packager, Is.EqualTo("Tobias Powalowski <tpowa@archlinux.org>"));
      // And the three that were already read are still read: the two new headers sit between
      // %VERSION% and %VALIDATION% in a real file, and a walk that stops early loses the last of them.
      Assert.That(description.Name, Is.EqualTo("which"));
      Assert.That(description.Version, Is.EqualTo("2.25-1"));
      Assert.That(description.Validation, Is.EqualTo(PacmanLocalDatabase.Validation.Signature));
    });
  }

  /// <summary>
  /// A package with no such lines has none, and null is that answer rather than an empty string a
  /// properties box would render as a blank field (PRD §72.3).
  /// </summary>
  [Test]
  public void APackageThatSaysNeitherIsNotReportedAsSayingNothing() {
    var desc = Encoding.UTF8.GetBytes("%NAME%\nx\n\n%VERSION%\n1\n");
    var description = PacmanLocalDatabase.ReadDescription(desc);

    Assert.Multiple(() => {
      Assert.That(description.Summary, Is.Null);
      Assert.That(description.Packager, Is.Null);
    });
  }

  /// <summary>
  /// A description with no validation line is not a package that was installed unchecked.
  /// </summary>
  [Test]
  public void AMissingValidationLineIsUnknownRatherThanNone() {
    var desc = Encoding.UTF8.GetBytes("%NAME%\nwhich\n\n%VERSION%\n2.25-1\n");
    Assert.That(
      PacmanLocalDatabase.ReadDescription(desc).Validation,
      Is.EqualTo(PacmanLocalDatabase.Validation.Unknown)
    );
  }

  [Test]
  public void AFileListYieldsFilesAndNotDirectories() {
    var files = File.ReadAllBytes(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "files"));

    var found = new List<string>();
    foreach (var path in PacmanLocalDatabase.Paths(files))
      found.Add(Encoding.UTF8.GetString(path));

    Assert.That(found, Is.EqualTo(new[] {
      "usr/bin/which",
      "usr/share/info/which.info.gz",
      "usr/share/man/man1/which.1.gz",
    }), "the directories end in a slash and own nothing that can be running");
  }

  [Test]
  public void OwnershipIsTheExactPathAndNotAPrefixOfIt() {
    var files = File.ReadAllBytes(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "files"));

    Assert.Multiple(() => {
      Assert.That(PacmanLocalDatabase.Owns(files, "usr/bin/which"u8), Is.True);
      Assert.That(PacmanLocalDatabase.Owns(files, "usr/bin/whichever"u8), Is.False);
      Assert.That(PacmanLocalDatabase.Owns(files, "usr/bin/whic"u8), Is.False);
    });
  }

  /// <summary>The digest <c>pacman -Qkk</c> compares against, out of the gzipped manifest.</summary>
  [Test]
  public void TheManifestCarriesTheDigestThePackageShipped() {
    var mtree = Gunzip(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "mtree"));

    Assert.That(PacmanLocalDatabase.TryFindEntry(mtree, "usr/bin/which"u8, out var entry), Is.True);
    Assert.Multiple(() => {
      Assert.That(entry.Sha256, Is.EqualTo("178ff2f482e277f3903a64d3a29e91cc78d2de2ca50255f396b723857d5db69c"));
      Assert.That(entry.SizeBytes.TryGetValue(out var size), Is.True);
      Assert.That(size, Is.EqualTo(27280UL));
    });
  }

  /// <summary>
  /// A directory has no digest, which is not the same as a digest that did not match.
  /// </summary>
  [Test]
  public void ADirectoryEntryHasNoDigestAtAll() {
    var mtree = Gunzip(Path.Combine(ArchRoot, "pacman", "local", "which-2.25-1", "mtree"));

    Assert.That(PacmanLocalDatabase.TryFindEntry(mtree, "usr/bin"u8, out var entry), Is.True);
    Assert.That(entry.Sha256, Is.Null);
  }

  /// <summary>
  /// A path with a space in it, escaped the way libarchive escapes it.
  /// </summary>
  /// <remarks>
  /// The two lines below are copied from the <c>alsa-ucm-conf</c> manifest on the machine this was
  /// written on, which is where the case was found: comparing the raw bytes reported every file with
  /// a space in its name as missing from the package that owns it.
  /// </remarks>
  [Test]
  public void AnEscapedPathIsDecodedBeforeItIsCompared() {
    var mtree = Encoding.UTF8.GetBytes(
      "#mtree\n"
      + "/set type=file uid=0 gid=0 mode=644\n"
      + "./usr/share/alsa/ucm2/NXP/iMX8/Librem_5/Librem\\0405.conf time=1781523786.0 size=700 sha256digest=5326b590a6f015c7fccde8a773c87534e81b1943c1a479ebdc33cd99db3ad640\n"
      + "./usr/share/alsa/ucm2/NXP/iMX8/Librem_5_Devkit/Librem\\0405\\040Devkit.conf time=1781523786.0 size=54 sha256digest=54fda5e31793067e06379dd73c82bada07ce4a61dd5a4cb4c14c2cec33c5c417\n"
    );

    Assert.Multiple(() => {
      Assert.That(
        PacmanLocalDatabase.TryFindEntry(mtree, "usr/share/alsa/ucm2/NXP/iMX8/Librem_5/Librem 5.conf"u8, out var one),
        Is.True
      );
      Assert.That(one.Sha256, Is.EqualTo("5326b590a6f015c7fccde8a773c87534e81b1943c1a479ebdc33cd99db3ad640"));

      Assert.That(
        PacmanLocalDatabase.TryFindEntry(
          mtree,
          "usr/share/alsa/ucm2/NXP/iMX8/Librem_5_Devkit/Librem 5 Devkit.conf"u8,
          out var two
        ),
        Is.True
      );
      Assert.That(two.Sha256, Is.EqualTo("54fda5e31793067e06379dd73c82bada07ce4a61dd5a4cb4c14c2cec33c5c417"));
    });
  }

  #endregion

  #region dpkg's own files

  /// <summary>
  /// Written from <c>deb-md5sums(5)</c> and from the shape of an installed <c>.list</c>, because
  /// there is no <c>dpkg</c> on the machine this was written on to record one from.
  /// </summary>
  [Test]
  public void ADebianFileListYieldsAbsolutePathsWithoutItsOwnRoot() {
    var list = File.ReadAllBytes(Path.Combine(DebianRoot, "dpkg", "info", "bash.list"));

    var found = new List<string>();
    foreach (var path in DpkgDatabase.Paths(list))
      found.Add(Encoding.UTF8.GetString(path));

    Assert.Multiple(() => {
      Assert.That(found, Does.Contain("/bin/bash"));
      Assert.That(found, Does.Contain("/usr/share/man/man1/bash.1.gz"));
      Assert.That(found, Does.Not.Contain("/."), "the package's own root names no file");
    });
  }

  [Test]
  public void ADebianDigestListIsFoundByPathWithoutItsLeadingSlash() {
    var sums = File.ReadAllBytes(Path.Combine(DebianRoot, "dpkg", "info", "bash.md5sums"));

    Assert.Multiple(() => {
      Assert.That(DpkgDatabase.FindMd5(sums, "bin/bash"u8), Is.EqualTo("53c0d4afe4bc4eccb5cb234d2e06ef4d"));
      Assert.That(DpkgDatabase.FindMd5(sums, "/bin/bash"u8), Is.Null, "the file writes no leading slash");
      Assert.That(DpkgDatabase.FindMd5(sums, "etc/bash.bashrc"u8), Is.Null, "a conffile has no digest here");
    });
  }

  /// <summary>Two spaces between the digest and the path, and a path may contain one of its own.</summary>
  [Test]
  public void APathWithASpaceInItIsStillFound() {
    var sums = Encoding.UTF8.GetBytes(
      "d41d8cd98f00b204e9800998ecf8427e  usr/share/My App/data.bin\n"
    );

    Assert.That(
      DpkgDatabase.FindMd5(sums, "usr/share/My App/data.bin"u8),
      Is.EqualTo("d41d8cd98f00b204e9800998ecf8427e")
    );
  }

  [Test]
  public void AVersionComesOutOfTheStanzaItBelongsTo() {
    var status = File.ReadAllBytes(Path.Combine(DebianRoot, "dpkg", "status"));

    Assert.Multiple(() => {
      Assert.That(DpkgDatabase.FindVersion(status, "bash"), Is.EqualTo("5.2.15-2+b7"));
      Assert.That(DpkgDatabase.FindVersion(status, "coreutils"), Is.EqualTo("9.1-1"));
      Assert.That(DpkgDatabase.FindVersion(status, "not-installed"), Is.Null);
    });
  }

  /// <summary>
  /// The same three fields <c>pacman</c>'s <c>desc</c> carries, out of a <c>dpkg</c> stanza — and
  /// out of <em>one</em> walk of it, because the file is one stanza per installed package and
  /// walking it three times to answer three questions costs three times as much.
  /// </summary>
  [Test]
  public void ADebianStanzaCarriesTheVersionTheSynopsisAndTheMaintainer() {
    var status = File.ReadAllBytes(Path.Combine(DebianRoot, "dpkg", "status"));
    var stanza = DpkgDatabase.FindStanza(status, "bash");

    Assert.Multiple(() => {
      Assert.That(stanza.Version, Is.EqualTo("5.2.15-2+b7"));
      Assert.That(stanza.Summary, Is.EqualTo("GNU Bourne Again SHell"));
      Assert.That(stanza.Maintainer, Is.EqualTo("Matthias Klose <doko@debian.org>"));
    });
  }

  /// <summary>
  /// Debian writes <c>Maintainer</c> above <c>Version</c> and <c>Description</c> at the end of the
  /// stanza, and promises that order nowhere — so the stanza is walked to its end rather than
  /// abandoned at whichever of the three came first.
  /// </summary>
  [Test]
  public void TheWholeStanzaIsReadRatherThanTheFirstFieldFoundInIt() {
    var status = File.ReadAllBytes(Path.Combine(DebianRoot, "dpkg", "status"));

    Assert.Multiple(() => {
      // The last stanza in the file, whose Description is its final line and has no blank line after
      // it: a reader that needs the terminator to commit what it has read loses this one.
      var last = DpkgDatabase.FindStanza(status, "coreutils");
      Assert.That(last.Summary, Is.EqualTo("GNU core utilities"));
      Assert.That(last.Maintainer, Is.EqualTo("Michael Stone <mstone@debian.org>"));

      // And a package in no stanza has no fields, rather than the fields of the stanza beside it.
      var absent = DpkgDatabase.FindStanza(status, "not-installed");
      Assert.That(absent.Version, Is.Null);
      Assert.That(absent.Summary, Is.Null);
      Assert.That(absent.Maintainer, Is.Null);
    });
  }

  /// <summary>
  /// The extended description is the indented paragraphs under the synopsis, and a properties box
  /// wants the sentence rather than the essay.
  /// </summary>
  [Test]
  public void OnlyTheSynopsisIsTakenAndNotTheParagraphsUnderIt() {
    var status = Encoding.UTF8.GetBytes(
      "Package: x\nVersion: 1\nDescription: the one-line summary\n it goes on at length\n .\n and on\n"
    );

    Assert.That(DpkgDatabase.FindStanza(status, "x").Summary, Is.EqualTo("the one-line summary"));
  }

  [Test]
  public void ThePackageNameLosesItsArchitectureQualifier() {
    Assert.Multiple(() => {
      Assert.That(DpkgDatabase.PackageOf("bash.list", ".list"), Is.EqualTo("bash"));
      Assert.That(DpkgDatabase.PackageOf("libfoo:amd64.list", ".list"), Is.EqualTo("libfoo"));
      Assert.That(DpkgDatabase.PackageOf("bash.md5sums", ".list"), Is.Null);
    });
  }

  #endregion

  #region the sandboxes

  /// <summary>
  /// The keys are <c>flatpak-metadata(5)</c>'s, and the file is written from it rather than
  /// recorded: there is no Flatpak on the machine this was written on to record one from.
  /// </summary>
  [Test]
  public void AFlatpakNamesItselfInsideItsOwnSandbox() {
    var info = File.ReadAllBytes(SandboxFixture("flatpak-info"));
    var identity = SandboxPackaging.ReadFlatpakInfo(info);

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.Flatpak));
      Assert.That(identity.ApplicationId, Is.EqualTo("org.gnome.Calculator"));
      Assert.That(identity.Name, Is.EqualTo("org.gnome.Calculator"));
      // Out of [Instance], which is the part flatpak fills in for a running app.
      Assert.That(identity.Version, Is.EqualTo("stable"));
    });
  }

  /// <summary>
  /// The runtime a Flatpak is built against is in the same file and is not what the app is.
  /// </summary>
  [Test]
  public void TheRuntimeAFlatpakRunsOnIsNotItsVersion() {
    var info = File.ReadAllBytes(SandboxFixture("flatpak-info"));
    var identity = SandboxPackaging.ReadFlatpakInfo(info);

    Assert.That(identity.Version, Does.Not.Contain("Platform"));
  }

  [Test]
  public void AFileWithNoApplicationNameIsNotAFlatpak() {
    var identity = SandboxPackaging.ReadFlatpakInfo("[Context]\nshared=network;\n"u8);
    Assert.That(identity.Source, Is.EqualTo(PackageSource.Unknown));
  }

  /// <summary>
  /// The scope snapd builds, from its own format string.
  /// </summary>
  /// <remarks>
  /// <c>sandbox/cgroup/tracking.go</c> writes <c>fmt.Sprintf("%s-%s.scope", securityTagUnitName,
  /// uuid)</c> and gives <c>snap.hello-world.sh-4706fe54-7802-4808-aa7e-ae8b567239e0.scope</c> as
  /// its example. The UUID is joined with a hyphen and not with a third dot, which is the part worth
  /// a test: splitting on dots would report the application as <c>sh-4706fe54</c>.
  /// </remarks>
  [Test]
  public void ASnapNamesItselfInTheCgroupSnapdConfinedItTo() {
    var identity = SandboxPackaging.ReadSnapCgroup(
      "/user.slice/user-1000.slice/user@1000.service/app.slice/snap.hello-world.sh-4706fe54-7802-4808-aa7e-ae8b567239e0.scope"
    );

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.Snap));
      Assert.That(identity.Name, Is.EqualTo("hello-world"), "a snap name may contain hyphens of its own");
      Assert.That(identity.ApplicationId, Is.EqualTo("hello-world.sh"));
    });
  }

  [Test]
  public void ASnapScopeWithoutAnInstanceIsStillASnap() {
    var identity = SandboxPackaging.ReadSnapCgroup("/system.slice/snap.firefox.firefox.scope");

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.Snap));
      Assert.That(identity.Name, Is.EqualTo("firefox"));
      Assert.That(identity.ApplicationId, Is.EqualTo("firefox.firefox"));
    });
  }

  [Test]
  public void AnOrdinaryScopeIsNotASnap() {
    Assert.Multiple(() => {
      Assert.That(
        SandboxPackaging.ReadSnapCgroup("/user.slice/user-1000.slice/session-3.scope").Source,
        Is.EqualTo(PackageSource.None)
      );
      Assert.That(SandboxPackaging.ReadSnapCgroup(null).Source, Is.EqualTo(PackageSource.Unknown));
    });
  }

  /// <summary>
  /// The cgroup shape flatpak documents, used when the sandbox's own file is out of reach.
  /// </summary>
  [Test]
  public void AFlatpakScopeCarriesTheApplicationId() {
    var identity = SandboxPackaging.ReadFlatpakCgroup(
      "/user.slice/user-1000.slice/user@1000.service/app.slice/app-flatpak-org.gnome.Calculator-2495.scope"
    );

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.Flatpak));
      Assert.That(identity.ApplicationId, Is.EqualTo("org.gnome.Calculator"));
    });
  }

  [Test]
  public void AnAppImageIsRecognisedByTheMountItsRuntimeMade() {
    // mkdtemp replaces six X's, so the six characters after the name are not part of it.
    var identity = SandboxPackaging.ReadAppImage("/tmp/.mount_KritaHkL9Wq/AppRun", null);

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.AppImage));
      Assert.That(identity.Name, Is.EqualTo("Krita"));
    });
  }

  [Test]
  public void TheAppImageVariableBeatsAGuessFromTheMountPoint() {
    var identity = SandboxPackaging.ReadAppImage(
      "/tmp/.mount_KritaHkL9Wq/AppRun",
      "/home/somebody/Applications/krita-5.2.2-x86_64.AppImage"
    );

    Assert.That(identity.Name, Is.EqualTo("krita-5.2.2-x86_64.AppImage"));
  }

  /// <summary>
  /// Extracted rather than mounted — the fallback when FUSE is unavailable — has no name in the
  /// path, and says so rather than inventing one out of a hash.
  /// </summary>
  [Test]
  public void AnExtractedAppImageIsStillAnAppImageWithNoName() {
    var identity = SandboxPackaging.ReadAppImage("/tmp/appimage_extracted_9f8e7d6c/AppRun", null);

    Assert.Multiple(() => {
      Assert.That(identity.Source, Is.EqualTo(PackageSource.AppImage));
      Assert.That(identity.Name, Is.Null);
    });
  }

  [Test]
  public void AnOrdinaryPathIsNoAppImage()
    => Assert.That(SandboxPackaging.ReadAppImage("/usr/bin/bash", null).Source, Is.EqualTo(PackageSource.None));

  [Test]
  public void AVariableIsReadOutOfTheNulSeparatedBlock() {
    var environ = "LANG=en_GB.UTF-8\0APPIMAGE=/home/x/App.AppImage\0PATH=/usr/bin\0"u8.ToArray();

    Assert.Multiple(() => {
      Assert.That(
        SandboxPackaging.ReadEnvironmentVariable(environ, "APPIMAGE"u8),
        Is.EqualTo("/home/x/App.AppImage")
      );
      // A prefix of another variable's name must not match it.
      Assert.That(SandboxPackaging.ReadEnvironmentVariable(environ, "APP"u8), Is.Null);
      Assert.That(SandboxPackaging.ReadEnvironmentVariable(environ, "HOME"u8), Is.Null);
    });
  }

  #endregion

  #region what is running inside

  [Test]
  public void ADotNetProcessIsRecognisedByItsRuntimeAndNotByItsName()
    => Assert.That(
      RuntimeDetector.Detect(Maps("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so")),
      Is.EqualTo(ProcessRuntime.DotNet)
    );

  [TestCase("/usr/lib/jvm/java-21-openjdk/lib/server/libjvm.so", ProcessRuntime.Java)]
  [TestCase("/usr/lib/libpython3.13.so.1.0", ProcessRuntime.Python)]
  [TestCase("/usr/lib/libruby.so.3.4", ProcessRuntime.Ruby)]
  [TestCase("/usr/lib/perl5/5.40/core_perl/CORE/libperl.so", ProcessRuntime.Perl)]
  [TestCase("/usr/lib/libmonosgen-2.0.so.1", ProcessRuntime.Mono)]
  [TestCase("/usr/lib/wine/x86_64-unix/libwine.so.1", ProcessRuntime.Wine)]
  public void EachRuntimeIsNamedByTheLibraryThatRunsIt(string module, ProcessRuntime expected)
    => Assert.That(RuntimeDetector.Detect(Maps(module)), Is.EqualTo(expected));

  /// <summary>
  /// A process with no runtime mapped is native, which is a finding rather than a hole.
  /// </summary>
  [Test]
  public void AProcessWithNoRuntimeMappedIsNative()
    => Assert.That(
      RuntimeDetector.Detect(Maps("/usr/lib/libc.so.6", "/usr/bin/bash")),
      Is.EqualTo(ProcessRuntime.Native)
    );

  /// <summary>
  /// A name is not evidence. <c>libmyjvm.so</c> is somebody's own library, and a file called
  /// <c>java</c> in the map is a file and not a virtual machine.
  /// </summary>
  [Test]
  public void ALibraryThatMerelyContainsTheNameIsNotARuntime()
    => Assert.That(
      RuntimeDetector.Detect(Maps("/opt/thing/libmyjvm.so", "/opt/thing/java")),
      Is.EqualTo(ProcessRuntime.Native)
    );

  /// <summary>
  /// A host that has loaded a scripting library is the host. Which one runs the program is the
  /// question, and a Python extension inside a .NET process does not make it a Python process.
  /// </summary>
  [Test]
  public void TheHostWinsOverWhatItHasLoaded()
    => Assert.That(
      RuntimeDetector.Detect(Maps("/usr/lib/libpython3.13.so.1.0", "/usr/share/dotnet/libcoreclr.so")),
      Is.EqualTo(ProcessRuntime.DotNet)
    );

  #endregion

  #region the reader, against a fixture database

  [Test]
  public void ThePackageThatOwnsAPathIsFoundAndConfirmed() {
    var trust = new PackageDatabaseReader(ArchRoot).Describe("/usr/bin/which", 27280, 0, default, verify: false);

    Assert.Multiple(() => {
      Assert.That(trust.Package.Source, Is.EqualTo(PackageSource.Pacman));
      Assert.That(trust.Package.Name, Is.EqualTo("which"));
      Assert.That(trust.Package.Version, Is.EqualTo("2.25-1"));
      Assert.That(trust.Package.Text, Is.EqualTo("which 2.25-1"));
      // Asked for the owner and nothing else: the check is a separate question with its own cost.
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.NotChecked));
    });
  }

  [Test]
  public void APathNoPackageClaimsIsNotPackagedAndIsThereforeUnsigned() {
    var trust = new PackageDatabaseReader(ArchRoot).Describe("/home/somebody/build/a.out", 10, 0, default, verify: true);

    Assert.Multiple(() => {
      Assert.That(trust.Package.Source, Is.EqualTo(PackageSource.None));
      Assert.That(trust.Package.Text, Is.EqualTo("not packaged"));
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Unsigned));
      Assert.That(trust.Detail, Is.Not.Null);
    });
  }

  [Test]
  public void ADebianPackageIsFoundWithTheVersionOutOfTheStatusFile() {
    var trust = new PackageDatabaseReader(DebianRoot).Describe("/bin/bash", 1, 0, default, verify: false);

    Assert.Multiple(() => {
      Assert.That(trust.Package.Source, Is.EqualTo(PackageSource.Dpkg));
      Assert.That(trust.Package.Name, Is.EqualTo("bash"));
      Assert.That(trust.Package.Version, Is.EqualTo("5.2.15-2+b7"));
    });
  }

  #endregion

  #region the vocabulary

  /// <summary>
  /// §70's first requirement, at the only place a reader meets it: five columns, five answers, and
  /// no way to read one off another.
  /// </summary>
  /// <remarks>
  /// The row below is the ordinary case on an Arch machine with anything built locally on it — the
  /// file is exactly what its package recorded and nobody signed the package — and it is the case
  /// this program used to report as the single word "Unsigned", losing the half about the file.
  /// Every one of the five reads differently here, which is the whole assertion.
  /// </remarks>
  [Test]
  public void TheFiveQuestionsReadAsFiveAnswers() {
    var record = default(ProcessRecord);
    record.ImageSha256 = "b42407086de28a30a5f8dd23825999cb1da0a8582332adccf7bca8ed9f9577ad";
    record.PackageStatus = SignatureStatus.Verified;
    record.TrustChain = SignatureStatus.Unsigned;

    Assert.Multiple(() => {
      // One: what the bytes are, which is not a verdict about anything.
      Assert.That(FieldAccessor.Text(ProcessField.ImageSha256, in record, null, 0), Is.EqualTo(record.ImageSha256));
      // Two: the file against what was recorded for it.
      Assert.That(FieldAccessor.Text(ProcessField.PackageStatus, in record, null, 0), Is.EqualTo("Verified"));
      // Three: who stands behind it, which here is nobody — and says so without touching two.
      Assert.That(FieldAccessor.Text(ProcessField.TrustChain, in record, null, 0), Is.EqualTo("Unsigned"));
      // Four: nothing was asked of anybody, because this program does not ask.
      Assert.That(
        FieldAccessor.Text(ProcessField.Reputation, in record, null, 0),
        Is.EqualTo(Humanize.Placeholder(UnknownReason.NotAskedByDesign))
      );
      // Five: nothing was ever sent. The reader's every answer says so, on every path it has.
      Assert.That(ImageTrust.NotChecked.Submitted, Is.False);
    });
  }

  /// <summary>
  /// The reputation cell must not read as a feature that is on its way (PRD §70.1, §97).
  /// </summary>
  /// <remarks>
  /// <para>
  /// It used to say <c>n/i</c>, which is this program's word for "this machine could answer and we
  /// have not written it yet" — a promise, over a decision that has been taken and will not be
  /// revisited. §70.1 refuses the provider rather than deferring it, so the cell says the program did
  /// not ask.
  /// </para>
  /// <para>
  /// Asserted against the two wrong answers by name rather than only against the right one, because
  /// the failure this guards is a later change quietly moving the cell back onto one of them: "n/i"
  /// would restore the promise, and an empty cell would let a reader take the silence for a clean
  /// verdict, which is the whole reason the column exists at all.
  /// </para>
  /// </remarks>
  [Test]
  public void AnUnaskedReputationSaysSoRatherThanReadingAsUnbuiltOrAsClean() {
    var record = default(ProcessRecord);
    var shown = FieldAccessor.Text(ProcessField.Reputation, in record, null, 0);

    Assert.Multiple(() => {
      Assert.That(shown, Is.EqualTo("not asked"));
      Assert.That(shown, Is.Not.EqualTo(Humanize.Placeholder(UnknownReason.NotImplementedHere)));
      Assert.That(shown, Is.Not.Empty);
      // An unasked question must sort and filter as nothing at all, so no ordering and no query can
      // group these rows as though a provider had answered for them.
      Assert.That(FieldAccessor.Number(ProcessField.Reputation, in record, null, 0), Is.Null);
      Assert.That(FieldAccessor.RawText(ProcessField.Reputation, in record, null, 0), Is.Null);
    });
  }

  /// <summary>
  /// A chain nobody looked at must not read as one that was looked at and found wanting.
  /// "Unsigned" is a finding; the absence of an answer is not (PRD §72.3).
  /// </summary>
  [Test]
  public void AChainNobodyAskedAboutIsNotAnUnsignedOne() {
    var record = default(ProcessRecord);
    record.TrustChainReason = UnknownReason.NotSampledYet;

    Assert.Multiple(() => {
      Assert.That(record.TrustChain, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(
        FieldAccessor.Text(ProcessField.TrustChain, in record, null, 0),
        Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet))
      );
      Assert.That(FieldAccessor.RawText(ProcessField.TrustChain, in record), Is.Null, "and matches no filter");
      Assert.That(FieldAccessor.Number(ProcessField.TrustChain, in record, null, 0), Is.Null);
    });
  }

  /// <summary>
  /// A packaging system that keeps no record of a signature has not failed to check one, and the
  /// mark it shows is the one that means "no such concept here" rather than "not yet".
  /// </summary>
  [Test]
  public void APackagingSystemWithNoSignatureRecordShowsThatItHasNone() {
    var record = default(ProcessRecord);
    record.TrustChainReason = UnknownReason.NotSupportedOnPlatform;

    Assert.That(
      FieldAccessor.Text(ProcessField.TrustChain, in record, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform))
    );
  }

  /// <summary>
  /// PRD §70 fixes the words. A synonym anywhere would make two front-ends disagree about what the
  /// same verdict is called, which is the thing the list exists to stop.
  /// </summary>
  [Test]
  public void TheVocabularyIsExactlyTheOnePrdSeventyLists() {
    var words = new List<string>();
    foreach (SignatureStatus status in Enum.GetValues<SignatureStatus>())
      words.Add(status.Text());

    Assert.That(words, Is.EqualTo(new[] {
      "Not checked",
      "Verified",
      "Valid but untrusted chain",
      "Unsigned",
      "Invalid signature",
      "Revoked",
      "Expired",
      "Verification error",
    }));
  }

  /// <summary>
  /// Nought is "nobody asked". A default-constructed record must not claim anything was verified,
  /// and it must not claim the file is unsigned either (PRD §72.3).
  /// </summary>
  [Test]
  public void NothingIsAssertedByARecordNobodyFilled() {
    var record = default(ProcessRecord);

    Assert.Multiple(() => {
      Assert.That(record.PackageStatus, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(record.Package.Source, Is.EqualTo(PackageSource.Unknown));
      Assert.That(record.Package.WasChecked, Is.False);
      Assert.That(record.Runtime, Is.EqualTo(ProcessRuntime.Unknown));
    });

    // Not asserted on the record's own default: a struct's default is zeroes, and a zeroed Counter
    // reads as "the value is present" — which is the very trap this fixture is about, and one no
    // amount of care inside ProcessRecord can close. What closes it is the probe filling the field
    // explicitly on every sample, so that is what is checked.
    var filled = default(ProcessRecord);
    filled.ImageCreatedUtcTicks = Counter.NotSampledYet;
    Assert.That(filled.ImageCreatedUtcTicks.HasValue, Is.False);
    Assert.That(
      FieldAccessor.Text(ProcessField.ImageCreated, in filled, null, 0),
      Is.EqualTo(Humanize.Placeholder(UnknownReason.NotSampledYet)),
      "a birth time nobody read is a placeholder, never the first of January in the year one");
  }

  #endregion

  #region helpers

  private static byte[] Gunzip(string path) {
    using var file = File.OpenRead(path);
    using var gzip = new GZipStream(file, CompressionMode.Decompress);
    using var buffer = new MemoryStream();
    gzip.CopyTo(buffer);
    return buffer.ToArray();
  }

  /// <summary>A <c>maps</c> file with one mapping per module named.</summary>
  private static byte[] Maps(params string[] modules) {
    var text = new StringBuilder();
    var address = 0x7f0000000000UL;
    foreach (var module in modules) {
      text.Append($"{address:x}-{address + 0x1000:x} r-xp 00000000 fd:00 1234567                   {module}\n");
      address += 0x100000;
    }

    return Encoding.UTF8.GetBytes(text.ToString());
  }

  #endregion

}

/// <summary>
/// The check itself, over bytes that exist (PRD §70).
/// </summary>
/// <remarks>
/// <para>
/// A file, a database that recorded its digest, and the answers that can come out of comparing
/// them. Built rather than recorded: a recording of somebody else's database describes files that
/// are not on this machine, and a comparison needs a file to read. The formats it is built in are
/// the ones the recorded fixtures prove.
/// </para>
/// <para>
/// Not on Windows, and not because the code is Linux-only — the reader has no platform attribute
/// and the fixture tests above run on every leg. A package database records paths relative to the
/// file system root, and a drive letter is not one, so the pretend machine below cannot be built
/// there without writing something no real database contains.
/// </para>
/// </remarks>
[TestFixture]
[Platform(Exclude = "Win", Reason = "A package database records paths relative to /, which a drive letter is not.")]
public sealed class PackageVerificationTests {

  #region the four answers a comparison can give

  [Test]
  public void AnImageThatMatchesASignedPackageIsVerifiedAndSoIsItsChain() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "pgp", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.Package.Name, Is.EqualTo("thing"));
      Assert.That(trust.Detail, Does.Contain("pacman -Qkk"));
      // Two readings that happen to agree, and each says which one it is.
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.ChainDetail, Does.Contain("PGP"));
      // The last two of §70's five questions, neither of which anything here asks.
      Assert.That(trust.Reputation, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(trust.Submitted, Is.False);
    });
  }

  /// <summary>
  /// The case that used to be reported as one word. A package built on this machine ships files
  /// that match their record exactly and carries nobody's signature; <c>pacman -Qkk</c> counts no
  /// modified files for it and <c>pacman -Qi</c> prints "Validated By: None". Those are two
  /// findings, and folding them into "Unsigned" threw away the one about the file (PRD §70).
  /// </summary>
  [Test]
  public void AnImageThatMatchesAPackageNobodySignedIsVerifiedWithAnUnsignedChain() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "sha256", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified), "the bytes are the ones recorded");
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.Unsigned), "and nobody signed for them");
      Assert.That(trust.ChainDetail, Does.Contain("checksum"));
    });
  }

  [Test]
  public void APackageInstalledWithCheckingOffHasAnUnsignedChain() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "none", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.Unsigned));
      Assert.That(trust.ChainDetail, Does.Contain("turned off"));
    });
  }

  /// <summary>
  /// A database entry that does not say how the package was validated has not answered the chain
  /// question, and answering it as "nobody signed this" would be a finding invented out of a gap.
  /// </summary>
  [Test]
  public void APackageWhoseEntryDoesNotSayHowItWasValidatedIsAChainError() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", validation: null, machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.VerificationError));
    });
  }

  /// <summary>
  /// The other direction, and the reason the two slots are not one: a signature this machine trusts
  /// stands behind the package while the file on disk is no longer the one it shipped.
  /// </summary>
  [Test]
  public void AnImageThatNoLongerMatchesItsPackageSaysSoWithoutTouchingTheChain() {
    using var machine = new PretendMachine();
    var file = machine.WriteFile("usr/bin/thing", "the shipped bytes");
    machine.Install("thing", "1.0-1", "pgp", file);
    File.WriteAllText(file, "something else entirely");

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.InvalidSignature));
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.Verified), "the package is still the signed one");
      Assert.That(trust.Package.Name, Is.EqualTo("thing"), "it is still that package's path");
    });
  }

  /// <summary>
  /// PRD §3 promises nothing about an executable is transmitted without being asked, and §70 keeps
  /// submission as its own question so that hashing can never be read as uploading. No path through
  /// this reader sets it, and this is what says so.
  /// </summary>
  [Test]
  public void NoAnswerThisReaderCanProduceEverClaimsAFileWasSubmitted() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "pgp", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    Assert.Multiple(() => {
      Assert.That(machine.Check("usr/bin/thing").Submitted, Is.False);
      Assert.That(machine.Check("usr/bin/thing", verify: false).Submitted, Is.False);
      Assert.That(machine.Check("usr/bin/thing").Reputation, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(ImageTrust.NotChecked.Reputation, Is.EqualTo(SignatureStatus.NotChecked));
    });
  }

  /// <summary>
  /// The chain is read out of the package's own entry and needs no hash of anything, so a run that
  /// asked only who owns a file has already paid for it (PRD §5.4).
  /// </summary>
  [Test]
  public void TheChainIsAnsweredWithoutHashingTheImage() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "pgp", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing", verify: false);

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.NotChecked), "nobody asked for the file check");
      Assert.That(trust.Sha256, Is.Null, "and so nothing was hashed");
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.Verified));
    });
  }

  /// <summary>
  /// A package whose manifest has no entry for the path cannot be compared, and that is a failure to
  /// check rather than a check that passed.
  /// </summary>
  [Test]
  public void APathWithNoRecordedDigestIsAVerificationError() {
    using var machine = new PretendMachine();
    var file = machine.WriteFile("usr/bin/thing", "the shipped bytes");
    machine.Install("thing", "1.0-1", "pgp", file, recordDigest: false);

    Assert.That(machine.Check("usr/bin/thing").Signature, Is.EqualTo(SignatureStatus.VerificationError));
  }

  /// <summary>
  /// The Debian half, over a real file and a real MD5 of it. The chain has no answer here at all —
  /// <c>dpkg</c> records nothing about a signature over an installed file — and no answer is not
  /// the answer "nothing signed it" (PRD §72.3).
  /// </summary>
  [Test]
  public void ADebianImageThatMatchesItsRecordedDigestIsVerifiedWithNoChainToAsk() {
    using var machine = new PretendMachine();
    machine.InstallDebian("thing", "1.0", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Package.Source, Is.EqualTo(PackageSource.Dpkg));
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.Detail, Does.Contain("dpkg --verify"));
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(trust.ChainReason, Is.EqualTo(UnknownReason.NotSupportedOnPlatform));
      Assert.That(trust.ChainDetail, Does.Contain("dpkg keeps no record"));
    });
  }

  [Test]
  public void ADebianImageThatHasChangedSaysSo() {
    using var machine = new PretendMachine();
    var file = machine.WriteFile("usr/bin/thing", "the shipped bytes");
    machine.InstallDebian("thing", "1.0", file);
    File.WriteAllText(file, "something else entirely");

    Assert.That(machine.Check("usr/bin/thing").Signature, Is.EqualTo(SignatureStatus.InvalidSignature));
  }

  #endregion

  #region the pretend machine

  /// <summary>
  /// A temporary directory holding both a file system and the package database that describes it.
  /// </summary>
  private sealed class PretendMachine : IDisposable {

    private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      "procman-packages-" + Guid.NewGuid().ToString("N")[..8]
    );

    private string DatabaseRoot => Path.Combine(this._root, "var", "lib");

    /// <summary>Writes a file and hands back its absolute path.</summary>
    public string WriteFile(string relativePath, string content) {
      var path = Path.Combine(this._root, relativePath.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, content);
      return path;
    }

    /// <summary>Records the file the way <c>libalpm</c> records one.</summary>
    /// <param name="validation">
    /// What <c>%VALIDATION%</c> says, or <see langword="null"/> to leave the line out altogether —
    /// which is a database entry that does not say, and a different case from one that says none.
    /// </param>
    public void Install(string name, string version, string? validation, string file, bool recordDigest = true) {
      var directory = Path.Combine(this.DatabaseRoot, "pacman", "local", $"{name}-{version}");
      Directory.CreateDirectory(directory);
      var relative = Relative(file);

      File.WriteAllText(
        Path.Combine(directory, "desc"),
        $"%NAME%\n{name}\n\n%VERSION%\n{version}\n"
        + (validation is null ? string.Empty : $"\n%VALIDATION%\n{validation}\n")
      );

      File.WriteAllText(Path.Combine(directory, "files"), $"%FILES%\n{relative}\n");

      var manifest = new StringBuilder("#mtree\n/set type=file uid=0 gid=0 mode=644\n");
      if (recordDigest)
        manifest.Append($"./{relative} time=1.0 size={new FileInfo(file).Length} sha256digest={Sha256(file)}\n");
      else
        manifest.Append($"./{relative} time=1.0 type=link link=elsewhere\n");

      using var gzip = new GZipStream(File.Create(Path.Combine(directory, "mtree")), CompressionMode.Compress);
      gzip.Write(Encoding.UTF8.GetBytes(manifest.ToString()));
    }

    /// <summary>Records the file the way <c>dpkg</c> records one.</summary>
    public void InstallDebian(string name, string version, string file) {
      var info = Path.Combine(this.DatabaseRoot, "dpkg", "info");
      Directory.CreateDirectory(info);
      var relative = Relative(file);

      File.WriteAllText(Path.Combine(info, name + ".list"), $"/.\n/{relative}\n");
      File.WriteAllText(Path.Combine(info, name + ".md5sums"), $"{Md5(file)}  {relative}\n");
      File.WriteAllText(
        Path.Combine(this.DatabaseRoot, "dpkg", "status"),
        $"Package: {name}\nStatus: install ok installed\nVersion: {version}\n\n"
      );
    }

    /// <summary>Asks the reader about one of the installed files, hashing it the way the probe does.</summary>
    /// <param name="verify">
    /// False to ask only who owns the file, the way a run that named the package column and not the
    /// check does. Nothing is hashed then, and the digest handed over is the empty one the probe
    /// passes in that case.
    /// </param>
    public ImageTrust Check(string relativePath, bool verify = true) {
      var path = Path.Combine(this._root, relativePath.Replace('/', Path.DirectorySeparatorChar));
      var info = new FileInfo(path);
      return new PackageDatabaseReader(this.DatabaseRoot).Describe(
        path,
        info.Length,
        info.LastWriteTimeUtc.Ticks,
        verify ? FileDigest.Of(path) : default,
        verify
      );
    }

    /// <summary>
    /// The path as a package database writes it: relative to the file system root, which means the
    /// whole absolute path less its leading slash — <c>usr/bin/which</c> and not <c>/usr/bin/which</c>.
    /// </summary>
    private static string Relative(string file) => file.TrimStart('/');

    private static string Sha256(string path) {
      using var stream = File.OpenRead(path);
      return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string Md5(string path) {
      using var stream = File.OpenRead(path);
      return Convert.ToHexStringLower(MD5.HashData(stream));
    }

    public void Dispose() {
      try {
        Directory.Delete(this._root, recursive: true);
      } catch (IOException) {
      } catch (UnauthorizedAccessException) {
      }
    }

  }

  #endregion

}
