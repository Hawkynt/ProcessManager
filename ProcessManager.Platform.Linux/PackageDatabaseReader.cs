using System.IO.Compression;
using System.Text;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// Which package a file on this machine belongs to, and whether it is still the file that package
/// shipped (PRD §14, §70).
/// </summary>
/// <remarks>
/// <para>
/// An ELF carries no signature to check — there is nothing inside it for a verifier to verify. What
/// signs a program on Linux is the package it came in, so the honest equivalent of Authenticode here
/// is the question <c>pacman -Qkk</c> and <c>dpkg --verify</c> ask: does this file still match the
/// digest its package recorded, and was that package itself validated when it was installed. This
/// reads the two databases directly rather than running either program.
/// </para>
/// <para>
/// The index is the expensive part and is built once. Arch's database is a file list per installed
/// package — 564,000 paths across 1,300 packages on the machine this was written on, thirty
/// megabytes of text — and the only affordable way to ask "who owns this one path" repeatedly is to
/// read all of it once and remember a hash of each path. Hashes rather than the paths themselves:
/// the strings would be sixty megabytes to hold for an answer about two hundred of them. A hash that
/// collides costs nothing but a wrong candidate, because the candidate is confirmed against the
/// package's own file list before anything is claimed (PRD §5.4).
/// </para>
/// <para>
/// Held against <c>pacman -Qo</c> and <c>pacman -Qkk</c> on the machine it was written on. The
/// <c>dpkg</c> half is replayed from a recorded database and has never met a live <c>dpkg</c>; the
/// <c>rpm</c> databases are not read at all, because since Fedora 33 they are an SQLite file whose
/// schema rpm.org documents as an implementation detail reachable only through <c>librpm</c> — a
/// parser for it would be a guess wearing a reader's clothes (PRD §9.2).
/// </para>
/// </remarks>
internal sealed class PackageDatabaseReader {

  private const string _PACMAN_LOCAL = "pacman/local";
  private const string _DPKG_INFO = "dpkg/info";
  private const string _DPKG_STATUS = "dpkg/status";
  private const string _LIST = ".list";
  private const string _MD5SUMS = ".md5sums";

  private readonly string _root;

  /// <summary>Hash of an owned path, without its leading slash, to the slot that owns it.</summary>
  private readonly Dictionary<ulong, int> _owners = [];

  /// <summary>Where each slot's database entry is: a pacman directory, or a dpkg <c>.list</c>.</summary>
  private readonly List<string> _slots = [];
  private readonly List<PackageSource> _slotSources = [];

  /// <summary>
  /// One answer per image, keyed by what the file was when it was read.
  /// </summary>
  /// <remarks>
  /// The same key as the digest cache and for the same reason: three hundred processes of one
  /// runtime ask this question about one file, and a file replaced underneath them must be asked
  /// again rather than answered from a record of bytes that are no longer there.
  /// </remarks>
  private readonly Dictionary<(string Path, long Size, long Modified), ImageTrust> _answers = [];

  private bool _indexed;

  public PackageDatabaseReader(string root) => this._root = root.TrimEnd('/');

  /// <summary>
  /// What claims this image, and what the claim is worth.
  /// </summary>
  /// <param name="digest">
  /// The image's SHA-256, from the one read of it the hash columns already paid for. Without it
  /// nothing can be compared, and this says so rather than reporting a match it did not make.
  /// </param>
  /// <param name="verify">
  /// False to ask only who owns the file. The check is a separate question and a dearer one, and
  /// somebody who asked for the package column has not asked for it (PRD §5.4, §70).
  /// </param>
  public ImageTrust Describe(string path, long size, long modified, in FileDigest digest, bool verify) {
    var key = (path, size, modified);
    if (this._answers.TryGetValue(key, out var known))
      return known;

    var answer = this.Read(path, digest, verify);
    this._answers[key] = answer;
    return answer;
  }

