using Hawkynt.ProcessManager.Model;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hawkynt.ProcessManager.Platform.Windows;

/// <summary>
/// Everything about a process that is fixed for its lifetime and needs a handle to read (PRD §14,
/// §21).
/// </summary>
/// <param name="UserName">The owner, or null when the process would not open.</param>
/// <param name="UserId">The SID's relative identifier, or -1.</param>
/// <param name="Elevated">1 when the token is elevated, 0 when it is not.</param>
/// <param name="Integrity">The mandatory label's level.</param>
/// <param name="ProtectionLevel">The <c>PROTECTION_LEVEL_*</c> value; <c>0xFFFFFFFE</c> is none.</param>
/// <param name="IsAppContainer">1 when the token belongs to an AppContainer.</param>
/// <param name="Emulation">
/// The machine the process is being translated from, or 0 when it is running natively.
/// </param>
/// <param name="ImagePath">
/// The full path of the running image, which the bulk query does not carry — it holds the file name
/// alone — and which a great many other answers are keyed on.
/// </param>
internal readonly record struct WindowsProcessIdentity(
  string? UserName,
  int UserId,
  Counter Elevated,
  Counter Integrity,
  Counter ProtectionLevel,
  Counter IsAppContainer,
  Counter Emulation,
  string? ImagePath
) {

  /// <summary>What a process that would not open at all looks like: no answers, and a reason.</summary>
  public static readonly WindowsProcessIdentity Refused = new(
    null,
    -1,
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted,
    Counter.NotPermitted,
    null
  );

}

/// <summary>
/// The six Windows per-process mitigation policies, each as the flags word its structure carries
/// (PRD §21).
/// </summary>
/// <remarks>
/// The word and no interpretation of it. What the bits mean is decided where the column is rendered,
/// which is portable code and is therefore tested on every leg rather than only on Windows
/// (PRD §9.4).
/// </remarks>
internal readonly record struct WindowsMitigations(
  Counter Dep,
  Counter Aslr,
  Counter ControlFlowGuard,
  Counter ShadowStack,
  Counter DynamicCode,
  Counter BinarySignature
) {

  /// <summary>All six as one reason, for the two cases where none of them was read.</summary>
  private static WindowsMitigations All(Counter reading) => new(reading, reading, reading, reading, reading, reading);

  /// <summary>Nobody named one of the six columns, so nobody paid for the six calls (PRD §5.4).</summary>
  public static readonly WindowsMitigations NotAsked = All(Counter.NotSampledYet);

  /// <summary>The process would not open for the stronger right these need.</summary>
  public static readonly WindowsMitigations Refused = All(Counter.NotPermitted);

}

