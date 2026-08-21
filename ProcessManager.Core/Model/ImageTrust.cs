namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// The verdict vocabulary of PRD §70 — exactly these words, and no synonyms.
/// </summary>
/// <remarks>
/// <para>
/// One vocabulary for every platform, so that "Verified" means the same thing in every window,
/// every export and every filter. What was verified differs per platform and is never smuggled into
/// the word: an Authenticode signature on Windows, and on Linux the packaging system's own record of
/// the bytes it shipped. Which of the two answered is in <see cref="ImageTrust.Detail"/>, in a
/// sentence, where it can be read rather than inferred.
/// </para>
/// <para>
/// The same eight words answer each of §70's questions separately, and the question is the slot
/// rather than the word. "Verified" in <see cref="ImageTrust.Signature"/> says the bytes are the
/// ones that were recorded; "Verified" in <see cref="ImageTrust.TrustChain"/> says somebody this
/// machine trusts signed for them. They are routinely not the same answer — a locally built package
/// is the first and not the second — and the whole point of §70's first requirement is that one
/// word must never be allowed to stand for both.
/// </para>
/// <para>
/// <see cref="NotChecked"/> is nought, because a record nobody filled has checked nothing. Anything
/// else as the default would make an unfilled field an assertion (PRD §72.3).
/// </para>
/// </remarks>
public enum SignatureStatus : byte {

  /// <summary>Nobody asked. Verification is opt-in and costs a hash of the file (PRD §5.4).</summary>
  NotChecked = 0,

  /// <summary>The signature is good and its chain is trusted.</summary>
  Verified,

  /// <summary>
  /// The signature itself is good; whoever made it is not somebody this machine trusts.
  /// </summary>
  ValidButUntrustedChain,

  /// <summary>There is no signature at all. Not a failure — most of a Linux system is like this.</summary>
  Unsigned,

  /// <summary>There is a signature and it does not match the bytes.</summary>
  InvalidSignature,

  /// <summary>The signature was good and the key behind it has since been withdrawn.</summary>
  Revoked,

  /// <summary>The signature was good and the certificate behind it has run out.</summary>
  Expired,

  /// <summary>Checking was attempted and failed — unreadable file, unreadable database.</summary>
  VerificationError,

}

/// <summary>The vocabulary as text. One place, so no front-end can invent a synonym (PRD §70).</summary>
public static class SignatureStatusText {

  public static string Text(this SignatureStatus status) => status switch {
    SignatureStatus.Verified => "Verified",
    SignatureStatus.ValidButUntrustedChain => "Valid but untrusted chain",
    SignatureStatus.Unsigned => "Unsigned",
    SignatureStatus.InvalidSignature => "Invalid signature",
    SignatureStatus.Revoked => "Revoked",
    SignatureStatus.Expired => "Expired",
    SignatureStatus.VerificationError => "Verification error",
    _ => "Not checked",
  };

}

/// <summary>
/// What is known about an image's provenance, with each of §70's five questions in its own slot.
/// </summary>
/// <remarks>
/// <para>
/// PRD §70's first requirement is that the program never conflates hash calculation, local
/// signature verification, trust-chain verification, an online reputation query and file
/// submission. They are separate fields here rather than one "status", because one status is
/// exactly how they get conflated: a hash that matched becomes "verified", a package that shipped
/// the file becomes "trusted", and a reader has no way left to tell which question was answered.
/// </para>
/// <para>
/// <b>A hash is not a verdict.</b> <see cref="Sha256"/> says what the bytes are and nothing about
/// whether anybody vouches for them.
/// </para>
/// </remarks>
/// <param name="Sha256">
/// Hash calculation, question one. Present only when somebody asked for it; it is the one reading
/// here whose cost is the size of the file.
/// </param>
/// <param name="Package">Where the bytes came from, if anything claims them.</param>
/// <param name="Signature">
/// Local signature verification, question two: whether the bytes are the ones a signature covers.
/// On Linux there is no signature inside an ELF to check, so this is the packaging system's
/// answer — the running file against the digest its package recorded, which is the comparison
/// <c>pacman -Qkk</c> and <c>dpkg --verify</c> make, and nothing more than that.
/// </param>
/// <param name="Detail">
/// One sentence naming what was actually checked, so the word in <paramref name="Signature"/> is
/// never the whole story.
/// </param>
/// <param name="TrustChain">
/// Trust-chain verification, question three: whether the key behind the signature chains to
/// something this machine trusts. Deliberately its own slot and never inferred from
/// <paramref name="Signature"/> — a good signature by an unknown key is a different answer from a
/// good signature by a known one.
/// <para>
/// On Linux this is what the packaging system recorded about the package itself: <c>pacman</c>
/// writes <c>%VALIDATION%</c>, which is the same fact <c>pacman -Qi</c> prints as "Validated By".
/// Folding it into <paramref name="Signature"/> is how this program used to report a locally built
/// package whose files were untouched as "Unsigned" — one word carrying two findings, which is
/// exactly what §70's first requirement forbids.
/// </para>
/// </param>
/// <param name="ChainDetail">
/// One sentence naming what stands behind the package, for the same reason
/// <paramref name="Detail"/> exists.
/// </param>
/// <param name="ChainReason">
/// Why there is no chain verdict, when there is none. A packaging system that keeps no record of a
/// signature over an installed file has not failed to check — it has no such concept, and
/// <see cref="UnknownReason.NotSupportedOnPlatform"/> is a different statement from
/// <see cref="SignatureStatus.Unsigned"/> (PRD §72.3).
/// </param>
/// <param name="Reputation">
/// Online reputation, question four. Always <see cref="SignatureStatus.NotChecked"/> until somebody
/// configures a provider: §3 promises this program transmits nothing about an executable without
/// being asked, and there is no provider to ask (PRD §70, §97).
/// </param>
/// <param name="Submitted">
/// File submission, question five — sending the bytes themselves somewhere. Never true: nothing in
/// this program uploads a file, and the field exists so that "we hashed it" can never be read as
/// "we sent it".
/// </param>
/// <param name="Summary">
/// What the packaging system says the package is for, in one line. §31 asks a mapped image for its
/// description, and an ELF has nowhere to keep one: there is no counterpart to a Windows version
/// resource in the format, so what a Linux machine publishes about a file is what the database that
/// installed it publishes about its package. Null where nothing claims the file, and where the
/// claim carries no such line (PRD §5.3).
/// </param>
/// <param name="Publisher">
/// Who assembled the package — <c>pacman</c>'s packager, <c>dpkg</c>'s maintainer. §31's "company",
/// answered the only way this machine can answer it. Not a signer: nobody signed the file, and this
/// name is the party the database records rather than a party any signature names.
/// </param>
public sealed record ImageTrust(
  string? Sha256,
  PackageIdentity Package,
  SignatureStatus Signature,
  string? Detail,
  SignatureStatus TrustChain = SignatureStatus.NotChecked,
  string? ChainDetail = null,
  UnknownReason ChainReason = UnknownReason.NotSampledYet,
  SignatureStatus Reputation = SignatureStatus.NotChecked,
  bool Submitted = false,
  string? Summary = null,
  string? Publisher = null
) {

  /// <summary>Nothing has been asked of this image yet.</summary>
  public static readonly ImageTrust NotChecked = new(
    null,
    PackageIdentity.NotChecked,
    SignatureStatus.NotChecked,
    null
  );

}
