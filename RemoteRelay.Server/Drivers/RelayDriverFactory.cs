using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Drivers;

public enum RelayDriverKind
{
    Mock,
    RpiGpio,
    K8090,
    SainSmart,
    Lcus,
    ModbusRtu
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
                "sainsmart" or "sainsmartusb" or "kmtronic" => RelayDriverKind.SainSmart,
                "lcus" or "seeit" or "lctech" => RelayDriverKind.Lcus,
                "modbusrtu" or "modbus" or "waveshare" => RelayDriverKind.ModbusRtu,
                _ => throw new InvalidOperationException(
                    $"Unknown RelayDriver '{settings.RelayDriver}'. Valid values: Auto, Mock, RpiGpio, K8090, SainSmart, LCUS, ModbusRTU.")
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
                // The driver's own monitor thread opens the port and keeps retrying if the
                // card isn't present yet, so we don't retry here. Construction only throws on
                // a genuine config error (no port name); fall back to Mock in that case so the
                // server (and its UI/API) still starts.
                try
                {
                    var k8090 = new K8090RelayDriver(port, logger);
                    logger.LogInformation("Configured K8090 relay driver on {Port}; connecting in the background.", port);
                    return k8090;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialise K8090 driver (port '{Port}'); falling back to Mock. Relay switching is INOPERATIVE until the configuration is fixed and reloaded.", port);
                    return new MockRelayDriver();
                }

            case RelayDriverKind.SainSmart:
                var sainsmartPort = settings.SainSmart?.Port ?? string.Empty;
                var channels = settings.SainSmart?.Channels > 0 ? settings.SainSmart.Channels : 4;
                var baudRate = settings.SainSmart?.BaudRate > 0 ? settings.SainSmart.BaudRate : 9600;
                try
                {
                    var sainsmart = new SainSmartRelayDriver(sainsmartPort, channels, baudRate, logger);
                    logger.LogInformation("Configured SainSmart relay driver on {Port} ({Channels} channels, {BaudRate} baud); connecting in the background.",
                        sainsmartPort, channels, baudRate);
                    return sainsmart;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialise SainSmart driver (port '{Port}'); falling back to Mock. Relay switching is INOPERATIVE until the configuration is fixed and reloaded.", sainsmartPort);
                    return new MockRelayDriver();
                }

            case RelayDriverKind.Lcus:
                var lcusPort = settings.LCUS?.Port ?? string.Empty;
                var lcusChannels = settings.LCUS?.Channels > 0 ? settings.LCUS.Channels : 4;
                var lcusBaudRate = settings.LCUS?.BaudRate > 0 ? settings.LCUS.BaudRate : 9600;
                try
                {
                    var lcus = new LcusRelayDriver(lcusPort, lcusChannels, lcusBaudRate, logger);
                    logger.LogInformation("Configured LCUS relay driver on {Port} ({Channels} channels, {BaudRate} baud); connecting in the background.",
                        lcusPort, lcusChannels, lcusBaudRate);
                    return lcus;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialise LCUS driver (port '{Port}'); falling back to Mock. Relay switching is INOPERATIVE until the configuration is fixed and reloaded.", lcusPort);
                    return new MockRelayDriver();
                }

            case RelayDriverKind.ModbusRtu:
                var modbusPort = settings.ModbusRTU?.Port ?? string.Empty;
                var modbusChannels = settings.ModbusRTU?.Channels > 0 ? settings.ModbusRTU.Channels : 8;
                var modbusBaudRate = settings.ModbusRTU?.BaudRate > 0 ? settings.ModbusRTU.BaudRate : 9600;
                var modbusSlaveId = settings.ModbusRTU?.SlaveId > 0 ? settings.ModbusRTU.SlaveId : (byte)1;
                try
                {
                    var modbus = new ModbusRtuRelayDriver(modbusPort, modbusChannels, modbusBaudRate, modbusSlaveId, logger);
                    logger.LogInformation("Configured ModbusRTU relay driver on {Port} (slave {SlaveId}, {Channels} channels, {BaudRate} baud); connecting in the background.",
                        modbusPort, modbusSlaveId, modbusChannels, modbusBaudRate);
                    return modbus;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to initialise ModbusRTU driver (port '{Port}'); falling back to Mock. Relay switching is INOPERATIVE until the configuration is fixed and reloaded.", modbusPort);
                    return new MockRelayDriver();
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

            case RelayDriverKind.SainSmart:
                if (string.IsNullOrWhiteSpace(settings.SainSmart?.Port))
                {
                    yield return "RelayDriver is 'SainSmart' but SainSmart.Port is not set in config.";
                }
                var maxCh = settings.SainSmart?.Channels > 0 ? settings.SainSmart.Channels : 4;
                if (settings.Routes != null)
                {
                    foreach (var route in settings.Routes)
                    {
                        if (route.RelayPin < 1 || route.RelayPin > maxCh)
                        {
                            yield return $"Route {route.SourceName}->{route.OutputName} uses channel {route.RelayPin}, but SainSmart only supports channels 1-{maxCh}.";
                        }
                    }
                }
                if (settings.InactiveRelay != null && (settings.InactiveRelay.Pin < 1 || settings.InactiveRelay.Pin > maxCh))
                {
                    yield return $"InactiveRelay.Pin {settings.InactiveRelay.Pin} is out of SainSmart channel range 1-{maxCh}.";
                }
                break;

            case RelayDriverKind.Lcus:
                if (string.IsNullOrWhiteSpace(settings.LCUS?.Port))
                {
                    yield return "RelayDriver is 'LCUS' but LCUS.Port is not set in config.";
                }
                var maxLcusCh = settings.LCUS?.Channels > 0 ? settings.LCUS.Channels : 4;
                if (settings.Routes != null)
                {
                    foreach (var route in settings.Routes)
                    {
                        if (route.RelayPin < 1 || route.RelayPin > maxLcusCh)
                        {
                            yield return $"Route {route.SourceName}->{route.OutputName} uses channel {route.RelayPin}, but LCUS only supports channels 1-{maxLcusCh}.";
                        }
                    }
                }
                if (settings.InactiveRelay != null && (settings.InactiveRelay.Pin < 1 || settings.InactiveRelay.Pin > maxLcusCh))
                {
                    yield return $"InactiveRelay.Pin {settings.InactiveRelay.Pin} is out of LCUS channel range 1-{maxLcusCh}.";
                }
                break;

            case RelayDriverKind.ModbusRtu:
                if (string.IsNullOrWhiteSpace(settings.ModbusRTU?.Port))
                {
                    yield return "RelayDriver is 'ModbusRTU' but ModbusRTU.Port is not set in config.";
                }
                var maxModbusCh = settings.ModbusRTU?.Channels > 0 ? settings.ModbusRTU.Channels : 8;
                if (settings.Routes != null)
                {
                    foreach (var route in settings.Routes)
                    {
                        if (route.RelayPin < 1 || route.RelayPin > maxModbusCh)
                        {
                            yield return $"Route {route.SourceName}->{route.OutputName} uses channel {route.RelayPin}, but ModbusRTU only supports channels 1-{maxModbusCh}.";
                        }
                    }
                }
                if (settings.InactiveRelay != null && (settings.InactiveRelay.Pin < 1 || settings.InactiveRelay.Pin > maxModbusCh))
                {
                    yield return $"InactiveRelay.Pin {settings.InactiveRelay.Pin} is out of ModbusRTU channel range 1-{maxModbusCh}.";
                }
                break;

            case RelayDriverKind.RpiGpio:
            case RelayDriverKind.Mock:
                // The 1..40 range is already enforced by AppSettingsValidator.
                break;
        }
    }
}
