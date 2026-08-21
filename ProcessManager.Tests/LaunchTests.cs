using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Starting a program (PRD §54).
/// </summary>
/// <remarks>
/// These start real processes, because the whole feature is about what the operating system does
/// with the request — a stub would only prove that the record was filled in. Every one cleans up
/// after itself, and every program started is one that exits on its own if the clean-up fails.
/// </remarks>
[TestFixture]
[Platform("Linux")]
public sealed class LaunchTests {

  private static LinuxProcessActions Actions() => new(new());

  private static void Stop(int pid) {
    if (pid <= 0)
      return;

    try {
      System.Diagnostics.Process.GetProcessById(pid).Kill();
    } catch (ArgumentException) {
      // Already gone, which is the ordinary case for the short-lived ones.
    } catch (InvalidOperationException) {
    }
  }

  [Test]
  public void AProgramStartsAndIsIdentifiable() {
    var result = Actions().Launch(new("/bin/sleep", ["30"]));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);
      Assert.That(result.Pid, Is.GreaterThan(0));
      Assert.That(result.Key.Pid, Is.EqualTo(result.Pid));
      Assert.That(result.Key.StartTicks, Is.GreaterThan(0ul), "the identity pair, not a bare pid");
      Assert.That(Directory.Exists($"/proc/{result.Pid}"), Is.True);
    } finally {
      Stop(result.Pid);
    }
  }

  /// <summary>
  /// A program that exits before it can be read back has a pid and no readable start time. That is a
  /// successful launch of a short-lived program, not a failure to start one — <c>echo</c> does it
  /// every time (PRD §8.2).
  /// </summary>
  [Test]
  public void AProgramThatFinishesImmediatelyStillStarted() {
    var result = Actions().Launch(new("/bin/true", []));

    Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);
    Assert.That(result.Pid, Is.GreaterThan(0));
  }

  /// <summary>
  /// …but if there were settings to apply, the caller has to hear that they could not be. Silence
  /// would mean a program was left at a priority nobody asked for.
  /// </summary>
  [Test]
  public void AProgramThatFinishesBeforeItsPriorityIsSetSaysSo() {
    var result = Actions().Launch(new("/bin/true", [], Nice: 15));

    Assert.That(result.Pid, Is.GreaterThan(0), "it did start");
    if (!result.Outcome.Succeeded)
      Assert.That(result.Outcome.Outcome, Is.EqualTo(ActionOutcome.ProcessExited));
  }

  [Test]
  public void ArgumentsReachTheProgramUnsplit() {
    // A path with a space in it is the case a launcher that re-splits gets wrong.
    var directory = Path.Combine(Path.GetTempPath(), $"procman launch test {Environment.ProcessId}");
    Directory.CreateDirectory(directory);
    try {
      var result = Actions().Launch(new("/bin/sleep", ["30"], directory));

      try {
        Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);
        // The kernel's own answer for where the process is, which is the only one worth checking.
        Assert.That(Directory.ResolveLinkTarget($"/proc/{result.Pid}/cwd", true)?.FullName, Is.EqualTo(directory));
      } finally {
        Stop(result.Pid);
      }
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Test]
  public void TheWorkingDirectoryHasToExist() {
    var result = Actions().Launch(new("/bin/sleep", ["30"], "/no/such/directory/anywhere"));

    Assert.That(result.Outcome.Succeeded, Is.False);
    Assert.That(result.Outcome.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(result.Pid, Is.Zero, "nothing was started, so there is no pid to report");
  }

  /// <summary>
  /// Overrides, not a replacement. A process started with an emptied environment loses its locale,
  /// its display and its path, which is never what somebody setting one variable meant.
  /// </summary>
  [Test]
  public void TheEnvironmentIsAddedToRatherThanReplaced() {
    var result = Actions().Launch(new(
      "/bin/sleep",
      ["30"],
      Environment: [new("PROCMAN_LAUNCH_TEST", "yes")]
    ));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);

      var environment = File.ReadAllText($"/proc/{result.Pid}/environ").Split('\0');
      Assert.That(environment, Does.Contain("PROCMAN_LAUNCH_TEST=yes"));
      Assert.That(environment, Has.Some.StartWith("PATH="), "the inherited environment survived");
    } finally {
      Stop(result.Pid);
    }
  }

  /// <summary>
  /// Stopped the instant it exists. There is a race here that cannot be closed from outside — between
  /// exec and the signal the program has begun — which is the same race every tool offering this has.
  /// </summary>
  [Test]
  public void AProgramCanBeStartedStopped() {
    var result = Actions().Launch(new("/bin/sleep", ["30"], Suspended: true));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);

      var state = File.ReadAllText($"/proc/{result.Pid}/stat").Split(' ')[2];
      Assert.That(state, Is.EqualTo("T"), "T is stopped");
    } finally {
      Stop(result.Pid);
    }
  }

  [Test]
  public void APriorityIsAppliedAfterTheProgramExists() {
    var result = Actions().Launch(new("/bin/sleep", ["30"], Nice: 15));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);
      Assert.That(File.ReadAllText($"/proc/{result.Pid}/stat").Split(' ')[18], Is.EqualTo("15"), "the nice field");
    } finally {
      Stop(result.Pid);
    }
  }

  [Test]
  public void AProgramThatIsNotThereIsAFailureAndNotACrash() {
    var result = Actions().Launch(new("/nonexistent/program", []));

    Assert.That(result.Outcome.Succeeded, Is.False);
    Assert.That(result.Pid, Is.Zero);
    Assert.That(result.Outcome.Detail, Does.Contain("/nonexistent/program"));
  }

  [Test]
  public void AnEmptyRequestIsRefusedRatherThanAttempted() {
    foreach (var name in new[] { string.Empty, "   " }) {
      var result = Actions().Launch(new(name, []));

      Assert.That(result.Outcome.Outcome, Is.EqualTo(ActionOutcome.Refused));
      Assert.That(result.Pid, Is.Zero);
    }
  }

  /// <summary>
  /// There is deliberately no password anywhere on the request, and there never will be: a dialog
  /// that remembers a credential is a credential store nobody audited, and one that holds it even
  /// briefly is a string in a heap a core dump will carry (PRD §54).
  /// </summary>
  /// <remarks>
  /// Reflection, which the shipped code may not use — the trimmer has to see every member (§2). A
  /// test is not shipped, and enumerating a public surface is stable in a way that reaching into
  /// private state is not. This exists to fail the day somebody adds the field in good faith.
  /// </remarks>
  [Test]
  public void TheRequestCarriesNoSecret() {
    foreach (var property in typeof(LaunchRequest).GetProperties())
      Assert.That(
        property.Name,
        Does.Not.Contain("Password").And.Not.Contain("Secret").And.Not.Contain("Credential"),
        property.Name
      );
  }

}
