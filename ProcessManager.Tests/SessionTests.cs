using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Login sessions (PRD §43), read from a recorded utmp so the offsets are checked on every CI leg
/// rather than only where the file exists.
/// </summary>
[TestFixture(false, TestName = "SessionTests (syscalls)")]
[TestFixture(true, TestName = "SessionTests (portable file access)")]
public sealed class SessionTests(bool portable) {

  private static string Fixtures
    => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

  private static IReadOnlyList<SessionRecord> Read(bool portable, string? path = null) {
    using var probe = new LinuxProbe(new() {
      UsePortableFileAccess = portable,
      ProcRoot = Path.Combine(Fixtures, "proc-desktop"),
      UtmpPath = path ?? Path.Combine(Fixtures, "utmp-desktop"),
      EffectiveUserId = 0,
    });

    return probe.GetSessions();
  }

  private IReadOnlyList<SessionRecord> Sessions() => Read(portable);

  private static SessionRecord One(IReadOnlyList<SessionRecord> sessions, string user) {
    foreach (var session in sessions)
      if (session.UserName == user)
        return session;

    Assert.Fail($"no session for '{user}'");
    return default;
  }

  [Test]
  public void EveryFieldOfALoginLands() {
    var alice = One(this.Sessions(), "alice");

    Assert.That(alice.Kind, Is.EqualTo(SessionKind.User));
    Assert.That(alice.Terminal, Is.EqualTo("pts/0"));
    Assert.That(alice.RemoteHost, Is.EqualTo("10.0.0.5"));
    Assert.That(alice.Pid, Is.EqualTo(1234));
    Assert.That(
      new DateTime(alice.LoginTimeUtcTicks, DateTimeKind.Utc),
      Is.EqualTo(new DateTime(2026, 3, 1, 8, 1, 0, DateTimeKind.Utc))
    );
  }

  /// <summary>
  /// An empty host field means a login at the machine itself, which is a different answer from "we
  /// do not know where this came from" — so it is null rather than an empty string, and the report
  /// prints "local" (PRD §72.3).
  /// </summary>
  [Test]
  public void ALocalLoginHasNoRemoteHostRatherThanAnEmptyOne() {
    var bob = One(this.Sessions(), "bob");

    Assert.That(bob.Terminal, Is.EqualTo("tty1"));
    Assert.That(bob.RemoteHost, Is.Null);
  }

  [Test]
  public void TheDifferentKindsOfRecordAreToldApart() {
    var sessions = this.Sessions();
    var kinds = new List<SessionKind>();
    foreach (var session in sessions)
      kinds.Add(session.Kind);

    Assert.That(kinds, Does.Contain(SessionKind.Boot));
    Assert.That(kinds, Does.Contain(SessionKind.User));
    Assert.That(kinds, Does.Contain(SessionKind.LoginProcess));
    Assert.That(kinds, Does.Contain(SessionKind.Dead));

    // A user who logged out is a Dead record, and counting it as a login would report somebody
    // present who went home.
    Assert.That(One(sessions, "carol").Kind, Is.EqualTo(SessionKind.Dead));
  }

  /// <summary>
  /// utmp is preallocated, so runs of empty slots are normal and must not appear as sessions with
  /// no name.
  /// </summary>
  [Test]
  public void EmptySlotsAreNotSessions() {
    foreach (var session in this.Sessions())
      Assert.That(session.Kind, Is.Not.EqualTo(SessionKind.Unknown));
  }

  /// <summary>
  /// A field that is full has no terminator at all, so a parser looking for one reads into the next
  /// field. Reading the whole field instead drags the padding into every shorter value.
  /// </summary>
  [Test]
  public void AFieldWithNoRoomForATerminatorIsReadWhole() {
    var full = One(this.Sessions(), new string('u', 32));

    Assert.That(full.UserName, Has.Length.EqualTo(32));
    Assert.That(full.Terminal, Is.EqualTo("pts/1"), "and the next field is still intact");
  }

  [Test]
  public void ShorterValuesDoNotCarryTheirPadding() {
    var alice = One(this.Sessions(), "alice");

    Assert.That(alice.UserName, Has.Length.EqualTo(5));
    Assert.That(alice.UserName, Does.Not.Contain("\0"));
  }

  /// <summary>
  /// The file is written while it is read, so a trailing partial record is normal and must be
  /// ignored rather than parsed out of whatever follows it.
  /// </summary>
  [Test]
  public void ATrailingPartialRecordIsIgnored() {
    // Seven whole records are in the fixture; the eighth is 104 bytes of a 384-byte structure.
    Assert.That(this.Sessions(), Has.Count.EqualTo(6), "seven records less the empty slot");
  }

  [Test]
  public void AMissingFileIsNotAnError() =>
    Assert.That(Read(portable, Path.Combine(Fixtures, "no-such-utmp")), Is.Empty);

  [Test]
  public void TheBootRecordCarriesWhenTheMachineStarted() {
    foreach (var session in this.Sessions())
      if (session.Kind == SessionKind.Boot) {
        Assert.That(
          new DateTime(session.LoginTimeUtcTicks, DateTimeKind.Utc),
          Is.EqualTo(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc))
        );

        return;
      }

    Assert.Fail("the fixture has a boot record");
  }

  [Test]
  public void BothFileAccessPathsAgree() {
    var syscalls = Read(portable: false);
    var managed = Read(portable: true);

    Assert.That(managed, Has.Count.EqualTo(syscalls.Count));
    for (var i = 0; i < syscalls.Count; ++i)
      Assert.That(managed[i], Is.EqualTo(syscalls[i]));
  }

}
