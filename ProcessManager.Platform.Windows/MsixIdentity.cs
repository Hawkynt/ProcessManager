using Hawkynt.ProcessManager.Model;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// What an MSIX package's names say about it (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Split out from <see cref="WindowsIdentityResolver"/> and carrying no platform attribute, for the
/// same reason <see cref="SystemProcessInformationReader"/> does: it touches no Windows API — it
/// takes two strings the package manager handed back and reads them — so it runs on every CI leg
/// rather than only where nobody can debug it (PRD §9.4).
/// </para>
/// <para>
/// A package full name is <c>name_version_architecture__publisherhash</c>: five fields joined by
/// underscores, with an empty one between the architecture and the hash. The family name is
/// <c>name_publisherhash</c> — the two halves that survive every version of the package, which is
/// exactly what an application id has to be, and which is why it is asked for separately rather than
/// cut out of the full name.
/// </para>
/// </remarks>
internal static class MsixIdentity {

  /// <summary>
  /// The package, from the two names the package manager gave for it.
  /// </summary>
  /// <param name="fullName">
  /// The package full name, or <see langword="null"/> when the process came from no package.
  /// </param>
  /// <param name="familyName">The package family name, or <see langword="null"/> when it would not answer.</param>
  /// <remarks>
  /// A full name that is not in the documented shape is shown whole rather than cut at a separator
  /// it does not have: reporting the first half of a name nobody recognises is worse than reporting
  /// the name (PRD §5.3).
  /// </remarks>
  public static PackageIdentity Describe(string? fullName, string? familyName) {
    if (fullName is not { Length: > 0 })
      return PackageIdentity.NotPackaged;

    string? name = null;
    string? version = null;
    var parts = fullName.Split('_');
    if (parts.Length >= 2 && parts[0].Length > 0) {
      name = parts[0];
      version = parts[1].Length > 0 ? parts[1] : null;
    }

    return new(
      PackageSource.Msix,
      name ?? fullName,
      version,
      familyName is { Length: > 0 } ? familyName : null,
      UnknownReason.None
    );
  }

}
