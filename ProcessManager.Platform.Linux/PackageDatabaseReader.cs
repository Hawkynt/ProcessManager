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

  private ImageTrust Read(string path, in FileDigest digest, bool verify) {
    var owner = this.Owner(path, out var slot);
    if (!verify)
      return new(digest.Sha256, owner, SignatureStatus.NotChecked, null);

    if (owner.Source == PackageSource.None)
      return new(
        digest.Sha256,
        owner,
        SignatureStatus.Unsigned,
        "no packaging system on this machine claims this file, so nothing here vouches for its bytes"
      );

    if (!owner.WasChecked)
      return new(digest.Sha256, owner, SignatureStatus.VerificationError, "the package databases could not be read");

    var (status, detail) = this._slotSources[slot] == PackageSource.Pacman
      ? VerifyPacman(this._slots[slot], path, digest)
      : VerifyDpkg(this._slots[slot], path);

    return new(digest.Sha256, owner, status, detail);
  }

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
  /// Compares the file against the digest <c>pacman</c> recorded for it.
  /// </summary>
  /// <remarks>
  /// Two readings and one verdict. <c>mtree</c> says what the bytes were when the package was
  /// installed, and <c>%VALIDATION%</c> says whether a PGP signature was checked before they were
  /// let on to the machine. A file that matches a package nobody signed is exactly as unsigned as
  /// one that came from nowhere, and saying "Verified" of it would be a verdict about a signature
  /// that never existed (PRD §70).
  /// </remarks>
  private static (SignatureStatus Status, string? Detail) VerifyPacman(string directory, string path, in FileDigest digest) {
    if (digest.Sha256 is not { Length: > 0 } sha256)
      return (SignatureStatus.VerificationError, digest.Reason ?? "the image could not be read to hash it");

    if (!TryReadGzip(Path.Combine(directory, "mtree"), out var mtree))
      return (SignatureStatus.VerificationError, "the package records no file digests to compare against");

    var relative = Encoding.UTF8.GetBytes(path.TrimStart('/'));
    if (!PacmanLocalDatabase.TryFindEntry(mtree, relative, out var entry) || entry.Sha256 is not { } recorded)
      return (SignatureStatus.VerificationError, "the package's manifest has no digest for this path");

    var description = TryRead(Path.Combine(directory, "desc"), out var desc)
      ? PacmanLocalDatabase.ReadDescription(desc)
      : default;

    if (!string.Equals(recorded, sha256, StringComparison.OrdinalIgnoreCase))
      return (
        SignatureStatus.InvalidSignature,
        "the running image no longer matches the digest its package recorded for this path"
      );

    return description.Validation switch {
      PacmanLocalDatabase.Validation.Signature => (
        SignatureStatus.Verified,
        "the image matches the digest its package recorded, and that package's PGP signature was verified when it was installed"
      ),
      PacmanLocalDatabase.Validation.Checksum => (
        SignatureStatus.Unsigned,
        "the image matches the digest its package recorded; the package itself was installed on a checksum, with nobody's signature on it"
      ),
      PacmanLocalDatabase.Validation.None => (
        SignatureStatus.Unsigned,
        "the image matches the digest its package recorded; the package was installed with signature checking turned off"
      ),
      _ => (
        SignatureStatus.Unsigned,
        "the image matches the digest its package recorded; the database does not say whether that package was signed"
      ),
    };
  }

  /// <summary>
  /// Compares the file against the MD5 <c>dpkg</c> recorded for it.
  /// </summary>
  /// <remarks>
  /// Never "Verified": <c>dpkg</c> keeps no record that anything was signed. Debian signs the
  /// release files a package was fetched through, and none of that survives into the installed
  /// database — so the strongest true statement here is that the file is the one that was installed,
  /// and that is not a signature (PRD §70).
  /// </remarks>
  private static (SignatureStatus Status, string? Detail) VerifyDpkg(string listPath, string path) {
    var sums = listPath[..^_LIST.Length] + _MD5SUMS;
    if (!TryRead(sums, out var content))
      return (SignatureStatus.VerificationError, "the package records no file digests to compare against");

    var recorded = DpkgDatabase.FindMd5(content, Encoding.UTF8.GetBytes(path.TrimStart('/')));
    if (recorded is null)
      return (SignatureStatus.VerificationError, "the package's digest list has no entry for this path");

    var actual = FileDigest.Md5Of(path);
    if (actual is null)
      return (SignatureStatus.VerificationError, "the image could not be read to hash it");

    return string.Equals(recorded, actual, StringComparison.OrdinalIgnoreCase)
      ? (
        SignatureStatus.Unsigned,
        "the image matches the digest dpkg recorded for it; dpkg keeps no record of a signature over the installed file"
      )
      : (
        SignatureStatus.InvalidSignature,
        "the running image no longer matches the digest dpkg recorded for this path"
      );
  }

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
