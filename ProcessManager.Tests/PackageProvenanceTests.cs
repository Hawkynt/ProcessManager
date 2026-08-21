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
  public void AnImageThatMatchesASignedPackageIsVerified() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "pgp", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Verified));
      Assert.That(trust.Package.Name, Is.EqualTo("thing"));
      Assert.That(trust.Detail, Does.Contain("PGP"));
      // The trust chain and the reputation are separate questions and neither was asked (PRD §70).
      Assert.That(trust.TrustChain, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(trust.Reputation, Is.EqualTo(SignatureStatus.NotChecked));
      Assert.That(trust.Submitted, Is.False);
    });
  }

  [Test]
  public void AnImageThatMatchesAnUnsignedPackageIsUnsignedAndNotVerified() {
    using var machine = new PretendMachine();
    machine.Install("thing", "1.0-1", "sha256", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      // The bytes are the ones the package recorded and nobody signed the package: a match is not a
      // signature, and calling it "Verified" would be a verdict about one that never existed.
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Unsigned));
      Assert.That(trust.Detail, Does.Contain("checksum"));
    });
  }

  [Test]
  public void AnImageThatNoLongerMatchesItsPackageSaysSo() {
    using var machine = new PretendMachine();
    var file = machine.WriteFile("usr/bin/thing", "the shipped bytes");
    machine.Install("thing", "1.0-1", "pgp", file);
    File.WriteAllText(file, "something else entirely");

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.InvalidSignature));
      Assert.That(trust.Package.Name, Is.EqualTo("thing"), "it is still that package's path");
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
  /// The Debian half, over a real file and a real MD5 of it — never "Verified", because dpkg keeps
  /// no record that anything was signed.
  /// </summary>
  [Test]
  public void ADebianImageThatMatchesItsRecordedDigestIsUnsigned() {
    using var machine = new PretendMachine();
    machine.InstallDebian("thing", "1.0", machine.WriteFile("usr/bin/thing", "the shipped bytes"));

    var trust = machine.Check("usr/bin/thing");

    Assert.Multiple(() => {
      Assert.That(trust.Package.Source, Is.EqualTo(PackageSource.Dpkg));
      Assert.That(trust.Signature, Is.EqualTo(SignatureStatus.Unsigned));
      Assert.That(trust.Detail, Does.Contain("dpkg"));
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
    public void Install(string name, string version, string validation, string file, bool recordDigest = true) {
      var directory = Path.Combine(this.DatabaseRoot, "pacman", "local", $"{name}-{version}");
      Directory.CreateDirectory(directory);
      var relative = Relative(file);

      File.WriteAllText(
        Path.Combine(directory, "desc"),
        $"%NAME%\n{name}\n\n%VERSION%\n{version}\n\n%VALIDATION%\n{validation}\n"
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
    public ImageTrust Check(string relativePath) {
      var path = Path.Combine(this._root, relativePath.Replace('/', Path.DirectorySeparatorChar));
      var info = new FileInfo(path);
      return new PackageDatabaseReader(this.DatabaseRoot).Describe(
        path,
        info.Length,
        info.LastWriteTimeUtc.Ticks,
        FileDigest.Of(path),
        verify: true
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
