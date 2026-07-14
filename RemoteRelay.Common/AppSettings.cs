using System.Device.Gpio;

namespace RemoteRelay.Common;

public class PhysicalButtonConfig
{
    public int PinNumber { get; set; }

    private string _triggerState = "Low"; // Default value
    public string TriggerState
    {
        get => _triggerState;
        set // Public setter for validation
        {
            if (string.Equals(value, "High", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Low", StringComparison.OrdinalIgnoreCase))
            {
                _triggerState = value;
            }
            else
            {
                throw new ArgumentException("TriggerState must be either \"High\" or \"Low\".");
            }
        }
    }

    public PinValue GetTriggerPinValue() => _triggerState.Equals("Low", StringComparison.OrdinalIgnoreCase) ? PinValue.Low : PinValue.High;

    public PinEventTypes GetTriggerEventType() => _triggerState.Equals("Low", StringComparison.OrdinalIgnoreCase) ? PinEventTypes.Falling : PinEventTypes.Rising;
}

public class K8090Settings
{
    public string Port { get; set; } = string.Empty;
}

public class InactiveRelaySettings
{
    public int Pin { get; set; }

    private string _inactiveState = "High"; // Default value
    public string InactiveState
    {
        get => _inactiveState;
        set
        {
            if (string.Equals(value, "High", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Low", StringComparison.OrdinalIgnoreCase))
            {
                _inactiveState = value;
            }
            else
            {
                throw new ArgumentException("InactiveState must be either \"High\" or \"Low\".");
            }
        }
    }

    public PinValue GetInactivePinValue() => _inactiveState.Equals("High", StringComparison.OrdinalIgnoreCase) ? PinValue.High : PinValue.Low;
    public PinValue GetActivePinValue() => _inactiveState.Equals("High", StringComparison.OrdinalIgnoreCase) ? PinValue.Low : PinValue.High;
}

[Serializable]
public struct AppSettings
{
    /// <summary>
    /// The config.json schema version this build writes. Bump it and add a step
    /// in ConfigMigrator whenever the schema changes shape.
    /// </summary>
    public const int CurrentConfigVersion = 1;

    /// <summary>
    /// Schema version the loaded file was written with. In the raw JSON a
    /// missing value means the file predates versioning; ConfigMigrator reads
    /// the version from the document, so don't rely on this property to detect
    /// old files — the constructor defaults it to CurrentConfigVersion.
    /// </summary>
    public int ConfigVersion { get; set; }

    //Sources
    public List<RelayConfig> Routes { get; set; }
    public string? DefaultSource { get; set; }
    public Dictionary<string, string> DefaultRoutes { get; set; }
    public Dictionary<string, PhysicalButtonConfig> PhysicalSourceButtons { get; set; }
    public Dictionary<string, string> SourceColorPalette { get; set; }
    // Note: Sources and Outputs are expression-bodied members and don't need initialization here.

    //Communication
    public int ServerPort { get; set; }
    public string? TcpMirrorAddress { get; set; }
    public int? TcpMirrorPort { get; set; }
    public int? UdpApiPort { get; set; }

    //Options
    public InactiveRelaySettings? InactiveRelay { get; set; }
    public bool FlashOnSelect { get; set; }
    public bool ShowIpOnScreen { get; set; }
    public bool? ShowClockOnScreen { get; set; } // null treated as true so configs without the key keep the clock
    public bool Logging { get; set; }
    public string LogoFile { get; set; }
    public bool UseMockGpio { get; set; }
    public string? RelayDriver { get; set; } // "Auto" | "Mock" | "RpiGpio" | "K8090"; null/Auto preserves legacy behaviour
    public K8090Settings? K8090 { get; set; }
    public string ThemePalette { get; set; }

    // Parameterless constructor for struct initialization
    public AppSettings()
    {
        ConfigVersion = CurrentConfigVersion;
        PhysicalSourceButtons = new Dictionary<string, PhysicalButtonConfig>();
        DefaultRoutes = new Dictionary<string, string>();
        SourceColorPalette = new Dictionary<string, string>();
        Routes = new List<RelayConfig>();
        LogoFile = string.Empty;
        ThemePalette = "Default";
        // DefaultSource, TcpMirrorAddress are nullable strings (default to null)
        // ServerPort, TcpMirrorPort are value types (default to 0 or null)
        // InactiveRelay is a nullable struct (defaults to null)
        // Booleans (FlashOnSelect, ShowIpOnScreen, Logging) default to false.
        // Sources and Outputs are computed properties.
    }

    public IReadOnlyCollection<string> Sources => Routes?.Select(x => x.SourceName).Distinct().ToArray() ?? Array.Empty<string>();
    public IReadOnlyCollection<string> Outputs => Routes?.Select(x => x.OutputName).Distinct().ToArray() ?? Array.Empty<string>();

    public bool IsConfigured => Routes != null && Routes.Count > 0;


    // Properties moved up to group them, constructor added above.
}