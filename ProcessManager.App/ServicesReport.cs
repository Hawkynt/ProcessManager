using Hawkynt.ProcessManager.Abstractions;
using Hawkynt.ProcessManager.Model;
using Hawkynt.ProcessManager.Query;

namespace Hawkynt.ProcessManager.App;

/// <summary>
/// <c>--services</c>: the machine's services and which of them are running (PRD §41).
/// </summary>
internal static class ServicesReport {

  public static int Run(ISystemProbe probe, CommandLineOptions options) {
    var services = probe.GetServices();
    if (services.Count == 0) {
      Console.Error.WriteLine(
        OperatingSystem.IsWindows()
          ? "procman: services are not read on Windows yet — the service control manager is still to do."
          : "procman: no service units were found."
      );

      return 0;
    }

    // Default to the ones that are actually running, because a machine has several hundred units
    // and four fifths of them have never started. --filter widens it back out.
    var runningOnly = options.Filter is null;
    var width = 4;
    foreach (var service in services)
      if (!runningOnly || service.State == ServiceState.Running)
        width = Math.Max(width, service.Name.Length);

    var shown = 0;
    foreach (var service in services) {
      if (runningOnly && service.State != ServiceState.Running)
        continue;

      ++shown;
      // Four states, because "active" is not "running" and not "stopped" either: a oneshot unit that
      // set something up and finished has no processes and is still doing its job, and calling that
      // stopped is the answer somebody would act on and be wrong (PRD §41).
      var state = service.Masked ? "masked "
        : service.State switch {
          ServiceState.Running => "running",
          ServiceState.Active => "active ",
          ServiceState.Inactive => "stopped",
          _ => "—      ",
        };

      // Three states, not two: enabled, disabled, and "nothing wants it" — which is what a
      // socket-activated unit looks like and is not the same as disabled.
      var boot = service.Enabled switch { true => "boot", false => "no  ", null => "—   " };
      var pid = service.MainPid > 0 ? service.MainPid.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

      Console.WriteLine(
        $"{state}  {boot}  {pid,7}  {service.Name.PadRight(width)}  {service.Description ?? service.Command ?? ""}"
      );
    }

    if (runningOnly)
      Console.Error.WriteLine($"\n{shown} running of {services.Count} units. Pass --filter to see them all.");

    return 0;
  }

}
