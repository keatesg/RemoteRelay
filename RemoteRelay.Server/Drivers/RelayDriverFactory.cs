using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

public enum RelayDriverKind
{
    Mock,
    RpiGpio,
    K8090
}

public static class RelayDriverFactory
{
    public static RelayDriverKind ResolveKind(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RelayDriver) &&
            !string.Equals(settings.RelayDriver, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.RelayDriver.ToLowerInvariant() switch
            {
                "mock" => RelayDriverKind.Mock,
                "rpigpio" or "gpio" or "rpi" => RelayDriverKind.RpiGpio,
                "k8090" or "velleman" => RelayDriverKind.K8090,
                _ => throw new InvalidOperationException(
                    $"Unknown RelayDriver '{settings.RelayDriver}'. Valid values: Auto, Mock, RpiGpio, K8090.")
            };
        }

        if (settings.UseMockGpio)
        {
            return RelayDriverKind.Mock;
        }

        if (Environment.OSVersion.Platform == PlatformID.Unix && Directory.Exists("/sys/class/gpio"))
        {
            return RelayDriverKind.RpiGpio;
        }

        return RelayDriverKind.Mock;
    }

    public static IRelayDriver Create(AppSettings settings, ILogger logger)
    {
        var kind = ResolveKind(settings);

        switch (kind)
        {
            case RelayDriverKind.RpiGpio:
                try
                {
                    var driver = new GpioRelayDriver();
                    logger.LogInformation("Using RpiGpio relay driver.");
                    return driver;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to initialise RpiGpio driver; falling back to Mock.");
                    return new MockRelayDriver();
                }

            case RelayDriverKind.K8090:
                var port = settings.K8090?.Port ?? string.Empty;
                // A config reload disposes the previous driver and immediately reopens the same
                // COM port; on Windows the OS can briefly report it as still in use, so retry
                // before giving up. Falling back to Mock keeps the server (and its UI/API)
                // alive rather than leaving SwitcherState with no driver at all.
                const int maxAttempts = 3;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        var k8090 = new K8090RelayDriver(port, logger);
                        logger.LogInformation("Using K8090 relay driver on {Port}.", port);
                        return k8090;
                    }
                    catch (Exception ex) when (attempt < maxAttempts)
                    {
                        logger.LogWarning(ex, "Failed to open K8090 on '{Port}' (attempt {Attempt}/{MaxAttempts}); retrying.", port, attempt, maxAttempts);
                        Thread.Sleep(500);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to initialise K8090 driver on '{Port}' after {MaxAttempts} attempts; falling back to Mock. Relay switching is INOPERATIVE until the configuration is reloaded successfully.", port, maxAttempts);
                        return new MockRelayDriver();
                    }
                }

            case RelayDriverKind.Mock:
            default:
                logger.LogInformation("Using Mock relay driver.");
                return new MockRelayDriver();
        }
    }

    public static IEnumerable<string> ValidateForKind(RelayDriverKind kind, AppSettings settings)
    {
        switch (kind)
        {
            case RelayDriverKind.K8090:
                if (string.IsNullOrWhiteSpace(settings.K8090?.Port))
                {
                    yield return "RelayDriver is 'K8090' but K8090.Port is not set in config.";
                }
                if (settings.Routes != null)
                {
                    foreach (var route in settings.Routes)
                    {
                        if (route.RelayPin < 1 || route.RelayPin > 8)
                        {
                            yield return $"Route {route.SourceName}->{route.OutputName} uses channel {route.RelayPin}, but K8090 only supports channels 1-8.";
                        }
                    }
                }
                if (settings.InactiveRelay != null && (settings.InactiveRelay.Pin < 1 || settings.InactiveRelay.Pin > 8))
                {
                    yield return $"InactiveRelay.Pin {settings.InactiveRelay.Pin} is out of K8090 channel range 1-8.";
                }
                break;

            case RelayDriverKind.RpiGpio:
            case RelayDriverKind.Mock:
                // The 1..40 range is already enforced by AppSettingsValidator.
                break;
        }
    }
}