  /// <summary>
  /// The two questions a package database answers about one file, kept apart (PRD §70).
  /// </summary>
  /// <remarks>
  /// One struct rather than one status because the two answers disagree constantly and each is
  /// worth reading on its own. A package built on this machine ships files that match their record
  /// exactly and carries nobody's signature; a package from a mirror may be signed by a key this
  /// machine trusts and have had one of its files overwritten since. Reporting either case in one
  /// word loses the half that matters.
  /// </remarks>
  /// <param name="ChainReason">
  /// Why <paramref name="Chain"/> is <see cref="SignatureStatus.NotChecked"/>, when it is. A
  /// database that has no concept of a signature over an installed file is not a check that failed.
  /// </param>
  private readonly record struct Verdict(
    SignatureStatus Signature,
    string? Detail,
    SignatureStatus Chain,
    string? ChainDetail,
    UnknownReason ChainReason
  );

  private ImageTrust Read(string path, in FileDigest digest, bool verify) {
    var owner = this.Owner(path, out var slot);
    // What the package says it is and who assembled it, from the entry the ownership lookup has
    // already opened. §31 asks a module for a description and a company and an ELF has neither, so
    // this is what a Linux machine can answer with (PRD §5.3, §31).
    var (summary, publisher) = slot >= 0 ? this.Publisher(slot) : (null, null);

    // Nothing claims the file. That is one answer to both questions and not a failure of either:
    // there is no package to compare the bytes against and none to have signed them.
    if (owner.Source == PackageSource.None)
      return Trust(digest, owner, WithFile(
        Chain(
          SignatureStatus.Unsigned,
          "nothing claims the file, so there is no package behind it whose signature could be followed"
        ),
        verify,
        SignatureStatus.Unsigned,
        "no packaging system on this machine claims this file, so nothing here vouches for its bytes"
      ), summary, publisher);

    if (!owner.WasChecked)
      return Trust(digest, owner, WithFile(
        Chain(SignatureStatus.VerificationError, "the package databases could not be read"),
        verify,
        SignatureStatus.VerificationError,
        "the package databases could not be read"
      ), summary, publisher);

    var pacman = this._slotSources[slot] == PackageSource.Pacman;
    var chain = pacman ? PacmanChain(this._slots[slot]) : _dpkgChain;

    // The chain is a reading of the package's own database entry and needs no hash of anything, so
    // it is answered whenever the owner is: it costs one more small file per image, against an
    // index that cost thirty megabytes to build. The file comparison is the dear half — it hashes
    // the image — and waits until somebody asks for it (PRD §5.4).
    return Trust(digest, owner, verify
      ? pacman
        ? VerifyPacman(this._slots[slot], path, digest, chain)
        : VerifyDpkg(this._slots[slot], path, chain)
      : chain, summary, publisher);
  }

  /// <summary>
  /// The one-line description and the packager, out of the entry a slot names (PRD §31).
  /// </summary>
  /// <remarks>
  /// <c>pacman</c> keeps both in the <c>desc</c> file the version came from, so this is a re-read of
  /// a file that has just been read and is served by the page cache. <c>dpkg</c> keeps them in
  /// <c>status</c>, which is one file for the whole machine and is walked once per lookup — the same
  /// walk the version already costs, and the reason both fields are taken in a single pass over it.
  /// </remarks>
  private (string? Summary, string? Publisher) Publisher(int slot) {
    var entry = this._slots[slot];
    if (this._slotSources[slot] == PackageSource.Pacman) {
      if (!TryRead(Path.Combine(entry, "desc"), out var desc))
        return (null, null);

      var description = PacmanLocalDatabase.ReadDescription(desc);
      return (description.Summary, description.Packager);
    }

    var package = DpkgDatabase.PackageOf(Path.GetFileName(entry), _LIST);
    if (package is null || !TryRead(Path.Combine(this._root, _DPKG_STATUS), out var status))
      return (null, null);

    var stanza = DpkgDatabase.FindStanza(status, package);
    return (stanza.Summary, stanza.Maintainer);
  }

  /// <summary>
  /// One answer, out of a verdict and out of what the package says about itself (PRD §31, §70).
  /// </summary>
  /// <remarks>
  /// The two travel together and are asked for separately: a verdict costs a hash of the image and
  /// the description costs a small file already in the page cache, so one of them is behind a
  /// button and the other is not (PRD §5.4).
  /// </remarks>
  private static ImageTrust Trust(
    in FileDigest digest,
    PackageIdentity owner,
    in Verdict verdict,
    string? summary,
    string? publisher
  ) => new(
    digest.Sha256,
    owner,
    verdict.Signature,
    verdict.Detail,
    verdict.Chain,
    verdict.ChainDetail,
    verdict.ChainReason,
    Summary: summary,
    Publisher: publisher
  );

