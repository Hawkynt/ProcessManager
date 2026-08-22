using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What each running image says about itself, read once per image (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Per <em>image</em> and not per process, which is the whole reason this class exists: three
/// hundred processes of one runtime share one binary, and reading it three hundred times would be
/// three hundred file reads for one answer. The cache is keyed on the path and is invalidated by the
/// file's own size and modification time, so an image replaced underneath a running process is read
/// again rather than reported as it used to be — which is the case somebody watching a version
/// column is watching for (PRD §5.4).
/// </para>
/// <para>
/// Not marked as Windows-only, and it calls nothing that is: it opens a file and hands the bytes to
/// <see cref="PortableExecutable"/>. That is what lets the whole of this be exercised on the Linux
/// leg against real PE images, which is where it was actually tested (PRD §9.4).
/// </para>
/// </remarks>
internal sealed class WindowsImageReader {

  /// <summary>
  /// A cache entry, with what it was read from so a replaced file can be noticed.
  /// </summary>
  /// <param name="Facts">
  /// Null when the file exists and is not a PE image, which is a different answer from not having
  /// looked and is cached just as firmly — otherwise every sample would re-read it.
  /// </param>
  /// <param name="Signature">
  /// Null both when nobody asked for it and when the file is not a PE image at all. The two are told
  /// apart by <paramref name="Facts"/> beside it, which is null only in the second case — so a
  /// cached entry taken before anything wanted a signature can be topped up rather than answering
  /// "unsigned" for the rest of the run (PRD §72.3).
  /// </param>
  private readonly record struct Entry(
    long Length,
    long ModifiedTicks,
    PeImageFacts? Facts,
    AuthenticodeFacts? Signature,
    UnknownReason Reason
  );

  private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Refuses to read anything larger than this, because the answer is in the first few hundred
  /// kilobytes and a single-file publish can be hundreds of megabytes.
  /// </summary>
  /// <remarks>
  /// The whole file is read rather than seeking about in it: the resource section is at the end as
  /// often as not, and a version resource sits behind a section table whose offsets can point
  /// anywhere. Sixty-four megabytes covers every ordinary program and refuses the handful that would
  /// stall a sample; those say why rather than being read halfway (PRD §72.3).
  /// </remarks>
  private const long _MaximumBytes = 64L * 1024 * 1024;

  /// <summary>
  /// What the image at <paramref name="path"/> says about itself, or why there is no answer.
  /// </summary>
  /// <param name="wantSignature">
  /// Whether to check the image's own signature as well (PRD §21, §5.4). Dearer than everything else
  /// here by a wide margin — it digests the whole file and verifies a signature over that digest —
  /// so it happens only when a column or a filter names one of the five columns behind it, and only
  /// once per image however many processes are running it.
  /// </param>
  public PeImageFacts? Read(string? path, bool wantSignature, out AuthenticodeFacts? signature, out UnknownReason reason) {
    signature = null;
    reason = UnknownReason.None;
    if (path is not { Length: > 0 }) {
      // No path at all: a kernel thread, or a process this user may not open. Which of the two it is
      // was decided where the path was asked for, and both mean there is nothing here to read.
      reason = UnknownReason.NotPermitted;
      return null;
    }

    FileInfo info;
    try {
      info = new(path);
      if (!info.Exists) {
        // The running image can be deleted underneath the process, which keeps running from it.
        reason = UnknownReason.SourceGone;
        return null;
      }
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
      reason = UnknownReason.NotPermitted;
      return null;
    }

    var length = info.Length;
    var modified = info.LastWriteTimeUtc.Ticks;
    if (this._byPath.TryGetValue(path, out var cached)
        && cached.Length == length
        && cached.ModifiedTicks == modified
        // A run that has only just been asked for the signature finds an entry taken without one,
        // and must read the file again rather than reporting for ever that the image is unsigned.
        && (!wantSignature || cached.Signature is not null || cached.Facts is null)) {
      reason = cached.Reason;
      signature = cached.Signature;
      return cached.Facts;
    }

    var entry = ReadFile(path, length, wantSignature);
    this._byPath[path] = entry;
    reason = entry.Reason;
    signature = entry.Signature;
    return entry.Facts;
  }

  private static Entry ReadFile(string path, long length, bool wantSignature) {
    var modified = File.GetLastWriteTimeUtc(path).Ticks;
    if (length is <= 0 or > _MaximumBytes)
      return new(length, modified, null, null, UnknownReason.NotImplementedHere);

    byte[] bytes;
    try {
      bytes = File.ReadAllBytes(path);
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
      return new(length, modified, null, null, UnknownReason.NotPermitted);
    }

    if (!PortableExecutable.TryRead(bytes, out var facts))
      // A file that runs and is not a PE image is a real thing on Windows — a script through its
      // interpreter, whatever a subsystem is running — and this is a finding about it rather than a
      // failure to read (PRD §72.3).
      return new(length, modified, null, null, UnknownReason.NotSupportedOnPlatform);

    AuthenticodeFacts? signature = null;
    if (wantSignature && AuthenticodeSignature.TryRead(bytes, out var verdict))
      signature = verdict;

    return new(length, modified, facts, signature, UnknownReason.None);
  }

  /// <summary>
  /// When the running image was created, in UTC ticks (PRD §14).
  /// </summary>
  /// <remarks>
  /// <para>
  /// NTFS has recorded a creation time for every file since it was written, so on Windows this is
  /// the one field of §14 that answers more reliably than the Linux half does — there, most file
  /// systems carry no birth time at all. Which is why the two <em>still</em> keep their reasons
  /// apart: a file system with nothing to say and a file this user may not stat are different
  /// findings and must not be the same cell (PRD §72.3).
  /// </para>
  /// <para>
  /// Not cached, and deliberately: this is a stat rather than a read of the file, it is behind its
  /// own switch, and a cache keyed on the path would have to be invalidated by the very stat it was
  /// meant to save. The version resource above is the other case — there the stat is what tells the
  /// cache whether the expensive read is still valid.
  /// </para>
  /// <para>
  /// A file system that carries no creation time hands back the epoch of a <c>FILETIME</c>, which is
  /// 1601 and not nought — so the test is against that instant rather than against zero. A column of
  /// 1601 would be a lie the width of the table, exactly as a column of 1970 would be on Linux.
  /// </para>
  /// </remarks>
  public static Counter Created(string? path) {
    if (path is not { Length: > 0 })
      return Counter.NotPermitted;

    try {
      var info = new FileInfo(path);
      if (!info.Exists)
        // The running image can be deleted underneath the process, which keeps running from it.
        return Counter.Unknown(UnknownReason.SourceGone);

      var ticks = info.CreationTimeUtc.Ticks;
      return ticks > _FileTimeEpochTicks
        ? Counter.Of((ulong)ticks)
        : Counter.NotSupported;
    } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) {
      return Counter.NotPermitted;
    }
  }

  /// <summary>1601-01-01 UTC in <see cref="DateTime"/> ticks: what "no creation time" comes back as.</summary>
  private static readonly long _FileTimeEpochTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

  /// <summary>Drops entries for images nothing is running any more.</summary>
  /// <remarks>
  /// Bounded rather than swept every sample: the set of distinct images on a machine is small and
  /// stable, and walking it once a second to find nothing would cost more than the entries do.
  /// </remarks>
  public void Prune(HashSet<string> livePaths) {
    if (this._byPath.Count < 4096)
      return;

    foreach (var path in this._byPath.Keys.Where(path => !livePaths.Contains(path)).ToList())
      this._byPath.Remove(path);
  }

  /// <summary>
  /// Copies what was read into a record, or the reason there is nothing (PRD §14).
  /// </summary>
  /// <remarks>
  /// A PE image that carries no version resource at all leaves the five strings null with the reason
  /// unset, which the column renders as an empty cell rather than as a placeholder — because
  /// "this program ships no version resource" is a true statement about a great many programs and is
  /// not the same finding as "nobody could look". The subsystem is filled either way: it is in the
  /// header rather than in the resource.
  /// </remarks>
  public static void Apply(ref ProcessRecord record, PeImageFacts? facts, UnknownReason reason) {
    if (facts is not { } value) {
      record.ImageVersionReason = reason;
      record.Subsystem = Counter.Unknown(reason == UnknownReason.None ? UnknownReason.NotSupportedOnPlatform : reason);
      return;
    }

    record.ImageDescription = value.Description;
    record.ImageCompany = value.Company;
    record.ImageProduct = value.Product;
    record.ImageProductVersion = value.ProductVersion;
    record.ImageFileVersion = value.FileVersion;
    record.ImageVersionReason = UnknownReason.None;
    record.Subsystem = Counter.Of(value.Subsystem);
  }

  /// <summary>
  /// Copies the signature verdict into a record, or the reason there is none (PRD §21, §70).
  /// </summary>
  /// <remarks>
  /// The three certificate strings stay null for an image that is unsigned, and that is deliberate:
  /// a file nobody signed has no signer, and the verdict beside them says "Unsigned" in words. What
  /// must never happen is the opposite — a verdict left at <see cref="SignatureStatus.NotChecked"/>
  /// with no reason, which renders as an empty cell and reads as "signed by nobody" when it means
  /// "nobody looked".
  /// </remarks>
  public static void ApplySignature(ref ProcessRecord record, AuthenticodeFacts? signature, UnknownReason reason) {
    if (signature is not { } value) {
      record.ImageSignature = SignatureStatus.NotChecked;
      record.ImageSignatureDetail = null;
      // A file that is not a PE image at all carries no Authenticode signature by construction, and
      // a file nobody could read carries no answer — two different empty cells (PRD §72.3).
      record.ImageSignatureReason = reason == UnknownReason.None ? UnknownReason.NotSupportedOnPlatform : reason;
      record.ImageSigner = null;
      record.CertificateSubject = null;
      record.CertificateIssuer = null;
      record.SignatureTimestampUtcTicks = Counter.Unknown(record.ImageSignatureReason);
      return;
    }

    record.ImageSignature = value.Status;
    record.ImageSignatureDetail = value.Detail;
    record.ImageSignatureReason = UnknownReason.None;
    record.ImageSigner = value.Signer;
    record.CertificateSubject = value.Subject;
    record.CertificateIssuer = value.Issuer;
    // Nought is "nothing countersigned this", which is a reading rather than a hole — but only for an
    // image that has a signature at all. An unsigned file has no timestamp to be absent.
    record.SignatureTimestampUtcTicks = value.Status == SignatureStatus.Unsigned
      ? Counter.NotSupported
      : Counter.Of((ulong)value.TimestampUtcTicks);
  }

  /// <summary>
  /// What the five signature readings say on a run that never asked for them (PRD §5.4, §72.3).
  /// </summary>
  /// <remarks>
  /// Spelled out rather than left at the default, because the default of
  /// <see cref="SignatureStatus"/> is <see cref="SignatureStatus.NotChecked"/> with no reason beside
  /// it — which is exactly the shape of a record nobody filled claiming to have an answer.
  /// </remarks>
  public static void NotAsked(ref ProcessRecord record) {
    record.ImageSignature = SignatureStatus.NotChecked;
    record.ImageSignatureDetail = null;
    record.ImageSignatureReason = UnknownReason.NotSampledYet;
    record.ImageSigner = null;
    record.CertificateSubject = null;
    record.CertificateIssuer = null;
    record.SignatureTimestampUtcTicks = Counter.NotSampledYet;
    NoTrustChain(ref record);
  }

  /// <summary>
  /// Says why there is no trust chain here, rather than leaving the slot at its default (PRD §70).
  /// </summary>
  /// <remarks>
  /// Windows is the platform that actually has certificates and a root store, so "this platform has
  /// no such thing" would be false — it has one and this program does not walk it. The verifier
  /// beside this recomputes the digest and checks the signer's own signature and deliberately stops
  /// there, which §21 argues at length; what it must not do is leave the slot at a default reason,
  /// because a defaulted reason means "the value is present" and the value would be NotChecked read
  /// as a finding (PRD §72.3).
  /// </remarks>
  public static void NoTrustChain(ref ProcessRecord record) {
    record.TrustChain = SignatureStatus.NotChecked;
    record.TrustChainReason = UnknownReason.NotImplementedHere;
  }

}
