using System.Buffers.Binary;
using System.Text;
using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// The columns a login record does not carry (PRD §43).
/// </summary>
/// <remarks>
/// <para>
/// utmp has a user, a line, a host, a pid and a time and has never had anything else. The session
/// id, the type, the state, the idle time and the account's own name all come from the machine
/// around it, and each has a way of being wrong that a recorded file alone would not catch.
/// </para>
/// <para>
/// The tree is built here rather than checked in because every one of these answers is about the
/// <em>relationship</em> between a login record and the machine: a pid that is there against one
/// that is not, a terminal with a timestamp against one without. A fixture would fix the record and
/// leave the machine, which is the half being tested. It is written into a directory this class
/// creates and deletes, and nothing outside it is touched.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SessionDetailTests {

  private string _root = string.Empty;

  [SetUp]
  public void Setup() {
    this._root = Path.Combine(Path.GetTempPath(), "procman-sessions-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(this._root);

    // A leader that is still there, in a session scope, so the id can be read off it.
    var live = Path.Combine(this._root, "proc", "4100");
    Directory.CreateDirectory(live);
    File.WriteAllText(Path.Combine(live, "cgroup"), "0::/user.slice/user-1000.slice/session-7.scope\n");

    // A leader that is there and is in no session scope: a login systemd did not open. Its id must
    // come back null rather than being borrowed from somewhere above it.
    var adopted = Path.Combine(this._root, "proc", "4200");
    Directory.CreateDirectory(adopted);
    File.WriteAllText(Path.Combine(adopted, "cgroup"), "0::/system.slice/getty@tty2.service\n");

    Directory.CreateDirectory(Path.Combine(this._root, "dev", "pts"));
    var terminal = Path.Combine(this._root, "dev", "pts", "0");
    File.WriteAllText(terminal, string.Empty);
    File.SetLastWriteTimeUtc(terminal, DateTime.UtcNow.AddHours(-3));

    File.WriteAllText(
      Path.Combine(this._root, "passwd"),
      "root:x:0:0:root:/root:/bin/bash\n"
      + "dana:x:1000:1000:Dana Marsh,Room 12,555-0100:/home/dana:/bin/bash\n"
      // No description at all, which is the ordinary case for a service account and must read as
      // "there is none" rather than as an empty name.
      + "nobody:x:65534:65534::/:/usr/bin/nologin\n"
    );

    File.WriteAllBytes(Path.Combine(this._root, "utmp"), Utmp([
      // A live login on a pseudo-terminal from a local display: a terminal window.
      new("dana", "pts/0", ":1", 4100),
      // A login whose leader has gone. The record outlived it, which is what a stale login is.
      new("dana", "pts/9", "10.0.0.5", 4999),
      // A console login whose leader systemd did not adopt into a session scope.
      new("nobody", "tty2", null, 4200),
    ]));
  }

  [TearDown]
  public void Cleanup() {
    if (this._root.Length > 0 && Directory.Exists(this._root))
      Directory.Delete(this._root, recursive: true);
  }

  private IReadOnlyList<SessionRecord> Sessions() {
    using var probe = new LinuxProbe(new() {
      ProcRoot = Path.Combine(this._root, "proc"),
      UtmpPath = Path.Combine(this._root, "utmp"),
      PasswdPath = Path.Combine(this._root, "passwd"),
      DeviceRoot = Path.Combine(this._root, "dev"),
      EffectiveUserId = 0,
    });

    return probe.GetSessions();
  }

  private SessionRecord On(string terminal) {
    foreach (var session in this.Sessions())
      if (session.Terminal == terminal)
        return session;

    Assert.Fail($"no session on '{terminal}'");
    return default;
  }

  /// <summary>
  /// The id comes off the leader's cgroup, because a session is a scope and the login record has no
  /// such field at all.
  /// </summary>
  [Test]
  public void TheSessionIdIsReadOffTheLeadersCgroup() =>
    Assert.That(this.On("pts/0").SessionId, Is.EqualTo("7"));

  /// <summary>
  /// A login systemd did not open has no session id, and a null one is the honest answer. Nothing
  /// can act on such a row — <c>loginctl</c> knows a session only by its id — and inventing one
  /// would send a command at somebody else's session.
  /// </summary>
  [Test]
  public void ALoginSystemdDidNotOpenHasNoId() {
    Assert.That(this.On("tty2").SessionId, Is.Null);
    Assert.That(this.On("pts/9").SessionId, Is.Null, "and a leader that has gone cannot be asked at all");
  }

  /// <summary>
  /// Whether the process that opened the login is still there. A record that outlived its leader is
  /// what the page means by a stale login.
  /// </summary>
  [Test]
  public void AStaleLoginIsToldApartFromALiveOne() {
    Assert.That(this.On("pts/0").State, Is.EqualTo(SessionState.Alive));
    Assert.That(this.On("pts/9").State, Is.EqualTo(SessionState.Stale));
  }

  [Test]
  public void EachKindOfLoginIsClassifiedByItsOwnEvidence() {
    Assert.That(this.On("pts/0").Type, Is.EqualTo(SessionType.Terminal), "a pseudo-terminal on a local display");
    Assert.That(this.On("pts/9").Type, Is.EqualTo(SessionType.Remote), "one from another machine");
    Assert.That(this.On("tty2").Type, Is.EqualTo(SessionType.Console), "somebody at the keyboard");
  }

  /// <summary>
  /// The description is the first comma-separated part and no more of it. The rest is an office and a
  /// telephone number, which do not belong in a column headed with somebody's name.
  /// </summary>
  [Test]
  public void OnlyTheNamePartOfTheDescriptionIsTaken() =>
    Assert.That(this.On("pts/0").FullName, Is.EqualTo("Dana Marsh"));

  /// <summary>
  /// An account with no description has none, rather than an empty name. Most accounts on most
  /// machines are like this.
  /// </summary>
  [Test]
  public void AnAccountWithNoDescriptionHasNoName() =>
    Assert.That(this.On("tty2").FullName, Is.Null);

  /// <summary>
  /// Idleness is the terminal's modification time, which is what <c>who -u</c> and <c>w</c> measure.
  /// </summary>
  [Test]
  public void IdlenessComesFromWhenTheTerminalWasLastWrittenTo() {
    var idle = SessionFacts.IdleFor(this.On("pts/0").LastInputUtcTicks, DateTime.UtcNow);

    Assert.That(idle, Is.Not.Null);
    Assert.That(idle!.Value.TotalHours, Is.EqualTo(3).Within(0.05));
    Assert.That(SessionFacts.DescribeIdle(idle), Is.EqualTo("3h00"));
  }

  /// <summary>
  /// A session with no terminal to ask has no idle time — and nought is not "active this second".
  /// The sessions this cannot see are the graphical ones, and reporting those as never idle would be
  /// wrong about the busiest session on most desktops (PRD §72.3).
  /// </summary>
  [Test]
  public void ATerminalThatIsNotThereIsNotAnIdleTimeOfNought() {
    var session = this.On("pts/9");

    Assert.That(session.LastInputUtcTicks, Is.Zero);
    Assert.That(SessionFacts.IdleFor(session.LastInputUtcTicks, DateTime.UtcNow), Is.Null);
    Assert.That(SessionFacts.DescribeIdle(null), Is.EqualTo("—"));
  }

  /// <summary>
  /// The terminal name becomes a path, and it comes out of a file any root process may write. A name
  /// that is not a terminal name must never be joined to it.
  /// </summary>
  [Test]
  public void ATerminalNameThatIsAPathIsNotFollowed() {
    File.WriteAllBytes(
      Path.Combine(this._root, "utmp"),
      Utmp([new("dana", "../../etc/passwd", null, 4100)])
    );

    Assert.That(this.Sessions()[0].LastInputUtcTicks, Is.Zero);
  }

  #region what the classifier does on its own (PRD §43)

  /// <summary>
  /// Console is the strongest claim on the list — it says somebody is physically present — and it is
  /// the last arm anything should fall through to.
  /// </summary>
  [Test]
  public void AnUnrecognisedLineIsNotAConsole() {
    Assert.That(SessionFacts.Type(null, null), Is.EqualTo(SessionType.Unknown));
    Assert.That(SessionFacts.Type("weird", null), Is.EqualTo(SessionType.Unknown));
    Assert.That(SessionFacts.Type(string.Empty, string.Empty), Is.EqualTo(SessionType.Unknown));
  }

  [Test]
  public void ADisplayInTheLineIsAGraphicalLogin() =>
    Assert.That(SessionFacts.Type(":0", null), Is.EqualTo(SessionType.Graphical));

  /// <summary>
  /// A scope somebody wrote by hand is a unit and not a login, however much its name looks like one.
  /// </summary>
  [Test]
  public void AScopeThatIsNotNumberedIsNotASession() {
    Assert.That(SessionFacts.IdFromCgroup("/user.slice/session-backup.scope"), Is.Null);
    Assert.That(SessionFacts.IdFromCgroup("/user.slice/session-.scope"), Is.Null);
    Assert.That(SessionFacts.IdFromCgroup(null), Is.Null);
    Assert.That(SessionFacts.IdFromCgroup("/user.slice/user-1000.slice"), Is.Null);
  }

  /// <summary>
  /// A leader that has moved deeper is still inside its session's scope, so the whole path is
  /// searched rather than only the innermost unit.
  /// </summary>
  [Test]
  public void TheWholePathIsSearchedForTheScope() =>
    Assert.That(
      SessionFacts.IdFromCgroup("/user.slice/user-1000.slice/session-42.scope/nested/thing.service"),
      Is.EqualTo("42")
    );

  [Test]
  public void AClockPutBackIsNotANegativeIdleTime() {
    var future = DateTime.UtcNow.AddHours(1).Ticks;

    Assert.That(SessionFacts.IdleFor(future, DateTime.UtcNow), Is.EqualTo(TimeSpan.Zero));
  }

  #endregion

  #region what may be done to a session (PRD §43, §69, §90)

  /// <summary>
  /// Nothing here runs <c>loginctl</c>. The runner is injected and the test asserts the command that
  /// would have been issued — a test suite may not log somebody out of the machine it is running on.
  /// </summary>
  [Test]
  public void TheCommandIsBuiltWithoutAShellAndWithTheIdAfterADoubleDash() {
    var seen = new List<string>();
    var control = new LoginctlSessionControl((program, arguments) => {
      seen.Add(program);
      seen.AddRange(arguments);
      return (0, string.Empty, string.Empty);
    });

    Assert.That(control.Apply(SessionCommand.Terminate, "7").Succeeded, Is.EqualTo(LoginctlSessionControl.IsPresent));
    if (!LoginctlSessionControl.IsPresent)
      Assert.Ignore("there is no logind here, so the call is refused before the runner is reached");

    Assert.That(seen, Is.EqualTo(new[] { "loginctl", "--no-ask-password", "terminate-session", "--", "7" }));
  }

  /// <summary>
  /// The id becomes a command-line argument, so anything that is not one is refused before a process
  /// is started rather than handed over and hoped about.
  /// </summary>
  [Test]
  public void AnIdThatIsNotAnIdIsRefusedBeforeAnythingRuns() {
    var ran = false;
    var control = new LoginctlSessionControl((_, _) => {
      ran = true;
      return (0, string.Empty, string.Empty);
    });

    foreach (var bad in (string[])["--all", "../7", "7 8", string.Empty, "7\n"])
      Assert.That(control.Apply(SessionCommand.Terminate, bad).Succeeded, Is.False, bad);

    Assert.That(control.Apply(SessionCommand.None, "7").Succeeded, Is.False, "and an unnamed command too");
    Assert.That(ran, Is.False, "nothing was started");
  }

  /// <summary>
  /// Ending somebody's session is asked about whatever the preference says. The setting that
  /// switches confirmations off is switched off by people who end their own editors all day, and not
  /// by people who meant to log somebody out of a machine they share (PRD §43, §69).
  /// </summary>
  [Test]
  public void EndingASessionIsConfirmedEvenWithConfirmationsSwitchedOff() {
    var @class = ISessionControl.ClassOf(SessionCommand.Terminate);

    Assert.That(@class, Is.EqualTo(ActionClass.DataLoss));
    Assert.That(ActionSafety.MustAsk(@class, confirmsSingleActions: false, systemTarget: true), Is.True);

    // And locking a screen is not in the same class: a prompt on something the item beside it undoes
    // teaches people to dismiss prompts.
    Assert.That(ISessionControl.ClassOf(SessionCommand.Lock), Is.EqualTo(ActionClass.Reversible));
  }

  /// <summary>
  /// An unclassified command is treated as the most dangerous there is, like everything else that
  /// nobody sorted (PRD §72.3).
  /// </summary>
  [Test]
  public void AnUnnamedSessionCommandIsTreatedAsTheMostDangerous() {
    Assert.That(ISessionControl.ClassOf(default), Is.EqualTo(ActionClass.Unclassified));
    Assert.That(ActionSafety.MustAsk(ActionClass.Unclassified, confirmsSingleActions: false), Is.True);
  }

  #endregion

  private readonly record struct Login(string User, string Line, string? Host, int Pid);

  /// <summary>
  /// A utmp file, written to the layout <see cref="UtmpParser"/> reads.
  /// </summary>
  /// <remarks>
  /// The offsets are the parser's own constants rather than a second copy of them, so this cannot
  /// drift into agreeing with a parser that has changed.
  /// </remarks>
  private static byte[] Utmp(IReadOnlyList<Login> logins) {
    var bytes = new byte[UtmpParser.RecordSize * logins.Count];
    for (var i = 0; i < logins.Count; ++i) {
      var record = bytes.AsSpan(i * UtmpParser.RecordSize, UtmpParser.RecordSize);
      BinaryPrimitives.WriteInt16LittleEndian(record, 7);
      BinaryPrimitives.WriteInt32LittleEndian(record[4..], logins[i].Pid);
      Write(record[8..], logins[i].Line, 32);
      Write(record[44..], logins[i].User, 32);
      Write(record[76..], logins[i].Host ?? string.Empty, 256);
      BinaryPrimitives.WriteInt32LittleEndian(record[340..], 1_772_000_000);
    }

    return bytes;
  }

  private static void Write(Span<byte> destination, string value, int length) {
    var written = Encoding.UTF8.GetBytes(value, destination[..length]);
    destination[written..length].Clear();
  }

}
