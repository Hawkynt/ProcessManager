namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// gid → group name, cached.
/// </summary>
/// <remarks>
/// The same trade <see cref="UserNameResolver"/> makes, for the same reasons and with the same cost:
/// <c>/etc/group</c> is read rather than <c>getgrgid_r</c> called, so a group that comes from LDAP,
/// SSSD or a container's own file stays a number. The gain is that the resolver is a pure function of
/// a file path and replays against a fixture like everything else in the probe (PRD §9.1), and that a
/// lookup can never block on a network directory.
/// <para>
/// Separate from the user resolver rather than a second dictionary inside it, because they are read
/// at different times: every sample resolves an owner, and only somebody who has opened the security
/// page resolves a group.
/// </para>
/// </remarks>
internal sealed class GroupNameResolver(string groupPath = "/etc/group") {

  private readonly Dictionary<int, string?> _cache = [];
  private bool _loaded;

  public string? Resolve(int gid) {
    if (gid < 0)
      return null;

    if (!this._loaded)
      this.Load();

    return this._cache.TryGetValue(gid, out var name) ? name : null;
  }

  /// <summary>
  /// Forgets everything. A group added while the program is running would otherwise stay numeric for
  /// as long as it runs.
  /// </summary>
  public void Invalidate() {
    this._cache.Clear();
    this._loaded = false;
  }

  private void Load() {
    this._loaded = true;
    string[] lines;
    try {
      lines = File.ReadAllLines(groupPath);
    } catch (IOException) {
      return;
    } catch (UnauthorizedAccessException) {
      return;
    }

    foreach (var line in lines) {
      // name:x:gid:members
      var fields = line.Split(':');
      if (fields.Length < 3 || !int.TryParse(fields[2], out var gid))
        continue;

      this._cache[gid] = fields[0];
    }
  }

}
