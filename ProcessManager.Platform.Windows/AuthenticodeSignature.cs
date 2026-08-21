using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What an image's own Authenticode signature says, and whether it still covers the bytes that are
/// there (PRD §21, §70).
/// </summary>
/// <param name="Status">
/// Local signature verification and only that — §70's second question, in §70's vocabulary. It is
/// never a trust chain: nothing here asks whether the certificate below chains to a root this
/// machine believes in, which is a separate question with a separate slot.
/// </param>
/// <param name="Detail">One sentence naming what was compared, so the word above is never the whole story.</param>
/// <param name="Signer">
/// The signing certificate's common name — the publisher as the certificate spells it, which is what
/// a person means by "who signed this".
/// </param>
/// <param name="Subject">That certificate's whole subject, for the cases where the common name is not enough.</param>
/// <param name="Issuer">Who issued that certificate. Not who this machine trusts: who put their name to it.</param>
/// <param name="TimestampUtcTicks">
/// When the signature was countersigned, or nought where nothing countersigned it. Nought is a real
/// answer and a common one — a great deal of software is signed and never timestamped — and it is
/// the difference between a signature that survives its certificate's expiry and one that does not.
/// </param>
internal readonly record struct AuthenticodeFacts(
  SignatureStatus Status,
  string? Detail,
  string? Signer,
  string? Subject,
  string? Issuer,
  long TimestampUtcTicks
);

/// <summary>
/// Reads the certificate table out of a PE image and checks the signature in it against the image.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is and is not.</b> §21 refused the signature columns for a long time on the grounds
/// that a verifier nobody can run against the OS it describes is worse than an empty one, and that
/// remains true of <c>WinVerifyTrust</c> — which is a verdict about <em>trust</em>, decided by a
/// machine's root store, its revocation lists and its policy providers. None of that is here. What is
/// here is the other half, and it is the half the version-resource walk beside it already
/// established as buildable: a documented on-disk layout, a documented digest algorithm, and
/// arithmetic that real signed files can be held against on any machine at all.
/// </para>
/// <para>
/// So the question this answers is exactly §70's second one — do these bytes still match the digest
/// the signature covers, and is the signature over that digest genuinely the signer's? — and it
/// answers it the same way the Linux side answers it for a package: by comparing the running image
/// against a recorded digest. <see cref="ImageTrust.TrustChain"/> stays empty and says why, because
/// a good signature by an unknown key is a different finding from a good signature by a known one
/// (PRD §70).
/// </para>
/// <para>
/// Deliberately not marked as Windows-only, and deliberately taking bytes rather than a path: every
/// step is portable, so the whole of it is exercised on every CI leg against real signed images
/// rather than only on a machine that runs them (PRD §9.4). It was written and checked on Linux
/// against six binaries signed by Microsoft and by the .NET Foundation, all of which verify, and
/// against one 1998 VeriSign-signed installer, whose certificate and countersignature it reads and
/// whose MD5 digest it declines to judge.
/// </para>
/// <para>
/// <b>The primary signature only.</b> A dual-signed image carries a second signature as an unsigned
/// attribute of the first, and this reads the first. Reporting the stronger of two and saying
/// nothing about which was read would be a cell that means different things on different rows.
/// </para>
/// </remarks>
internal static class AuthenticodeSignature {

  /// <summary>The data directory that points at the certificate table: <c>IMAGE_DIRECTORY_ENTRY_SECURITY</c>.</summary>
  private const int _SECURITY_DIRECTORY_INDEX = 4;

  /// <summary><c>WIN_CERT_TYPE_PKCS_SIGNED_DATA</c>, the only certificate type Authenticode uses.</summary>
  private const ushort _PKCS_SIGNED_DATA = 2;

  /// <summary><c>SPC_INDIRECT_DATA_OBJID</c> — what an Authenticode SignedData wraps.</summary>
  private const string _SPC_INDIRECT_DATA = "1.3.6.1.4.1.311.2.1.4";

  /// <summary>Microsoft's RFC 3161 timestamp attribute, which is how modern signatures are dated.</summary>
  private const string _RFC3161_COUNTERSIGNATURE = "1.3.6.1.4.1.311.3.3.1";

  /// <summary><c>id-ct-TSTInfo</c>, the content a timestamp token wraps.</summary>
  private const string _TST_INFO = "1.2.840.113549.1.9.16.1.4";

  /// <summary>PKCS#9 <c>signingTime</c>, which is how a legacy countersignature is dated.</summary>
  private const string _SIGNING_TIME = "1.2.840.113549.1.9.5";

