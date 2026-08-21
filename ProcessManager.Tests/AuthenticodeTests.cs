using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Windows;
using Hawkynt.ProcessManager.Query;
using Hawkynt.ProcessManager.Sampling;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The Authenticode reader (PRD §21, §70), on whatever OS the tests are running on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a circle.</strong> The images signed here are real PE images this repository built,
/// and the digest the signature carries is one this test computes with nothing but
/// <see cref="IncrementalHash"/> over the bytes the Authenticode specification names — so a reader
/// that skipped the wrong bytes, hashed the sections in table order rather than in file order, or
/// forgot the trailing data cannot agree with it. The signing itself is Microsoft's own PKCS#7
/// implementation rather than this repository's.
/// </para>
/// <para>
/// The other half of the verification is not in this file at all and could not be: the algorithm was
/// held against six real binaries signed by Microsoft and by the .NET Foundation, whose recorded
/// digests it reproduces byte for byte, and one of those recorded digests and one of those
/// countersignature times were read out independently with <c>openssl asn1parse</c>. What is here is
/// what a CI leg can repeat.
/// </para>
/// </remarks>
[TestFixture]
public sealed class AuthenticodeTests {

  /// <summary>One real PE image this build produced, unsigned as the compiler left it.</summary>
  private static byte[] UnsignedImage() {
    var directory = TestContext.CurrentContext.TestDirectory;
    foreach (var path in Directory.EnumerateFiles(directory, "Hawkynt.*.dll").OrderBy(static path => path))
      return File.ReadAllBytes(path);

    Assert.Fail("no assembly of this build was in the test output directory");
    return [];
  }

  #region what the reader says about real files

  [Test]
  public void AnImageThisBuildProducedIsReadAsUnsignedRatherThanUnreadable() {
    Assert.That(AuthenticodeSignature.TryRead(UnsignedImage(), out var facts), Is.True);
    Assert.That(facts.Status, Is.EqualTo(SignatureStatus.Unsigned));
    Assert.That(facts.Detail, Is.Not.Null.And.Not.Empty);
    // Unsigned is a finding, not a hole: there is nobody to name, and nothing here pretends there is.
    Assert.That(facts.Signer, Is.Null);
    Assert.That(facts.Subject, Is.Null);
    Assert.That(facts.Issuer, Is.Null);
    Assert.That(facts.TimestampUtcTicks, Is.Zero);
  }

  [Test]
  public void SomethingThatIsNotAPeImageIsNotAnUnsignedOne() {
    // The difference the return value carries: "these bytes are not a program" and "this program is
    // not signed" are different findings and the caller renders them differently (PRD §72.3).
    Assert.That(AuthenticodeSignature.TryRead("#!/bin/sh\necho hello\n"u8.ToArray(), out _), Is.False);
    Assert.That(AuthenticodeSignature.TryRead([], out _), Is.False);
    Assert.That(AuthenticodeSignature.TryRead([0x4D, 0x5A], out _), Is.False);
  }

  #endregion

  #region what it says about a signature it can check