/// <summary>
/// Resolves the owner of a process and the rest of its fixed identity, cached.
/// </summary>
/// <remarks>
/// <para>
/// None of what this reads changes while a process runs, so it is looked up once per process and
/// kept for as long as that process lives — which matters, because the lookup opens the process and
/// PRD §5.2 says there is no <c>OpenProcess</c> in the sampling loop. There is not: only new
/// processes pay, and only once.
/// </para>
/// <para>
/// One open answers seven questions. The owner, the elevation, the integrity level and the
/// AppContainer flag come off the token; the protection level and the emulated machine come off the
/// process handle itself. Every one of them needs only
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is the right that succeeds for another user's
/// process — so the answers are there for most of a machine's table rather than only for this
/// user's own rows.
/// </para>
/// <para>
/// The mitigation policies are the exception and are read separately. They need
/// <c>PROCESS_QUERY_INFORMATION</c>, which is a stronger right and a second open, for six more
/// calls — so they happen only when a column or a filter names one of the six (PRD §5.4).
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

  private readonly record struct Resolved(ulong StartTicks, WindowsProcessIdentity Identity, WindowsMitigations Mitigations);

  /// <summary>Whether <c>IsWow64Process2</c> exists on this Windows. Before 1709 it does not.</summary>
  private bool _emulationUnavailable;

  /// <summary>
  /// Everything fixed about the process, from one open, cached for its lifetime.
  /// </summary>
  /// <param name="readMitigations">
  /// Whether anything this run asked for the mitigation policies. They are a second open with a
  /// stronger access right and six more calls, so nothing pays for them unless a column names one
  /// (PRD §5.4).
  /// </param>
  public WindowsProcessIdentity Resolve(int pid, ulong startTicks, bool readMitigations, out WindowsMitigations mitigations) {
    mitigations = WindowsMitigations.NotAsked;
    if (this._byPid.TryGetValue(pid, out var cached)) {
      if (cached.StartTicks == startTicks) {
        // A run that has only just been asked for the mitigations finds a cache entry without them,
        // and must fill it in rather than reporting for ever that nobody asked.
        if (readMitigations && cached.Mitigations == WindowsMitigations.NotAsked) {
          cached = cached with { Mitigations = ReadMitigations(pid) };
          this._byPid[pid] = cached;
        }

        mitigations = cached.Mitigations;
        return cached.Identity;
      }

      // Same pid, different process. Windows recycles pids eagerly, so this is not rare, and reusing
      // the entry would attribute one user's process to another (PRD §3.2).
      this._byPid.Remove(pid);
      this._refused.Remove(pid);
    }

    if (this._refused.Contains(pid))
      return WindowsProcessIdentity.Refused;

    var identity = this.Read(pid);
    if (identity is null) {
      // Remembered so that a system process we may not open is not re-attempted every second for as
      // long as the program runs.
      this._refused.Add(pid);
      return WindowsProcessIdentity.Refused;
    }

    mitigations = readMitigations ? ReadMitigations(pid) : WindowsMitigations.NotAsked;
    this._byPid[pid] = new(startTicks, identity.Value, mitigations);
    return identity.Value;
  }

  /// <summary>Drops entries for processes no longer present, so the cache tracks the machine.</summary>
  public void Prune(HashSet<int> livePids) {
    if (this._byPid.Count < 4096 && this._refused.Count < 4096)
      return;

    foreach (var pid in this._byPid.Keys.Where(pid => !livePids.Contains(pid)).ToList())
      this._byPid.Remove(pid);

    this._refused.RemoveWhere(pid => !livePids.Contains(pid));
  }

  /// <summary>
  /// One open, seven answers.
  /// </summary>
  /// <remarks>
  /// Null only when the process itself would not open, which is the one case where nothing at all
  /// can be said. Everything below that is a per-question answer: a token that opens but refuses one
  /// class leaves that one counter unknown and the rest filled, because "we could not read the
  /// integrity level" and "we could not see this process" are different findings (PRD §72.3).
  /// </remarks>
  private WindowsProcessIdentity? Read(int pid) {
    var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (process == 0)
      return null;

    try {
      var protection = ReadProtectionLevel(process);
      var emulation = this.ReadEmulation(process);
      var imagePath = ReadImagePath(process);

      if (!Native.OpenProcessToken(process, Native.TOKEN_QUERY, out var token))
        return new(
          null,
          -1,
          Counter.NotPermitted,
          Counter.NotPermitted,
          protection,
          Counter.NotPermitted,
          emulation,
          imagePath
        );

      try {
        var sid = ReadSid(token);
        var name = sid is null ? null : this.LookupCached(sid.Value.Text);
        return new(
          name,
          sid?.RelativeId ?? -1,
          ReadElevation(token),
          ReadIntegrity(token),
          protection,
          ReadAppContainer(token),
          emulation,
          imagePath
        );
      } finally {
        Native.CloseHandle(token);
      }
    } finally {
      Native.CloseHandle(process);
    }
  }

  private string? LookupCached(string sid) {
    if (this._bySid.TryGetValue(sid, out var name))
      return name;

    name = LookupName(sid);
    this._bySid[sid] = name;
    return name;
  }

  /// <param name="RelativeId">
  /// The trailing number of the SID — 1000 and up for real accounts, and the closest thing Windows
  /// has to a uid for a row that has to show a number when the name lookup fails.
  /// </param>
  private readonly record struct Sid(string Text, int RelativeId);

  private static Sid? ReadSid(nint token) {
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

        var lastDash = text.LastIndexOf('-');
        var relativeId = lastDash >= 0 && int.TryParse(text[(lastDash + 1)..], out var rid) ? rid : -1;
        return new(text, relativeId);
      } finally {
        Native.LocalFree(stringSid);
      }
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>TOKEN_ELEVATION is a single DWORD, so the buffer size is known.</summary>
  private static Counter ReadElevation(nint token) => ReadTokenFlag(token, Native.TokenElevation);

  /// <summary>TOKEN_IS_APP_CONTAINER is a single DWORD too, and reads the same way.</summary>
  private static Counter ReadAppContainer(nint token) => ReadTokenFlag(token, Native.TokenIsAppContainer);

  private static Counter ReadTokenFlag(nint token, int informationClass) {
    var buffer = Marshal.AllocHGlobal(sizeof(int));
    try {
      return Native.GetTokenInformation(token, informationClass, buffer, sizeof(int), out _)
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

  /// <summary>
  /// The full path of the running image.
  /// </summary>
  /// <remarks>
  /// The buffer is the maximum an extended path can be rather than <c>MAX_PATH</c>: a service
  /// running out of a deep directory really does exceed 260 characters, and a truncated path is
  /// worse than none because it looks like a path.
  /// </remarks>
  private static string? ReadImagePath(nint process) {
    Span<char> buffer = stackalloc char[1024];
    var size = (uint)buffer.Length;
    if (!Native.QueryFullProcessImageNameW(process, 0, ref MemoryMarshal.GetReference(buffer), ref size))
      return null;

    // The size comes back as the character count without the terminator.
    var length = (int)Math.Min(size, (uint)buffer.Length);
    return length > 0 ? new string(buffer[..length]) : null;
  }

  /// <summary>
  /// The protected-process level, through the documented Win32 call rather than the undocumented one.
  /// </summary>
  /// <remarks>
  /// <c>GetProcessInformation(ProcessProtectionLevelInfo)</c> needs only
  /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and is a documented API;
  /// <c>NtQueryInformationProcess(ProcessProtectionInformation)</c> would answer the same question
  /// through a structure Microsoft has never published, and this program does not read structures
  /// nobody can check (PRD §8.3).
  /// </remarks>
  private static Counter ReadProtectionLevel(nint process) {
    var buffer = Marshal.AllocHGlobal(sizeof(uint));
    try {
      return Native.GetProcessInformation(process, Native.ProcessProtectionLevelInfo, buffer, sizeof(uint))
        ? Counter.Of((uint)Marshal.ReadInt32(buffer))
        : Counter.NotPermitted;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// Which instruction set the process is being translated from, or nought for none.
  /// </summary>
  /// <remarks>
  /// Nought is a real answer and the ordinary one, so it is returned as a value rather than as an
  /// unknown. A Windows older than 1709 does not export the call at all, which is a fact about the
  /// machine and not about the process — it is remembered once so the miss is not paid for every
  /// process on the table, and every row then reads as unsupported rather than as native
  /// (PRD §72.3).
  /// </remarks>
  private Counter ReadEmulation(nint process) {
    if (this._emulationUnavailable)
      return Counter.NotSupported;

    try {
      return Native.IsWow64Process2(process, out var machine, out _)
        ? Counter.Of(machine)
        : Counter.NotPermitted;
    } catch (EntryPointNotFoundException) {
      this._emulationUnavailable = true;
      return Counter.NotSupported;
    }
  }

  /// <summary>
  /// The six mitigation policies, through a second open with the stronger right they need.
  /// </summary>
  /// <remarks>
  /// <c>GetProcessMitigationPolicy</c> documents <c>PROCESS_QUERY_INFORMATION</c> rather than the
  /// limited form, which means that for another user's process this will usually fail — and it says
  /// so, per policy, rather than reporting six mitigations as absent. A mitigation reported off when
  /// nobody could look is the worst possible cell on this row (PRD §72.3).
  /// </remarks>
  private static WindowsMitigations ReadMitigations(int pid) {
    var process = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION, false, pid);
    if (process == 0)
      return WindowsMitigations.Refused;

    try {
      return new(
        // The DEP structure is the one of the six that is not just the flags word: it carries a
        // BOOLEAN after the union. Both are read, and the boolean is kept above the word.
        ReadDep(process),
        ReadPolicy(process, Native.ProcessASLRPolicy),
        ReadPolicy(process, Native.ProcessControlFlowGuardPolicy),
        ReadPolicy(process, Native.ProcessUserShadowStackPolicy),
        ReadPolicy(process, Native.ProcessDynamicCodePolicy),
        ReadPolicy(process, Native.ProcessSignaturePolicy)
      );
    } finally {
      Native.CloseHandle(process);
    }
  }

  /// <summary>
  /// One policy's flags word.
  /// </summary>
  /// <remarks>
  /// Every one of these structures is a union of a <c>DWORD Flags</c> with a bitfield of the same
  /// four bytes, so four bytes is the whole of what there is to read and the bitfield never has to
  /// be expressed in C# at all. A policy this Windows does not know — the shadow stack one on
  /// anything before Windows 10 2004 — fails the call rather than filling the buffer, and says so.
  /// </remarks>
  private static Counter ReadPolicy(nint process, int policy) {
    var buffer = Marshal.AllocHGlobal(sizeof(uint));
    try {
      Marshal.WriteInt32(buffer, 0);
      return Native.GetProcessMitigationPolicy(process, policy, buffer, sizeof(uint))
        ? Counter.Of((uint)Marshal.ReadInt32(buffer))
        : Counter.NotPermitted;
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
  }

  /// <summary>
  /// The DEP policy, whose structure is a flags word <em>and</em> a trailing <c>BOOLEAN Permanent</c>.
  /// </summary>
  /// <remarks>
  /// Eight bytes are asked for rather than four, because the structure is four bytes of union
  /// followed by a byte the compiler pads out. Permanent is carried in bit 32 of the counter — above
  /// everything the flags word itself can occupy, so nothing is lost and nothing is invented.
  /// </remarks>
  private static Counter ReadDep(nint process) {
    var buffer = Marshal.AllocHGlobal(8);
    try {
      Marshal.WriteInt64(buffer, 0);
      if (!Native.GetProcessMitigationPolicy(process, Native.ProcessDEPPolicy, buffer, 8))
        return Counter.NotPermitted;

      var flags = (uint)Marshal.ReadInt32(buffer);
      var permanent = Marshal.ReadByte(buffer, 4) != 0;
      return Counter.Of(permanent ? flags | (1ul << 32) : flags);
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
