namespace Hawkynt.ProcessManager.Model;

/// <summary>
/// Who says a running image is theirs (PRD §14).
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is nought on purpose. A default-constructed identity must not read as
/// <see cref="NotPackaged"/>: "no package claims this file" is a finding, and a record nobody filled
/// has found nothing (PRD §72.3).
/// </remarks>
public enum PackageSource : byte {

  /// <summary>Nobody asked, or nobody could.</summary>
  Unknown = 0,

  /// <summary>Asked, and no packaging system on this machine claims the file.</summary>
  None,

  /// <summary>Arch's <c>libalpm</c> database under <c>/var/lib/pacman/local</c>.</summary>
  Pacman,

  /// <summary>Debian's file lists under <c>/var/lib/dpkg/info</c>.</summary>
  Dpkg,

  /// <summary>A Flatpak sandbox, which says so in its own <c>.flatpak-info</c>.</summary>
  Flatpak,

  /// <summary>A snap, which says so in the name of the cgroup it is confined to.</summary>
  Snap,

  /// <summary>An AppImage, running out of the mount its own runtime made for it.</summary>
  AppImage,

  /// <summary>
  /// A Windows MSIX package, which the package manager names rather than the image (PRD §14).
  /// </summary>
  /// <remarks>
  /// Its own source and not "the distribution's own", because it is not the machine's ordinary way
  /// of installing anything: nearly every program on a Windows machine comes from no package at all,
  /// and a column that lumped an MSIX in with the Linux systems' own databases would be claiming a
  /// symmetry that is not there (PRD §5.3).
  /// </remarks>
  Msix,

}

/// <summary>
/// Which package a running image belongs to, and which application it is (PRD §14).
/// </summary>
/// <remarks>
/// <para>
/// Two questions with one answer here because on Linux they come from one lookup: the packaging
/// system that claims the file is also the only thing that knows the application id. A native
/// program has a package and no application id — nothing on an ordinary Linux system issues one —
/// and a Flatpak has both. Reporting the package name as an application id because the cell would
/// otherwise be empty is the false equivalence §5.3 exists to stop.
/// </para>
/// <para>
/// <paramref name="Reason"/> carries why there is nothing rather than leaving an empty string to be
/// read as "none" (PRD §72.3).
/// </para>
/// </remarks>
/// <param name="Version">
/// As the packaging system spells it, which is the string somebody will paste into a bug report.
/// </param>
/// <param name="ApplicationId">
/// The platform application id: a Flatpak's <c>org.gimp.GIMP</c>, a snap's name. Null for everything
/// else, because everything else has none.
/// </param>
public readonly record struct PackageIdentity(
  PackageSource Source,
  string? Name,
  string? Version,
  string? ApplicationId,
  UnknownReason Reason
) {

  /// <summary>Nobody has looked yet — the column was not asked for (PRD §5.4).</summary>
  public static readonly PackageIdentity NotChecked = new(PackageSource.Unknown, null, null, null, UnknownReason.NotSampledYet);

  /// <summary>Looked, and nothing on this machine claims the file.</summary>
  public static readonly PackageIdentity NotPackaged = new(PackageSource.None, null, null, null, UnknownReason.None);

  /// <summary>Could not look, and why.</summary>
  public static PackageIdentity Unknown(UnknownReason reason) => new(PackageSource.Unknown, null, null, null, reason);

  /// <summary>True when a packaging system answered, whether or not it claimed the file.</summary>
  public bool WasChecked => this.Source != PackageSource.Unknown;

  /// <summary>
  /// The cell: the package and its version, prefixed by the system that answered when that system
  /// is not the machine's own package manager.
  /// </summary>
  /// <remarks>
  /// A Flatpak and a distribution package are both "a package" and are not the same claim, so the
  /// cell says which one it is rather than leaving the reader to guess from the shape of a name.
  /// </remarks>
  public string? Text => this.Source switch {
    PackageSource.None => "not packaged",
    PackageSource.Pacman or PackageSource.Dpkg => Join(this.Name, this.Version),
    PackageSource.Flatpak => Prefix("flatpak", this.Name ?? this.ApplicationId, this.Version),
    PackageSource.Msix => Prefix("msix", this.Name, this.Version),
    PackageSource.Snap => Prefix("snap", this.Name, this.Version),
    PackageSource.AppImage => Prefix("appimage", this.Name, this.Version),
    _ => null,
  };

  private static string? Join(string? name, string? version) => name is not { Length: > 0 }
    ? null
    : version is { Length: > 0 } ? $"{name} {version}" : name;

  private static string? Prefix(string kind, string? name, string? version)
    => Join(name, version) is { } text ? $"{kind}: {text}" : kind;

}
