using System.Text;
using System;
using System.Collections.Generic;

namespace RemoteRelay.Common;

public static class AppSettingsValidator
{
    public static bool TryValidate(AppSettings settings, out string validationSummary)
        => TryValidate(settings, out validationSummary, out _);

    /// <summary>
    /// Validates the settings, separating fatal <paramref name="validationSummary"/> errors
    /// (which should prevent startup) from non-fatal <paramref name="warnings"/> (e.g. an
    /// as-yet-unconfigured relay port). A missing relay port is a warning, not an error: the
    /// server still starts and serves its UI/API, the relay driver falls back to Mock, and the
    /// ConfigurationWatcher picks up a valid port live once one is set.
    /// </summary>
    public static bool TryValidate(AppSettings settings, out string validationSummary, out IReadOnlyList<string> warnings)
    {
        var errors = new List<string>();
        var warningList = new List<string>();

        ValidateRoutes(settings, errors);
        ValidateServerPort(settings, errors);
        ValidateDefaultRoutes(settings, errors);
        ValidatePhysicalButtons(settings, errors);
        ValidateInactiveRelay(settings, errors);
        ValidateRelayDriver(settings, errors, warningList);

        if (settings.ConfigVersion > AppSettings.CurrentConfigVersion)
        {
            warningList.Add(
                $"Configuration schema v{settings.ConfigVersion} is newer than this build supports (v{AppSettings.CurrentConfigVersion}). " +
                "It was probably written by a newer release; settings this build doesn't know about will be ignored.");
        }

        warnings = warningList;

        if (errors.Count == 0)
        {
            validationSummary = string.Empty;
            return true;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Configuration validation failed:");
        foreach (var error in errors)
        {
            builder.Append(" - ");
            builder.AppendLine(error);
        }

        validationSummary = builder.ToString();
        return false;
    }

    private static void ValidateRoutes(AppSettings settings, List<string> errors)
    {
        if (settings.Routes == null || settings.Routes.Count == 0)
            return; // Empty routes = unconfigured but valid

        var seen = new HashSet<(string Source, string Output)>(new RouteEqualityComparer());
        foreach (var route in settings.Routes)
        {
            if (string.IsNullOrWhiteSpace(route.SourceName))
            {
                errors.Add("A route is missing a SourceName.");
            }

            if (string.IsNullOrWhiteSpace(route.OutputName))
            {
                errors.Add("Route for source '" + (route.SourceName ?? "<unknown>") + "' is missing an OutputName.");
            }

            if (route.RelayPin <= 0)
            {
                errors.Add($"Route {route.SourceName}->{route.OutputName} has an invalid relay pin '{route.RelayPin}'. Pin must be greater than 0.");
            }
            else if (route.RelayPin > 40)
            {
                errors.Add($"Route {route.SourceName}->{route.OutputName} has relay pin '{route.RelayPin}' which exceeds maximum valid pin (40).");
            }

            if (!string.IsNullOrWhiteSpace(route.SourceName) && !string.IsNullOrWhiteSpace(route.OutputName))
            {
                if (!seen.Add((route.SourceName, route.OutputName)))
                {
                    errors.Add($"Duplicate route detected for '{route.SourceName}' -> '{route.OutputName}'.");
                }
            }
        }
    }

    private static void ValidateServerPort(AppSettings settings, List<string> errors)
    {
        if (settings.ServerPort <= 0 || settings.ServerPort > 65535)
        {
            errors.Add($"ServerPort '{settings.ServerPort}' must be between 1 and 65535.");
        }
    }

    private static void ValidateDefaultRoutes(AppSettings settings, List<string> errors)
    {
        if (settings.DefaultRoutes == null)
        {
            return;
        }

        var validSources = new HashSet<string>(settings.Sources, StringComparer.OrdinalIgnoreCase);
        var validOutputs = new HashSet<string>(settings.Outputs, StringComparer.OrdinalIgnoreCase);
        var seenOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in settings.DefaultRoutes)
        {
            if (!validSources.Contains(pair.Key))
            {
                errors.Add($"Default route references unknown source '{pair.Key}'.");
            }

            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                if (!validOutputs.Contains(pair.Value))
                {
                    errors.Add($"Default route for source '{pair.Key}' references unknown output '{pair.Value}'.");
                }
                else if (!seenOutputs.Add(pair.Value))
                {
                    errors.Add($"Multiple default routes reference the same output '{pair.Value}'.");
                }
            }
        }
    }

    private static void ValidatePhysicalButtons(AppSettings settings, List<string> errors)
    {
        if (settings.PhysicalSourceButtons == null)
        {
            return;
        }

        var validSources = new HashSet<string>(settings.Sources, StringComparer.OrdinalIgnoreCase);
        foreach (var button in settings.PhysicalSourceButtons)
        {
            if (!validSources.Contains(button.Key))
            {
                errors.Add($"Physical button configured for unknown source '{button.Key}'.");
                continue;
            }

            if (button.Value.PinNumber <= 0)
            {
                errors.Add($"Physical button for source '{button.Key}' has invalid pin number '{button.Value.PinNumber}'. Pin must be greater than 0.");
            }
            else if (button.Value.PinNumber > 40)
            {
                errors.Add($"Physical button for source '{button.Key}' has pin '{button.Value.PinNumber}' which exceeds maximum valid pin (40).");
            }
        }
    }

    private static void ValidateInactiveRelay(AppSettings settings, List<string> errors)
    {
        if (settings.InactiveRelay == null)
        {
            return;
        }

        if (settings.InactiveRelay.Pin <= 0)
        {
            errors.Add($"Inactive relay pin '{settings.InactiveRelay.Pin}' must be greater than zero.");
        }
        else if (settings.InactiveRelay.Pin > 40)
        {
            errors.Add($"Inactive relay pin '{settings.InactiveRelay.Pin}' exceeds maximum valid pin (40).");
        }
    }

    private static void ValidateRelayDriver(AppSettings settings, List<string> errors, List<string> warnings)
    {
        var driver = settings.RelayDriver?.Trim();
        if (string.IsNullOrEmpty(driver) || string.Equals(driver, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var known = new[] { "Mock", "RpiGpio", "Gpio", "Rpi", "K8090", "Velleman" };
        if (!known.Any(k => string.Equals(k, driver, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"RelayDriver '{settings.RelayDriver}' is not recognised. Valid values: Auto, Mock, RpiGpio, K8090.");
            return;
        }

        if (string.Equals(driver, "K8090", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(driver, "Velleman", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(settings.K8090?.Port))
            {
                warnings.Add("RelayDriver is 'K8090' but K8090.Port is not set; relay switching will be inoperative until a port is configured in config.json.");
            }

            if (settings.Routes != null)
            {
                foreach (var route in settings.Routes)
                {
                    if (route.RelayPin < 1 || route.RelayPin > 8)
                    {
                        errors.Add($"Route {route.SourceName}->{route.OutputName} uses channel {route.RelayPin}; K8090 only supports channels 1-8.");
                    }
                }
            }

            if (settings.InactiveRelay != null && (settings.InactiveRelay.Pin < 1 || settings.InactiveRelay.Pin > 8))
            {
                errors.Add($"InactiveRelay.Pin {settings.InactiveRelay.Pin} is out of K8090 channel range 1-8.");
            }
        }
    }

    private sealed class RouteEqualityComparer : IEqualityComparer<(string Source, string Output)>
    {
        public bool Equals((string Source, string Output) x, (string Source, string Output) y)
        {
            return string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(x.Output, y.Output, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Source, string Output) obj)
        {
            var sourceHash = obj.Source is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source);
            var outputHash = obj.Output is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Output);
            return HashCode.Combine(sourceHash, outputHash);
        }
    }
}
