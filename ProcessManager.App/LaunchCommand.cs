using System.Globalization;
using Hawkynt.ProcessManager.Abstractions;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// Starts a program (PRD §54).
/// </summary>
/// <remarks>
/// Everything after <c>--run</c> belongs to the program being started, including anything that looks
/// like one of this program's own switches: a launcher that ate its child's <c>--help</c> would be
/// useless for exactly the programs somebody most wants to start.
/// </remarks>
internal static class LaunchCommand {

  public static int Run(IProcessActions? actions, CommandLineOptions options) {
    if (actions is null) {
      Console.Error.WriteLine("procman: this build cannot start processes on this platform.");
      return 1;
    }

    if (options.LaunchCommand is not { Count: > 0 } command) {
      Console.Error.WriteLine("procman: --run needs a program to start.");
      return 1;
    }

    var request = new LaunchRequest(
      command[0],
      command.Count > 1 ? command.Skip(1).ToArray() : [],
      options.LaunchDirectory,
      Suspended: options.LaunchSuspended
    );

    var result = actions.Launch(request);
    if (result.Pid != 0)
      Console.WriteLine(result.Pid.ToString(CultureInfo.InvariantCulture));

    if (result.Outcome.Succeeded)
      return 0;

    // A process that started and could not be niced is not a failure to start, and the exit status
    // has to say which of the two happened — a script that checked it would otherwise kill a running
    // program because its priority was refused.
    Console.Error.WriteLine($"procman: {result.Outcome.Detail ?? result.Outcome.Outcome.ToString()}");
    return result.Pid != 0 ? 0 : 1;
  }

}