  private const ushort _PE32_MAGIC = 0x010B;
  private const ushort _PE32PLUS_MAGIC = 0x020B;

  /// <summary>
  /// What the image's signature says, or <see langword="false"/> when the bytes are not a PE image.
  /// </summary>
  /// <remarks>
  /// "These bytes are not a program" is a different finding from "this program is not signed", and
  /// the caller renders them differently, so they are not the same return (PRD §72.3).
  /// </remarks>
  public static bool TryRead(byte[] file, out AuthenticodeFacts facts) {
    facts = default;
    if (!TryReadLayout(file, out var layout))
      return false;

    facts = Verify(file, in layout);
    return true;
  }

  #region the image's own layout

  /// <summary>
  /// The parts of a PE image the Authenticode digest is defined in terms of.
  /// </summary>
  /// <param name="ChecksumOffset">
  /// Where the optional header's <c>CheckSum</c> is. Skipped by the digest, because signing the file
  /// changes its length and therefore its checksum.
  /// </param>
  /// <param name="SecurityEntryOffset">
  /// Where the certificate table's data-directory entry is. Skipped for the same reason: it is
  /// written after the digest is taken.
  /// </param>
  /// <param name="HeadersSize">The optional header's <c>SizeOfHeaders</c>, where the section data begins.</param>
  /// <param name="Sections">Each section's file offset and length, in whatever order the table lists them.</param>
  /// <param name="CertificateOffset">The certificate table's file offset — a plain offset, not an address.</param>
  /// <param name="CertificateSize">Its length in bytes, or nought when the image carries none.</param>
  private readonly record struct Layout(
    int ChecksumOffset,
    int SecurityEntryOffset,
    int HeadersSize,
    (int Offset, int Size)[] Sections,
    int CertificateOffset,
    int CertificateSize
  );