  [Test]
  public void AnImageSignedOverItsOwnDigestVerifies() {
    using var certificate = SelfSigned("CN=ProcessManager Test Publisher, O=Hawkynt", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    var signed = Sign(UnsignedImage(), certificate);

    Assert.That(AuthenticodeSignature.TryRead(signed, out var facts), Is.True);
    Assert.That(facts.Status, Is.EqualTo(SignatureStatus.Verified), facts.Detail);
    Assert.That(facts.Signer, Is.EqualTo("ProcessManager Test Publisher"));
    Assert.That(facts.Subject, Does.Contain("ProcessManager Test Publisher"));
    Assert.That(facts.Issuer, Does.Contain("ProcessManager Test Publisher"));
    // Nothing countersigned it, which is a reading rather than a hole — and is what makes the
    // certificate's own validity the thing the verdict then turns on.
    Assert.That(facts.TimestampUtcTicks, Is.Zero);
    // The word must never be the whole story: §70's first requirement is that a verdict says what it
    // checked, and this one has to say that it did not check the chain.
    Assert.That(facts.Detail, Does.Contain("chain"));
  }

  /// <summary>
  /// The check that a wrong digest walk cannot pass: one byte of the image changes and nothing else
  /// does.
  /// </summary>
  /// <remarks>
  /// The byte chosen is inside the last section rather than in a header, which is the region a
  /// reader that hashed only the headers, or stopped at <c>SizeOfHeaders</c>, would never look at.
  /// </remarks>
  [Test]
  public void OneChangedByteInsideASectionInvalidatesTheSignature() {
    using var certificate = SelfSigned("CN=ProcessManager Test Publisher", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    var image = UnsignedImage();
    var signed = Sign(image, certificate);

    var target = LastSectionByte(image);
    signed[target] ^= 0xFF;

    Assert.That(AuthenticodeSignature.TryRead(signed, out var facts), Is.True);
    Assert.That(facts.Status, Is.EqualTo(SignatureStatus.InvalidSignature), facts.Detail);
    // The certificate is still readable and is still reported: who claimed to sign a file that has
    // since changed is precisely what somebody looking at this row wants to know.
    Assert.That(facts.Signer, Is.EqualTo("ProcessManager Test Publisher"));
  }

  /// <summary>
  /// A good signature made by a certificate whose validity has run out, with nothing to date it.
  /// </summary>
  /// <remarks>
  /// The one verdict of the eight that depends on the timestamp column beside it: with a
  /// countersignature this same file would be judged as of the day it was signed and would pass.
  /// </remarks>
  [Test]
  public void AGoodSignatureFromAnExpiredCertificateWithNoTimestampReadsAsExpired() {
    using var certificate = SelfSigned(
      "CN=ProcessManager Test Publisher",
      DateTimeOffset.UtcNow.AddYears(-3),
      DateTimeOffset.UtcNow.AddYears(-2)
    );

    Assert.That(AuthenticodeSignature.TryRead(Sign(UnsignedImage(), certificate), out var facts), Is.True);
    Assert.That(facts.Status, Is.EqualTo(SignatureStatus.Expired), facts.Detail);
    Assert.That(facts.Signer, Is.EqualTo("ProcessManager Test Publisher"));
  }

  [Test]
  public void ACertificateTableThatIsNotPkcs7IsAVerificationErrorRatherThanABadSignature() {
    using var certificate = SelfSigned("CN=ProcessManager Test Publisher", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    var signed = Sign(UnsignedImage(), certificate);

    // wCertificateType, at offset six of the WIN_CERTIFICATE the table begins with.
    var table = CertificateTableOffset(signed);
    BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(table + 6), 0x0001);

    Assert.That(AuthenticodeSignature.TryRead(signed, out var facts), Is.True);
    // Not "invalid signature": there is a certificate of a kind this build does not read, which is a
    // failure to check rather than a check that failed (PRD §70, §72.3).
    Assert.That(facts.Status, Is.EqualTo(SignatureStatus.VerificationError), facts.Detail);
  }

  #endregion

  #region what a record that was never filled says

  [Test]
  public void ARecordNobodyAskedAboutSaysSoRatherThanReportingAnUnsignedImage() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "test";
    WindowsImageReader.NotAsked(ref records[0]);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var pending = Humanize.Placeholder(UnknownReason.NotSampledYet);

    foreach (var field in new[] {
      ProcessField.ImageSignature, ProcessField.ImageSigner, ProcessField.CertificateSubject,
      ProcessField.CertificateIssuer, ProcessField.SignatureTimestamp,
    }) {
      Assert.That(FieldAccessor.Text(field, in snapshot.Processes[0], delta, 0), Is.EqualTo(pending), field.ToString());
      Assert.That(FieldAccessor.RawText(field, in snapshot.Processes[0]), Is.Null, field.ToString());
    }

    // And no number either, so a filter cannot match an unasked question as though it had an answer.
    Assert.That(FieldAccessor.Number(ProcessField.ImageSignature, in snapshot.Processes[0], delta, 0), Is.Null);
    Assert.That(FieldAccessor.Number(ProcessField.SignatureTimestamp, in snapshot.Processes[0], delta, 0), Is.Null);
    Assert.That(records[0].SignatureTimestampUtcTicks.HasValue, Is.False);
  }

  /// <summary>
  /// A platform whose executables carry no signature at all says "n/a", which is not "not sampled"
  /// and is certainly not "unsigned".
  /// </summary>
  [Test]
  public void APlatformWithNoEmbeddedSignaturesRendersNotApplicable() {
    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "test";
    // What every probe's records go through before the probe fills them.
    ProcessRecord.ClearPlatformReadings(ref records[0]);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);
    var notApplicable = Humanize.Placeholder(UnknownReason.NotSupportedOnPlatform);

    Assert.That(FieldAccessor.Text(ProcessField.ImageSignature, in snapshot.Processes[0], delta, 0), Is.EqualTo(notApplicable));
    Assert.That(FieldAccessor.Text(ProcessField.ImageSigner, in snapshot.Processes[0], delta, 0), Is.EqualTo(notApplicable));
    Assert.That(FieldAccessor.Text(ProcessField.SignatureTimestamp, in snapshot.Processes[0], delta, 0), Is.EqualTo(notApplicable));
    Assert.That(records[0].SignatureTimestampUtcTicks.HasValue, Is.False);
    Assert.That(records[0].PowerThrottling.HasValue, Is.False);
  }