  /// <summary>Which package owns a path, confirmed against that package's own list.</summary>
  private PackageIdentity Owner(string path, out int slot) {
    slot = -1;
    if (!this.EnsureIndex())
      return PackageIdentity.Unknown(UnknownReason.NotPermitted);

    var relative = Encoding.UTF8.GetBytes(path.TrimStart('/'));
    if (!this._owners.TryGetValue(Hash(relative), out var candidate))
      return PackageIdentity.NotPackaged;

    var identity = this.Confirm(candidate, relative);
    if (identity.WasChecked && identity.Source != PackageSource.None) {
      slot = candidate;
      return identity;
    }

    // The candidate did not own it after all: two paths whose hashes collided, or a database that
    // has changed under us since the index was built. Rare enough to answer the slow way rather
    // than to answer wrongly.
    for (var i = 0; i < this._slots.Count; ++i) {
      if (i == candidate)
        continue;

      var confirmed = this.Confirm(i, relative);
      if (!confirmed.WasChecked || confirmed.Source == PackageSource.None)
        continue;

      slot = i;
      return confirmed;
    }

    return PackageIdentity.NotPackaged;
  }

  private PackageIdentity Confirm(int slot, ReadOnlySpan<byte> relative) {
    var entry = this._slots[slot];
    if (this._slotSources[slot] == PackageSource.Pacman) {
      if (!TryRead(Path.Combine(entry, "files"), out var files) || !PacmanLocalDatabase.Owns(files, relative))
        return PackageIdentity.NotPackaged;

      if (!TryRead(Path.Combine(entry, "desc"), out var desc))
        return PackageIdentity.Unknown(UnknownReason.NotPermitted);

      var description = PacmanLocalDatabase.ReadDescription(desc);
      return new(PackageSource.Pacman, description.Name, description.Version, null, UnknownReason.None);
    }

    if (!TryRead(entry, out var list))
      return PackageIdentity.NotPackaged;

    var absolute = new byte[relative.Length + 1];
    absolute[0] = (byte)'/';
    relative.CopyTo(absolute.AsSpan(1));

    var owns = false;
    foreach (var owned in DpkgDatabase.Paths(list))
      if (owned.SequenceEqual(absolute)) {
        owns = true;
        break;
      }

    if (!owns)
      return PackageIdentity.NotPackaged;

    var package = DpkgDatabase.PackageOf(Path.GetFileName(entry), _LIST);
    return new(
      PackageSource.Dpkg,
      package,
      package is null ? null : this.DpkgVersion(package),
      null,
      UnknownReason.None
    );
  }

  private string? DpkgVersion(string package)
    => TryRead(Path.Combine(this._root, _DPKG_STATUS), out var status)
      ? DpkgDatabase.FindVersion(status, package)
      : null;

  /// <summary>
  /// Compares the file against the digest <c>pacman</c> recorded for it, and reads separately what
  /// stood behind the package when it was installed.
  /// </summary>
  /// <remarks>
  /// Two readings and two verdicts, which is the change §70's first requirement asked for.
  /// <c>mtree</c> says what the bytes were when the package was installed; <c>%VALIDATION%</c> says
  /// whether a PGP signature was checked before they were let on to the machine. They are the two
  /// halves <c>pacman</c> itself reports apart — <c>pacman -Qkk</c> counts modified files and
  /// <c>pacman -Qi</c> prints "Validated By" — and folding them into one word made this program
  /// report a locally built package whose files were untouched as "Unsigned", which is a finding
  /// about the packager smuggled into a column about the file.
  /// </remarks>
  private static Verdict VerifyPacman(string directory, string path, in FileDigest digest, in Verdict chain) {
    if (digest.Sha256 is not { Length: > 0 } sha256)
      return Unchecked(digest.Reason ?? "the image could not be read to hash it", chain);

    if (!TryReadGzip(Path.Combine(directory, "mtree"), out var mtree))
      return Unchecked("the package records no file digests to compare against", chain);

    var relative = Encoding.UTF8.GetBytes(path.TrimStart('/'));
    if (!PacmanLocalDatabase.TryFindEntry(mtree, relative, out var entry) || entry.Sha256 is not { } recorded)
      return Unchecked("the package's manifest has no digest for this path", chain);

    return string.Equals(recorded, sha256, StringComparison.OrdinalIgnoreCase)
      ? new(
        SignatureStatus.Verified,
        "the running image is byte for byte the file its package recorded at this path, which is the comparison pacman -Qkk makes",
        chain.Chain,
        chain.ChainDetail,
        chain.ChainReason
      )
      : new(
        SignatureStatus.InvalidSignature,
        "the running image no longer matches the digest its package recorded for this path",
        chain.Chain,
        chain.ChainDetail,
        chain.ChainReason
      );
  }

