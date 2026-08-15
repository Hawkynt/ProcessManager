namespace Hawkynt.ProcessManager.Platform.Linux;

/// <summary>
/// uid → login name, cached.
/// </summary>
/// <remarks>
/// Reads <c>/etc/passwd</c> rather than calling <c>getpwuid_r</c>. The trade is deliberate and has a
/// cost worth stating: a machine whose users come from LDAP, SSSD or systemd-homed through NSS will
/// show numeric uids for those accounts, because they are not in the file. The gain is that the
/// resolver is a pure function of a file path, so it replays against a fixture like everything else
/// in the probe (PRD §9.1), and that a name lookup can never block the sampling thread on a network
/// directory — which is a real failure mode of the "correct" call.
/// </remarks>
internal sealed class UserNameResolver(string passwdPath = "/etc/passwd") {

  private readonly Dictionary<int, string?> _cache = [];
  private bool _loaded;

  public string? Resolve(int uid) {
    if (uid < 0)
      return null;

    if (!this._loaded)
      this.Load();

    return this._cache.TryGetValue(uid, out var name) ? name : null;
  }

  /// <summary>
  /// Forgets everything. A user added while the program is running would otherwise stay numeric for
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
      lines = File.ReadAllLines(passwdPath);
    } catch (IOException) {
      return;
    } catch (UnauthorizedAccessException) {
      return;
    }

    foreach (var line in lines) {
      // name:x:uid:gid:gecos:home:shell
      var fields = line.Split(':');
      if (fields.Length < 3 || !int.TryParse(fields[2], out var uid))
        continue;

      this._cache[uid] = fields[0];
    }
  }

}