  /// <summary>
  /// Walks the DOS stub, the COFF header, the optional header and the section table.
  /// </summary>
  /// <remarks>
  /// Every offset is bounds-checked against the file before it is used: the input is a file somebody
  /// else wrote, and an image whose section table points past its own end is a thing that exists. A
  /// malformed image yields no facts rather than an exception in a sampler.
  /// </remarks>
  private static bool TryReadLayout(byte[] file, out Layout layout) {
    layout = default;
    var span = (ReadOnlySpan<byte>)file;
    if (span.Length < 0x40 || span[0] != (byte)'M' || span[1] != (byte)'Z')
      return false;

    var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
    if (peOffset > int.MaxValue || peOffset + 24 > (uint)span.Length)
      return false;

    var pe = (int)peOffset;
    if (span[pe] != (byte)'P' || span[pe + 1] != (byte)'E' || span[pe + 2] != 0 || span[pe + 3] != 0)
      return false;

    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 6)..]);
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(pe + 20)..]);
    var optional = pe + 24;
    if (optionalSize < 72 || (long)optional + optionalSize > span.Length)
      return false;

    var magic = BinaryPrimitives.ReadUInt16LittleEndian(span[optional..]);
    if (magic != _PE32_MAGIC && magic != _PE32PLUS_MAGIC)
      return false;

    // SizeOfHeaders sits at the same offset in both forms: the four bytes PE32 gains in the standard
    // fields are the four PE32+ gains in its 64-bit image base.
    var headersSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(optional + 60)..]);
    if (headersSize <= 0 || headersSize > span.Length)
      return false;

    var directoryCountOffset = optional + (magic == _PE32PLUS_MAGIC ? 108 : 92);
    if (directoryCountOffset + 4 > optional + optionalSize)
      return false;

    var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(span[directoryCountOffset..]);
    if (directoryCount <= _SECURITY_DIRECTORY_INDEX)
      return false;

    var securityEntry = directoryCountOffset + 4 + (_SECURITY_DIRECTORY_INDEX * 8);
    if (securityEntry + 8 > optional + optionalSize)
      return false;

    // Alone among the sixteen, this directory's first field is a file offset rather than an address:
    // the certificate table is never mapped, so there is no address it could have.
    var certificateOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[securityEntry..]);
    var certificateSize = BinaryPrimitives.ReadUInt32LittleEndian(span[(securityEntry + 4)..]);
    if ((long)certificateOffset + certificateSize > span.Length)
      return false;

    var table = (long)optional + optionalSize;
    if (table + ((long)sectionCount * 40) > span.Length)
      return false;

    var sections = new List<(int, int)>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var header = (int)table + (i * 40);
      var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 16)..]);
      var offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(header + 20)..]);
      if (size == 0)
        continue;

      if ((long)offset + size > span.Length)
        return false;

      sections.Add(((int)offset, (int)size));
    }

    layout = new(
      ChecksumOffset: optional + 64,
      SecurityEntryOffset: securityEntry,
      HeadersSize: headersSize,
      Sections: [.. sections],
      CertificateOffset: (int)certificateOffset,
      CertificateSize: (int)certificateSize
    );

    return true;
  }

  #endregion

  #region the verdict

  private static AuthenticodeFacts Verify(byte[] file, ref readonly Layout layout) {
    if (layout.CertificateSize == 0)
      return new(
        SignatureStatus.Unsigned,
        "the image carries no certificate table, so there is no signature to check",
        null,
        null,
        null,
        0
      );

    if (!TryReadCertificateBlob(file, in layout, out var blob, out var why))
      return Failed(why);

    var signed = new SignedCms();
    try {
      signed.Decode(blob);
    } catch (Exception exception) when (exception is CryptographicException or AsnContentException) {
      return Failed("the certificate table is not a PKCS#7 signed-data structure this build can read");
    }

    if (signed.ContentInfo.ContentType.Value != _SPC_INDIRECT_DATA)
      return Failed(
        $"the signature wraps {signed.ContentInfo.ContentType.Value ?? "nothing recognisable"} rather than Authenticode's indirect data"
      );

    if (!TryReadIndirectDigest(signed.ContentInfo.Content, out var digestOid, out var recorded))
      return Failed("the signature's indirect data does not carry a digest where Authenticode puts one");

    // A signer this build cannot name at all. There is always exactly one for an Authenticode
    // signature, and no certificate means nothing below can be reported either.
    var signer = signed.SignerInfos.Count > 0 ? signed.SignerInfos[0] : null;
    var certificate = signer?.Certificate;
    if (signer is null || certificate is null)
      return Failed("the signature names no signing certificate");

    var subject = certificate.Subject;
    var issuer = certificate.Issuer;
    var name = CommonName(certificate);
    var timestamp = Countersigned(signer);

    // MD5 is the one digest here that is refused rather than computed. Nineteen-nineties software
    // really does carry it, and reporting such a signature as verified would put this program's word
    // behind an algorithm that has been forgeable since 2004 — so it says what it found and declines
    // to judge it, which is what "verification error" means (PRD §70).
    if (!TryHashAlgorithm(digestOid, out var algorithm))
      return new(
        SignatureStatus.VerificationError,
        $"the image's Authenticode digest is {DigestName(digestOid)}, which this build will not accept as evidence",
        name,
        subject,
        issuer,
        timestamp
      );

    var computed = ImageDigest(file, in layout, algorithm);
    if (!computed.AsSpan().SequenceEqual(recorded))
      return new(
        SignatureStatus.InvalidSignature,
        $"the image's {algorithm.Name} digest is not the one its signature records — the file has changed since it was signed",
        name,
        subject,
        issuer,
        timestamp
      );

    try {
      // Signature only, and the parameter name says so. The chain deliberately goes unchecked here:
      // it needs a root store and a revocation list, it is a different one of §70's five questions,
      // and it has its own slot which stays empty rather than borrowing this verdict.
      signed.CheckSignature(verifySignatureOnly: true);
    } catch (CryptographicException) {
      return new(
        SignatureStatus.InvalidSignature,
        "the image's digest matches, but the signature over it is not the one the certificate's key would make",
        name,
        subject,
        issuer,
        timestamp
      );
    }

    // A countersigned timestamp is what keeps a signature good after the certificate behind it runs
    // out, which is the ordinary state of most signed software: this file was signed in 2019 by a
    // certificate that expired in 2021 and the signature is still the signature it was. Without one
    // there is nothing to date the signing by, so the only instant left to judge against is now.
    var instant = timestamp > 0
      ? new DateTimeOffset(timestamp, TimeSpan.Zero)
      : DateTimeOffset.UtcNow;

    if (instant < certificate.NotBefore.ToUniversalTime() || instant > certificate.NotAfter.ToUniversalTime())
      return new(
        SignatureStatus.Expired,
        timestamp > 0
          ? $"the signature is good, and the certificate behind it was not valid on {instant:yyyy-MM-dd}, when it was countersigned"
          : "the signature is good and the certificate behind it has run out; nothing countersigned the signature, so there is no earlier date to judge it by",
        name,
        subject,
        issuer,
        timestamp
      );

    return new(
      SignatureStatus.Verified,
      $"the image's {algorithm.Name} digest matches the one its signature records, and the signature over it is the certificate's. Nothing here checked the chain behind that certificate",
      name,
      subject,
      issuer,
      timestamp
    );
  }

  /// <summary>A failure that happened before any certificate could be read, so nothing below is filled.</summary>
  private static AuthenticodeFacts Failed(string detail)
    => new(SignatureStatus.VerificationError, detail, null, null, null, 0);

  #endregion

  #region the structures

  /// <summary>
  /// The PKCS#7 blob out of the <c>WIN_CERTIFICATE</c> the certificate table begins with.
  /// </summary>
  /// <remarks>
  /// The table is a list of these rather than one, and this reads the first — which is the image's
  /// own signature. The header is a length, a revision and a type, and the type is checked rather
  /// than assumed: the format has room for others, and handing a non-PKCS#7 blob to a PKCS#7 decoder
  /// would report a broken signature where there is a kind of certificate this build does not read.
  /// </remarks>
  private static bool TryReadCertificateBlob(
    byte[] file,
    ref readonly Layout layout,
    out byte[] blob,
    out string why
  ) {
    blob = [];
    why = "";
    var span = (ReadOnlySpan<byte>)file;
    if (layout.CertificateSize < 8) {
      why = "the certificate table is too short to hold a WIN_CERTIFICATE header";
      return false;
    }

    var start = layout.CertificateOffset;
    var length = BinaryPrimitives.ReadUInt32LittleEndian(span[start..]);
    var type = BinaryPrimitives.ReadUInt16LittleEndian(span[(start + 6)..]);
    if (length < 8 || length > (uint)layout.CertificateSize) {
      why = "the certificate table's first entry declares a length its own table cannot hold";
      return false;
    }

    if (type != _PKCS_SIGNED_DATA) {
      why = $"the image's certificate is of type {type}, which is not the PKCS#7 signed data Authenticode uses";
      return false;
    }

    blob = span.Slice(start + 8, (int)length - 8).ToArray();
    return true;
  }

  /// <summary>
  /// The digest Authenticode recorded for the image, out of <c>SpcIndirectDataContent</c>.
  /// </summary>
  /// <remarks>
  /// The structure is a sequence of two: what was signed, which is skipped, and a
  /// <c>DigestInfo</c> — an algorithm identifier and an octet string — which is the whole of what
  /// this needs. BER rather than DER, because signers in the wild have written both.
  /// </remarks>
  private static bool TryReadIndirectDigest(ReadOnlyMemory<byte> content, out string oid, out byte[] digest) {
    oid = "";
    digest = [];
    try {
      var sequence = new AsnReader(content, AsnEncodingRules.BER).ReadSequence();
      sequence.ReadSequence();
      var info = sequence.ReadSequence();
      oid = info.ReadSequence().ReadObjectIdentifier();
      digest = info.ReadOctetString();
      return digest.Length > 0;
    } catch (AsnContentException) {
      return false;
    }
  }

  /// <summary>
  /// When the signature was countersigned, in UTC ticks, or nought.
  /// </summary>
  /// <remarks>
  /// Two forms, because both are in the wild and neither replaced the other cleanly. Modern
  /// signatures carry an RFC 3161 token as an unsigned attribute, whose <c>TSTInfo</c> holds the
  /// authority's own generalised time; older ones carry a PKCS#9 countersignature whose signed
  /// attributes hold a <c>signingTime</c>. Read structurally rather than through
  /// <c>Rfc3161TimestampToken</c>, which refuses tokens this program has real files carrying —
  /// a Microsoft-signed compiler from 2021 among them — and a timestamp reported as absent because a
  /// stricter decoder declined it would be a false "never countersigned" (PRD §72.3).
  /// </remarks>
  private static long Countersigned(SignerInfo signer) {
    foreach (var attribute in signer.UnsignedAttributes) {
      if (attribute.Oid.Value != _RFC3161_COUNTERSIGNATURE)
        continue;

      foreach (var value in attribute.Values) {
        var token = new SignedCms();
        try {
          token.Decode(value.RawData);
        } catch (Exception exception) when (exception is CryptographicException or AsnContentException) {
          continue;
        }

        if (token.ContentInfo.ContentType.Value != _TST_INFO)
          continue;

        try {
          // TSTInfo: version, policy, messageImprint, serialNumber, then the time itself.
          var info = new AsnReader(token.ContentInfo.Content, AsnEncodingRules.BER).ReadSequence();
          info.ReadInteger();
          info.ReadObjectIdentifier();
          info.ReadSequence();
          info.ReadInteger();
          return info.ReadGeneralizedTime().UtcTicks;
        } catch (AsnContentException) {
          // A token whose shape is not the one RFC 3161 documents dates nothing, and saying so by
          // returning no time is better than dating the signature by a number read out of the wrong
          // field.
        }
      }
    }

    foreach (var counter in signer.CounterSignerInfos)
    foreach (var attribute in counter.SignedAttributes) {
      if (attribute.Oid.Value != _SIGNING_TIME || attribute.Values.Count == 0)
        continue;

      try {
        return new Pkcs9SigningTime(attribute.Values[0].RawData).SigningTime.ToUniversalTime().Ticks;
      } catch (CryptographicException) {
        // As above: no date rather than a wrong one.
      }
    }

    return 0;
  }

  #endregion

  #region the digest

  /// <summary>
  /// The image's Authenticode digest, over the bytes the specification says it covers.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Not a hash of the file. Three things are left out, and each of them for the same reason — they
  /// are written <em>after</em> the digest is taken, so hashing them would make every signature
  /// disagree with itself: the optional header's checksum, the eight bytes of the certificate
  /// table's directory entry, and the certificate table itself.
  /// </para>
  /// <para>
  /// The sections are hashed in ascending file order rather than in the order the section table
  /// lists them, and anything after the last section but before the certificate table is hashed too
  /// — a linker may write both, and a walk that assumed the table's order or ignored the trailing
  /// bytes would compute a digest that is wrong only for the files that do. Held against six real
  /// Microsoft- and .NET-Foundation-signed images, whose recorded digests this reproduces exactly.
  /// </para>
  /// </remarks>
  private static byte[] ImageDigest(byte[] file, ref readonly Layout layout, HashAlgorithmName algorithm) {
    using var hash = IncrementalHash.CreateHash(algorithm);
    hash.AppendData(file, 0, layout.ChecksumOffset);

    var afterChecksum = layout.ChecksumOffset + 4;
    hash.AppendData(file, afterChecksum, layout.SecurityEntryOffset - afterChecksum);

    var afterEntry = layout.SecurityEntryOffset + 8;
    hash.AppendData(file, afterEntry, layout.HeadersSize - afterEntry);

    var ordered = layout.Sections.ToArray();
    Array.Sort(ordered, static (a, b) => a.Offset.CompareTo(b.Offset));

    var hashed = (long)layout.HeadersSize;
    foreach (var (offset, size) in ordered) {
      hash.AppendData(file, offset, size);
      hashed += size;
    }

    // Whatever a linker appended after the last section and before the certificate table. Debug
    // directories and signing tools both put things here.
    var trailing = file.LongLength - layout.CertificateSize - hashed;
    if (trailing > 0 && hashed + trailing <= file.LongLength)
      hash.AppendData(file, (int)hashed, (int)trailing);

    return hash.GetHashAndReset();
  }

  /// <summary>
  /// The digest algorithms this build will judge a signature by.
  /// </summary>
  /// <remarks>
  /// SHA-1 is here and MD5 is not, which is a line drawn rather than an oversight: a SHA-1
  /// Authenticode signature is the second half of most dual-signed software and no public collision
  /// has ever been produced against one, while MD5 has been forgeable in practice since 2004. An
  /// algorithm not named here is reported as unsupported rather than as a broken signature
  /// (PRD §72.3).
  /// </remarks>
  private static bool TryHashAlgorithm(string oid, out HashAlgorithmName algorithm) {
    switch (oid) {
      case "1.3.14.3.2.26": algorithm = HashAlgorithmName.SHA1; return true;
      case "2.16.840.1.101.3.4.2.1": algorithm = HashAlgorithmName.SHA256; return true;
      case "2.16.840.1.101.3.4.2.2": algorithm = HashAlgorithmName.SHA384; return true;
      case "2.16.840.1.101.3.4.2.3": algorithm = HashAlgorithmName.SHA512; return true;
      default: algorithm = default; return false;
    }
  }

  /// <summary>What to call a digest this build refuses, so the cell names it rather than shrugging.</summary>
  private static string DigestName(string oid) => oid switch {
    "1.2.840.113549.2.5" => "MD5",
    "1.2.840.113549.2.2" => "MD2",
    _ => oid,
  };

  /// <summary>
  /// The certificate's common name, which is the publisher as a person would say it.
  /// </summary>
  /// <remarks>
  /// The whole subject is kept as its own field beside this one. A distinguished name is seven
  /// fields of which one is the answer to "who signed this", and a column that showed all seven
  /// would answer it less well than one that shows the one.
  /// </remarks>
  private static string? CommonName(X509Certificate2 certificate) {
    var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
    return name is { Length: > 0 } ? name : null;
  }

  #endregion

}