  /// <summary>
  /// What stood behind the package itself, out of <c>%VALIDATION%</c>.
  /// </summary>
  /// <remarks>
  /// The only signature record on this machine, and a record of a check rather than a signature:
  /// <c>pacman</c> verified the archive's PGP signature against the keyring at install time and
  /// remembered that it did, while the signature went away with the archive. So this can say the
  /// chain was followed and cannot say by which key — and it can never report a key that has since
  /// expired or been withdrawn, because nothing here names one to ask about (PRD §70, §9.2).
  /// </remarks>
  private static Verdict PacmanChain(string directory) {
    if (!TryRead(Path.Combine(directory, "desc"), out var desc))
      return Chain(
        SignatureStatus.VerificationError,
        "the package's own database entry could not be read, so nothing here says what stood behind it"
      );

    return PacmanLocalDatabase.ReadDescription(desc).Validation switch {
      PacmanLocalDatabase.Validation.Signature => Chain(
        SignatureStatus.Verified,
        "the package's PGP signature was verified against this machine's keyring when it was installed, which is what pacman -Qi prints as \"Validated By: Signature\""
      ),
      PacmanLocalDatabase.Validation.Checksum => Chain(
        SignatureStatus.Unsigned,
        "the package was installed on a checksum of its archive and nobody signed it, so no key stands behind these bytes"
      ),
      PacmanLocalDatabase.Validation.None => Chain(
        SignatureStatus.Unsigned,
        "the package was installed with signature checking turned off, so no key stands behind these bytes"
      ),
      _ => Chain(
        SignatureStatus.VerificationError,
        "the package's database entry does not record how it was validated when it was installed"
      ),
    };
  }