  /// <summary>The verdict, the certificate and the timestamp as they reach a row (PRD §21).</summary>
  [Test]
  public void TheVerdictReachesTheColumnsThatShowIt() {
    using var certificate = SelfSigned("CN=ProcessManager Test Publisher, O=Hawkynt", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    Assert.That(AuthenticodeSignature.TryRead(Sign(UnsignedImage(), certificate), out var facts), Is.True);

    var snapshot = new SystemSnapshot();
    var records = snapshot.PrepareProcesses(1);
    records[0] = default;
    records[0].Key = new(1, 1);
    records[0].Name = "test";
    WindowsImageReader.ApplySignature(ref records[0], facts, UnknownReason.None);

    var delta = new SnapshotDelta();
    delta.Update(null, snapshot, CpuPercentMode.Normalized);

    Assert.That(FieldAccessor.Text(ProcessField.ImageSignature, in snapshot.Processes[0], delta, 0), Is.EqualTo("Verified"));
    Assert.That(FieldAccessor.Text(ProcessField.ImageSigner, in snapshot.Processes[0], delta, 0), Is.EqualTo("ProcessManager Test Publisher"));
    // Nothing countersigned it, and the column says that word rather than showing a date in the year
    // one — which is what nought ticks would render as if it were treated as a time.
    Assert.That(FieldAccessor.Text(ProcessField.SignatureTimestamp, in snapshot.Processes[0], delta, 0), Is.EqualTo("none"));
    Assert.That(FieldAccessor.RawText(ProcessField.SignatureTimestamp, in snapshot.Processes[0]), Is.EqualTo("none"));
    // The verdict sorts and filters as itself, so "signature.status:Verified" finds this row.
    Assert.That(FieldAccessor.RawText(ProcessField.ImageSignature, in snapshot.Processes[0]), Is.EqualTo("Verified"));
  }

  #endregion

  #region building a signed image

  /// <summary>
  /// Appends a real Authenticode signature over <paramref name="image"/>'s own digest.
  /// </summary>
  /// <remarks>
  /// The digest is computed here, independently of the reader under test, over exactly what the
  /// specification says it covers: everything but the optional header's checksum, the eight bytes of
  /// the certificate table's directory entry, and the certificate table itself. The certificate table
  /// is appended at the end, so the sections and whatever follows them are hashed in file order.
  /// </remarks>
  private static byte[] Sign(byte[] image, X509Certificate2 certificate) {
    var file = (byte[])image.Clone();
    var (checksum, securityEntry, headers, sections) = Offsets(file);

    // The table must begin on an eight-byte boundary, which is also what makes the trailing-data
    // arithmetic in the reader worth testing: the padding is hashed and the table is not.
    var padding = (8 - (file.Length % 8)) % 8;
    var body = new byte[file.Length + padding];
    file.CopyTo(body, 0);

    var digest = Digest(body, checksum, securityEntry, headers, sections);
    var content = new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), IndirectData(digest));
    var signed = new SignedCms(content, detached: false);
    signed.ComputeSignature(new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
    var blob = signed.Encode();

    // WIN_CERTIFICATE: dwLength, wRevision (WIN_CERT_REVISION_2_0), wCertificateType
    // (WIN_CERT_TYPE_PKCS_SIGNED_DATA), then the blob, padded to eight bytes.
    var entryLength = 8 + blob.Length;
    var tableLength = entryLength + ((8 - (entryLength % 8)) % 8);
    var result = new byte[body.Length + tableLength];
    body.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(body.Length), (uint)entryLength);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(body.Length + 4), 0x0200);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(body.Length + 6), 0x0002);
    blob.CopyTo(result, body.Length + 8);

    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(securityEntry), (uint)body.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(securityEntry + 4), (uint)tableLength);
    return result;
  }

  /// <summary>
  /// <c>SpcIndirectDataContent</c>: what was signed, and the digest of it.
  /// </summary>
  /// <remarks>
  /// The first half is the minimum the structure requires — an algorithm identifier for the image
  /// type, which the reader skips because nothing in a process table turns on it. The second half is
  /// the <c>DigestInfo</c> the reader is looking for.
  /// </remarks>
  private static byte[] IndirectData(byte[] digest) {
    var writer = new AsnWriter(AsnEncodingRules.DER);
    using (writer.PushSequence()) {
      using (writer.PushSequence()) {
        // SPC_PE_IMAGE_DATA_OBJID, with no optional value beside it.
        writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15");
        writer.PushSequence();
        writer.PopSequence();
      }

      using (writer.PushSequence()) {
        using (writer.PushSequence()) {
          writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
          writer.WriteNull();
        }

        writer.WriteOctetString(digest);
      }
    }

    return writer.Encode();
  }

  private static X509Certificate2 SelfSigned(string subject, DateTimeOffset from, DateTimeOffset until) {
    using var key = RSA.Create(2048);
    var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    return request.CreateSelfSigned(from, until);
  }

  #endregion

  #region the same arithmetic, written twice

  private static (int Checksum, int SecurityEntry, int Headers, (int Offset, int Size)[] Sections) Offsets(byte[] file) {
    var pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
    var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 6));
    var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 20));
    var optional = pe + 24;
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(optional));
    var headers = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(optional + 60));
    var securityEntry = optional + (magic == 0x020B ? 108 : 92) + 4 + (4 * 8);

    var table = optional + optionalSize;
    var sections = new List<(int, int)>(sectionCount);
    for (var i = 0; i < sectionCount; ++i) {
      var header = table + (i * 40);
      var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(header + 16));
      var offset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(header + 20));
      if (size > 0)
        sections.Add((offset, size));
    }

    return (optional + 64, securityEntry, headers, [.. sections]);
  }

  private static byte[] Digest(byte[] file, int checksum, int securityEntry, int headers, (int Offset, int Size)[] sections) {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    hash.AppendData(file, 0, checksum);
    hash.AppendData(file, checksum + 4, securityEntry - (checksum + 4));
    hash.AppendData(file, securityEntry + 8, headers - (securityEntry + 8));

    var ordered = sections.OrderBy(static section => section.Offset).ToArray();
    var hashed = headers;
    foreach (var (offset, size) in ordered) {
      hash.AppendData(file, offset, size);
      hashed += size;
    }

    if (file.Length > hashed)
      hash.AppendData(file, hashed, file.Length - hashed);

    return hash.GetHashAndReset();
  }

  /// <summary>The last byte of whichever section sits last in the file.</summary>
  private static int LastSectionByte(byte[] file) {
    var (_, _, _, sections) = Offsets(file);
    var last = sections.OrderBy(static section => section.Offset).Last();
    return last.Offset + last.Size - 1;
  }

  private static int CertificateTableOffset(byte[] file) {
    var (_, securityEntry, _, _) = Offsets(file);
    return (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(securityEntry));
  }

  #endregion

}
