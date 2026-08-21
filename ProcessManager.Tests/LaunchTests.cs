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
  /// Stopped before it has run any of its own code — unconditionally, on a busy machine as much as
  /// on an idle one.
  /// </summary>
  /// <remarks>
  /// This assertion used to be written as "usually T", which is what let a race hide behind a green
  /// suite: starting the program and then signalling it leaves a window in which the program has
  /// already begun, and under load the machine lands in that window roughly one run in four. Loop it
  /// rather than sampling it once — a single pass proves nothing about a race.
  /// </remarks>
  [Test]
  [Repeat(12)]
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

  /// <summary>
  /// …and the program it stops is the one that was asked for, not the shell that holds it there.
  /// </summary>
  /// <remarks>
  /// The shell <c>exec</c>s the program, which keeps the pid, so resuming it has to leave the caller
  /// holding an identity that names the program. If <c>exec</c> were ever replaced by a fork the pid
  /// reported here would belong to a shell that outlives nothing.
  /// </remarks>
  [Test]
  public void AProgramStartedStoppedIsTheProgramOnceResumed() {
    var actions = Actions();
    var result = actions.Launch(new("/bin/sleep", ["31"], Suspended: true));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);
      Assert.That(actions.Resume(result.Key).Succeeded, Is.True);

      // exec is not instantaneous; what matters is that it happens at all and keeps the pid. What
      // marks the moment is argv[0]: before exec the vector is the shell's, which already contains
      // "31" as a positional parameter, and only afterwards does it begin with the program. Waiting
      // on the argument would have been satisfied before anything happened, and waiting on `comm`
      // would catch the microseconds in which the task has been renamed and its new vector is not
      // yet readable.
      var deadline = Environment.TickCount64 + 5000;
      string[] vector;
      do {
        vector = File.ReadAllText($"/proc/{result.Pid}/cmdline").Split('\0');
      } while (vector is not ["/bin/sleep", ..] && Environment.TickCount64 < deadline);

      Assert.That(vector, Is.EqualTo(new[] { "/bin/sleep", "31", string.Empty }), "the program and its own arguments");
      Assert.That(File.ReadAllText($"/proc/{result.Pid}/comm").Trim(), Is.EqualTo("sleep"));
    } finally {
      Stop(result.Pid);
    }
  }

  /// <summary>
  /// The shell that holds a suspended program still does not re-split or re-glob its arguments.
  /// </summary>
  /// <remarks>
  /// Read while it is still stopped, which is the one moment the whole vector is visible: the
  /// arguments are the shell's positional parameters at that point, so an argument containing a
  /// space that arrives as one entry, and a lone <c>*</c> that has not become a directory listing,
  /// prove between them that nothing interpolated them into the script text.
  /// </remarks>
  [Test]
  public void ASuspendedStartStillPassesItsArgumentsWhole() {
    var result = Actions().Launch(new("/bin/sleep", ["a b", "*", "30"], Suspended: true));

    try {
      Assert.That(result.Outcome.Succeeded, Is.True, result.Outcome.Detail);

      var vector = File.ReadAllText($"/proc/{result.Pid}/cmdline").Split('\0');
      Assert.That(vector, Does.Contain("a b"), "one argument, space and all");
      Assert.That(vector, Does.Contain("*"), "not expanded against the working directory");
      Assert.That(vector, Does.Contain("/bin/sleep"));
    } finally {
      Stop(result.Pid);
    }
  }

  /// <summary>
  /// A suspended start of a program that is not there is refused before anything runs, rather than
  /// becoming an exit status a shell prints to nobody when it is resumed.
  /// </summary>
  [Test]
  public void ASuspendedStartOfAMissingProgramIsRefused() {
    var result = Actions().Launch(new("/nonexistent/program", [], Suspended: true));

    Assert.That(result.Outcome.Succeeded, Is.False);
    Assert.That(result.Outcome.Outcome, Is.EqualTo(ActionOutcome.Refused));
    Assert.That(result.Pid, Is.Zero);
    Assert.That(result.Outcome.Detail, Does.Contain("/nonexistent/program"));
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
