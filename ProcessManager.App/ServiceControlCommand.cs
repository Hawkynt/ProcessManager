using Hawkynt.ProcessManager.Platform.Linux;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--service</c>: asking the manager to do something to a unit (PRD §41).
/// </summary>
/// <remarks>
/// The counterpart to <c>--services</c>, which only reads. Reading needs nothing but the files on
/// disk; changing a unit is the manager's business, so this asks <c>systemctl</c> and carries
/// whatever polkit decides — including the refusal, in the manager's own words rather than ours.
/// </remarks>
internal static class ServiceControlCommand {

  public static int Run(string? verb, string? unit) {
    var (known, command) = Parse(verb);
    if (!known) {
      Console.Error.WriteLine($"procman: '{verb}' is not something that can be done to a unit.");
      Console.Error.WriteLine("Try: start, stop, restart, reload, enable or disable.");
      return 1;
    }

    var result = new SystemdServiceControl().Apply(command, unit ?? string.Empty);
    if (result.Succeeded) {
      Console.WriteLine($"{verb} {unit}: done.");
      return 0;
    }

    Console.Error.WriteLine($"procman: {result.Detail}");
    return 1;
  }

  private static (bool Known, ServiceCommand Command) Parse(string? verb) => verb?.ToLowerInvariant() switch {
    "start" => (true, ServiceCommand.Start),
    "stop" => (true, ServiceCommand.Stop),
    "restart" => (true, ServiceCommand.Restart),
    "reload" => (true, ServiceCommand.Reload),
    "enable" => (true, ServiceCommand.Enable),
    "disable" => (true, ServiceCommand.Disable),
    _ => (false, ServiceCommand.Start),
  };

}
