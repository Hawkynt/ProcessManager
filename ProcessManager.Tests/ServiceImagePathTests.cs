using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.Tests;

/// <summary>
/// Splitting a Windows service's <c>ImagePath</c> into the program and its arguments (PRD §41).
/// </summary>
/// <remarks>
/// <para>
/// The one part of the Windows services reader that is text rather than syscalls, so it lives in
/// Core with no platform attribute and is tested on every CI leg (§9.2). The rest of that reader
/// cannot be tested here at all, which is exactly why this half was pulled out of it.
/// </para>
/// <para>
/// <c>ImagePath</c> is a command line and not a path. Most program paths on Windows have a space in
/// them, so splitting on the first space would name the wrong program for the majority of services
/// on the machine — and the wrong program is worse than no program, because it looks like an answer.
/// </para>
/// </remarks>
[TestFixture]
public sealed class ServiceImagePathTests {

  [Test]
  public void NothingIsNothing() {
    Assert.Multiple(() => {
      Assert.That(ServiceImagePath.ExecutableOf(null), Is.Null);
      Assert.That(ServiceImagePath.ExecutableOf(string.Empty), Is.Null);
      Assert.That(ServiceImagePath.ExecutableOf("   "), Is.Null);
      Assert.That(ServiceImagePath.ArgumentsOf(null), Is.Null);
    });
  }

  /// <summary>A quoted path is the easy case and the one that must never be got wrong.</summary>
  [Test]
  public void AQuotedPathIsTheWholeProgram() {
    const string Command = "\"C:\\Program Files\\Thing\\thing.exe\" --serve --port 80";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo("C:\\Program Files\\Thing\\thing.exe"));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.EqualTo("--serve --port 80"));
  }

  [Test]
  public void AQuotedPathWithNoArgumentsHasNone() {
    const string Command = "\"C:\\Program Files\\Thing\\thing.exe\"";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo("C:\\Program Files\\Thing\\thing.exe"));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.Null);
  }

  /// <summary>
  /// <b>The case the naive split gets wrong.</b> Unquoted with a space in the path is what the
  /// registry actually holds for a great many services, and ending the program at the first space
  /// would name <c>C:\Program</c>.
  /// </summary>
  [Test]
  public void AnUnquotedPathWithASpaceStillEndsAtTheExecutable() {
    const string Command = "C:\\Program Files\\Thing\\thing.exe --serve";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo("C:\\Program Files\\Thing\\thing.exe"));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.EqualTo("--serve"));
  }

  [Test]
  public void AnUnquotedPathWithNoArgumentsIsAllProgram() {
    const string Command = "C:\\Windows\\system32\\svchost.exe";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo(Command));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.Null);
  }

  /// <summary>The svchost line every Windows machine has forty of.</summary>
  [Test]
  public void TheGroupedHostServiceSplitsAsExpected() {
    const string Command = "C:\\Windows\\system32\\svchost.exe -k netsvcs -p";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo("C:\\Windows\\system32\\svchost.exe"));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.EqualTo("-k netsvcs -p"));
  }

  /// <summary>
  /// A driver has no <c>.exe</c> and often no arguments. Reading to the first space is right here and
  /// would have been wrong above, which is why the extension is looked for first.
  /// </summary>
  [Test]
  public void ADriverPathHasNoExtensionToFind() {
    const string Command = "\\SystemRoot\\System32\\drivers\\thing.sys";

    Assert.That(ServiceImagePath.ExecutableOf(Command), Is.EqualTo(Command));
    Assert.That(ServiceImagePath.ArgumentsOf(Command), Is.Null);
  }

  /// <summary>Case does not matter: the registry holds <c>.EXE</c> as readily as <c>.exe</c>.</summary>
  [TestCase("C:\\Windows\\THING.EXE /run")]
  [TestCase("C:\\Windows\\Thing.Exe /run")]
  public void TheExtensionIsFoundInAnyCase(string command) {
    Assert.That(ServiceImagePath.ExecutableOf(command), Does.EndWith("xe").IgnoreCase);
    Assert.That(ServiceImagePath.ArgumentsOf(command), Is.EqualTo("/run"));
  }

  /// <summary>
  /// An unclosed quote takes the rest, which is what every command-line reader does and is the least
  /// surprising of the available answers for a registry value somebody hand-edited.
  /// </summary>
  [Test]
  public void AnUnclosedQuoteTakesTheRest()
    => Assert.That(ServiceImagePath.ExecutableOf("\"C:\\Thing\\thing.exe"), Is.EqualTo("C:\\Thing\\thing.exe"));

  /// <summary>
  /// Leading and trailing space is not part of the program. A registry value with a stray space is
  /// ordinary and a path beginning with one is not.
  /// </summary>
  [Test]
  public void SurroundingSpaceIsNotPartOfIt()
    => Assert.That(ServiceImagePath.ExecutableOf("  C:\\Thing\\thing.exe  "), Is.EqualTo("C:\\Thing\\thing.exe"));

  /// <summary>
  /// The two halves always describe the same string. Joining them back gives what was written, which
  /// is the assertion that catches the pair drifting apart in a way no single case would.
  /// </summary>
  [TestCase("\"C:\\Program Files\\Thing\\thing.exe\" --serve --port 80")]
  [TestCase("C:\\Program Files\\Thing\\thing.exe --serve")]
  [TestCase("C:\\Windows\\system32\\svchost.exe -k netsvcs -p")]
  [TestCase("C:\\Windows\\system32\\svchost.exe")]
  [TestCase("\\SystemRoot\\System32\\drivers\\thing.sys")]
  public void TheTwoHalvesAccountForTheWholeLine(string command) {
    var executable = ServiceImagePath.ExecutableOf(command);
    var arguments = ServiceImagePath.ArgumentsOf(command);
    Assert.That(executable, Is.Not.Null);

    // Everything the line held is in one half or the other, once. The quotes and the separating space
    // are the only characters allowed to go missing.
    var rejoined = arguments is null ? executable : $"{executable} {arguments}";
    Assert.That(
      rejoined!.Replace("\"", string.Empty),
      Is.EqualTo(command.Trim().Replace("\"", string.Empty)),
      "the program and the arguments together are the line that was written"
    );
  }

}
