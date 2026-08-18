using Hawkynt.ProcessManager.Model;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Resolves the owner of a process, cached.
/// </summary>
/// <remarks>
/// <para>
/// A process's token does not change, so this is looked up once per process and kept for as long as
/// that process lives — which matters, because the lookup is four calls including an
/// <c>OpenProcess</c>, and PRD §5.2 says there is no <c>OpenProcess</c> in the sampling loop. There
/// is not: only new processes pay, and only once.
/// </para>
/// <para>
/// The SID is cached separately from the pid, because a machine runs hundreds of processes belonging
/// to a handful of accounts and <c>LookupAccountSid</c> can go to a domain controller. One trip per
/// account, not per process.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIdentityResolver {

  private readonly Dictionary<int, Resolved> _byPid = [];
  private readonly Dictionary<string, string?> _bySid = [];
  private readonly HashSet<int> _refused = [];

  private readonly record struct Resolved(ulong StartTicks, string? Name, int RelativeId, Counter Elevated, Counter Integrity);

  /// <summary>
  /// The owner's account name, or null when the process would not open. Also yields the SID's
  /// relative identifier as a numeric owner id, so a row can show <em>something</em> stable when the
  /// name is not available.
  /// </summary>
  /// <summary>
  /// The owner, and the two security answers that came out of the same token.
  /// </summary>
  public string? Resolve(int pid, ulong startTicks, out int userId, out Counter elevated, out Counter integrity) {
    elevated = Counter.NotPermitted;
    integrity = Counter.NotPermitted;
    var name = this.ResolveCore(pid, startTicks, out userId);
    if (this._byPid.TryGetValue(pid, out var resolved)) {
      elevated = resolved.Elevated;
      integrity = resolved.Integrity;
    }

    return name;
  }

  public string? Resolve(int pid, ulong startTicks, out int userId) => this.ResolveCore(pid, startTicks, out userId);

  private string? ResolveCore(int pid, ulong startTicks, out int userId) {
    userId = -1;
    if (this._byPid.TryGetValue(pid, out var cached)) {
      if (cached.StartTicks == startTicks) {
        userId = cached.RelativeId;
        return cached.Name;
      }

      // Same pid, different process. Windows recycles pids eagerly, so this is not rare, and reusing
      // the entry would attribute one user's process to another (PRD §3.2).
      this._byPid.Remove(pid);
      this._refused.Remove(pid);
    }

    if (this._refused.Contains(pid))
      return null;

    var sid = TryReadSid(pid);
    if (sid is null) {
      // Remembered so that a system process we may not open is not re-attempted every second for as
      // long as the program runs.
      this._refused.Add(pid);
      return null;
    }

    if (!this._bySid.TryGetValue(sid.Value.Text, out var name)) {
      name = LookupName(sid.Value.Text);
      this._bySid[sid.Value.Text] = name;
    }

    this._byPid[pid] = new(startTicks, name, sid.Value.RelativeId, sid.Value.Elevated, sid.Value.Integrity);
    userId = sid.Value.RelativeId;
    return name;
  }

  /// <summary>Drops entries for processes no longer present, so the cache tracks the machine.</summary>
  public void Prune(HashSet<int> livePids) {
    if (this._byPid.Count < 4096 && this._refused.Count < 4096)
      return;

    foreach (var pid in this._byPid.Keys.Where(pid => !livePids.Contains(pid)).ToList())
      this._byPid.Remove(pid);

    this._refused.RemoveWhere(pid => !livePids.Contains(pid));
  }

  /// <param name="Elevated">1 when the token is elevated, 0 when it is not.</param>
  /// <param name="Integrity">
  /// The mandatory label's level: 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system.
  /// </param>
  private readonly record struct Sid(string Text, int RelativeId, Counter Elevated, Counter Integrity);

  /// <summary>
  /// Reads the owner, the elevation and the integrity level from one token.
  /// </summary>
  /// <remarks>
  /// All three from a single OpenProcess/OpenProcessToken pair, because opening a token per process
  /// is the expensive part and doing it three times would be three times the cost for no more
  /// information. Cached by pid and start time like the name, since none of the three changes during
  /// a process's life.
  /// </remarks>
  private static Sid? TryReadSid(int pid) {
    var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (process == 0)
      return null;

    try {
      if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
        return null;

      try {
        // Ask for the size first: TOKEN_USER is a header plus a variable-length SID, so there is no
        // fixed buffer that is always right.
        Native.GetTokenInformation(token, Native.TokenUser, 0, 0, out var needed);
        if (needed == 0)
          return null;

        var buffer = Marshal.AllocHGlobal((int)needed);
        try {
          if (!Native.GetTokenInformation(token, Native.TokenUser, buffer, needed, out _))
            return null;

          var sidPointer = Marshal.ReadIntPtr(buffer);
          if (sidPointer == 0 || !Native.ConvertSidToStringSidW(sidPointer, out var stringSid))
            return null;

          try {
            var text = Marshal.PtrToStringUni(stringSid);
            if (text is null)
              return null;

            // The relative identifier is the trailing number of the SID — 1000 and up for real
            // accounts, and the closest thing Windows has to a uid for a row that has to show a
            // number when the name lookup fails.
            var lastDash = text.LastIndexOf('-');
            var relativeId = lastDash >= 0 && int.TryParse(text[(lastDash + 1)..], out var rid) ? rid : -1;
            return new(text, relativeId, ReadElevation(token), ReadIntegrity(token));
          } finally {
            Native.LocalFree(stringSid);
          }
        } finally {
          Marshal.FreeHGlobal(buffer);
        }
      } finally {
        Native.CloseHandle(token);
      }
    } finally {
      Native.CloseHandle(process);
    }
  }

  /// <summary>TOKEN_ELEVATION is a single DWORD, so the buffer size is known.</summary>
  private static Counter ReadElevation(nint token) {
    var buffer = Marshal.AllocHGlobal(sizeof(int));
    try {
      return Native.GetTokenInformation(token, Native.TokenElevation, buffer, sizeof(int), out _)
        ? Counter.Of(Marshal.ReadInt32(buffer) != 0 ? 1ul : 0ul)
        : Counter.NotPermitted;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// The integrity level is the <em>last</em> sub-authority of the mandatory label's SID.
  /// </summary>
  /// <remarks>
  /// Read out of the SID's own bytes rather than through GetSidSubAuthority: a SID is
  /// revision, sub-authority count, six identifier-authority bytes, then that many little-endian
  /// 32-bit sub-authorities. The count byte is what says where the last one is, and assuming a
  /// fixed position would read the wrong number on any label that ever gains one.
  /// </remarks>
  private static Counter ReadIntegrity(nint token) {
    Native.GetTokenInformation(token, Native.TokenIntegrityLevel, 0, 0, out var needed);
    if (needed == 0)
      return Counter.NotPermitted;

    var buffer = Marshal.AllocHGlobal((int)needed);
    try {
      if (!Native.GetTokenInformation(token, Native.TokenIntegrityLevel, buffer, needed, out _))
        return Counter.NotPermitted;

      // TOKEN_MANDATORY_LABEL is a SID_AND_ATTRIBUTES, whose first field is the SID pointer.
      var sid = Marshal.ReadIntPtr(buffer);
      if (sid == 0)
        return Counter.NotPermitted;

      // The count byte says how long the SID is, so the span is sized from the structure itself
      // rather than guessed — and IntegrityFromSid then bounds-checks against that length.
      var subAuthorityCount = Marshal.ReadByte(sid, 1);
      var length = 8 + (subAuthorityCount * 4);
      unsafe {
        return TokenSid.IntegrityFromSid(new ReadOnlySpan<byte>((void*)sid, length));
      }
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  private static string? LookupName(string stringSid) {
    if (!Native.ConvertStringSidToSidW(stringSid, out var sid))
      return null;

    try {
      Span<char> name = stackalloc char[256];
      Span<char> domain = stackalloc char[256];
      var nameLength = (uint)name.Length;
      var domainLength = (uint)domain.Length;
      if (!Native.LookupAccountSidW(
            null,
            sid,
            ref MemoryMarshal.GetReference(name),
            ref nameLength,
            ref MemoryMarshal.GetReference(domain),
            ref domainLength,
            out _
          ))
        return null;

      // DOMAIN\user is what every Windows tool shows, and the domain is what distinguishes a local
      // account from a domain one with the same name. The lengths come back as character counts
      // without the terminator.
      var account = new string(name[..(int)Math.Min(nameLength, (uint)name.Length)]);
      var authority = new string(domain[..(int)Math.Min(domainLength, (uint)domain.Length)]);
      return authority.Length > 0 ? $"{authority}\\{account}" : account;
    } finally {
      Native.LocalFree(sid);
    }
  }

}