  /// <summary>
  /// Compares the file against the MD5 <c>dpkg</c> recorded for it.
  /// </summary>
  /// <remarks>
  /// The chain half has no answer at all here, and that is not the same as answering that nothing
  /// signed the package. <c>dpkg</c> keeps no record of a signature over an installed file: Debian
  /// signs the release a package was fetched through and none of that survives installation, so the
  /// question has no place to be asked rather than a negative answer (PRD §72.3).
  /// </remarks>
  private static Verdict VerifyDpkg(string listPath, string path, in Verdict chain) {
    var sums = listPath[..^_LIST.Length] + _MD5SUMS;
    if (!TryRead(sums, out var content))
      return Unchecked("the package records no file digests to compare against", chain);

    var recorded = DpkgDatabase.FindMd5(content, Encoding.UTF8.GetBytes(path.TrimStart('/')));
    if (recorded is null)
      return Unchecked("the package's digest list has no entry for this path", chain);

    var actual = FileDigest.Md5Of(path);
    if (actual is null)
      return Unchecked("the image could not be read to hash it", chain);

    return string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase)
      ? new(
        SignatureStatus.Verified,
        "the running image matches the MD5 dpkg recorded for it, which is the comparison dpkg --verify makes",
        chain.Chain,
        chain.ChainDetail,
        chain.ChainReason
      )
      : new(
        SignatureStatus.InvalidSignature,
        "the running image no longer matches the digest dpkg recorded for this path",
        chain.Chain,
        chain.ChainDetail,
        chain.ChainReason
      );
  }

  /// <summary>
  /// What <c>dpkg</c> can say about a chain, which is nothing at all (PRD §72.3).
  /// </summary>
  private static readonly Verdict _dpkgChain = Chain(
    SignatureStatus.NotChecked,
    "dpkg keeps no record of a signature over an installed file: Debian signs the release a package was fetched through, and none of that survives into the installed database",
    UnknownReason.NotSupportedOnPlatform
  );

  /// <summary>A chain answer on its own, before there is a file verdict to go with it.</summary>
  private static Verdict Chain(SignatureStatus status, string detail, UnknownReason reason = UnknownReason.None)
    => new(SignatureStatus.NotChecked, null, status, detail, reason);

  /// <summary>The same chain answer, with the file verdict filled in when somebody asked for one.</summary>
  private static Verdict WithFile(in Verdict chain, bool verify, SignatureStatus status, string detail)
    => verify ? chain with { Signature = status, Detail = detail } : chain;

  /// <summary>
  /// The file could not be compared, which is a failure to check and never a check that passed. The
  /// chain answer survives it: what stood behind the package is a separate reading and is still
  /// known.
  /// </summary>
  private static Verdict Unchecked(string detail, in Verdict chain)
    => new(SignatureStatus.VerificationError, detail, chain.Chain, chain.ChainDetail, chain.ChainReason);

  /// <summary>
  /// Reads every installed package's file list, once.
  /// </summary>
  /// <returns>False when neither database is there to read, which is not a failure — it is a machine
  /// whose packages are managed by something this program cannot ask.</returns>
  private bool EnsureIndex() {
    if (this._indexed)
      return this._slots.Count > 0;

    this._indexed = true;
    this.IndexPacman();
    this.IndexDpkg();
    return this._slots.Count > 0;
  }

  private void IndexPacman() {
    var local = Path.Combine(this._root, _PACMAN_LOCAL);
    if (!Directory.Exists(local))
      return;

    foreach (var directory in Directory.EnumerateDirectories(local)) {
      if (!TryRead(Path.Combine(directory, "files"), out var files))
        continue;

      var slot = this.AddSlot(directory, PackageSource.Pacman);
      foreach (var path in PacmanLocalDatabase.Paths(files))
        this._owners[Hash(path)] = slot;
    }
  }

  private void IndexDpkg() {
    var info = Path.Combine(this._root, _DPKG_INFO);
    if (!Directory.Exists(info))
      return;

    foreach (var file in Directory.EnumerateFiles(info, "*" + _LIST)) {
      if (!TryRead(file, out var list))
        continue;

      var slot = this.AddSlot(file, PackageSource.Dpkg);
      foreach (var path in DpkgDatabase.Paths(list))
        // Indexed without the leading slash, so that one lookup answers for both databases.
        this._owners[Hash(path[1..])] = slot;
    }
  }

  private int AddSlot(string entry, PackageSource source) {
    this._slots.Add(entry);
    this._slotSources.Add(source);
    return this._slots.Count - 1;
  }

  /// <summary>FNV-1a, because the index needs a spread and not a defence.</summary>
  /// <remarks>
  /// Nothing rests on this hash: a collision produces a candidate that fails to confirm and the
  /// lookup falls back to reading the lists. So the requirement is that it be fast over half a
  /// million short strings, which this is.
  /// </remarks>
  private static ulong Hash(ReadOnlySpan<byte> path) {
    var hash = 14695981039346656037UL;
    foreach (var b in path) {
      hash ^= b;
      hash *= 1099511628211UL;
    }

    return hash;
  }

  private static bool TryRead(string path, out byte[] content) {
    try {
      content = File.ReadAllBytes(path);
      return true;
    } catch (IOException) {
      content = [];
      return false;
    } catch (UnauthorizedAccessException) {
      content = [];
      return false;
    }
  }

  /// <summary>
  /// <c>mtree</c> is gzipped, which is the one thing in either database that is not text.
  /// </summary>
  private static bool TryReadGzip(string path, out byte[] content) {
    try {
      using var file = File.OpenRead(path);
      using var gzip = new GZipStream(file, CompressionMode.Decompress);
      using var buffer = new MemoryStream();
      gzip.CopyTo(buffer);
      content = buffer.ToArray();
      return true;
    } catch (IOException) {
      content = [];
      return false;
    } catch (InvalidDataException) {
      content = [];
      return false;
    } catch (UnauthorizedAccessException) {
      content = [];
      return false;
    }
  }

}
