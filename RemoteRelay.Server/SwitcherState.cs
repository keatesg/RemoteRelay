using System;
using System.Collections.Generic;
using System.Device.Gpio;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;
using RemoteRelay.Server.Services;

namespace RemoteRelay.Server;

public class SwitcherState : IDisposable
{
    private readonly IHubContext<RelayHub> _hubContext;
    private readonly ILogger<SwitcherState> _logger;
    private readonly TcpMessageService _tcpMessageService;
    private readonly Dictionary<int, DateTime> _lastPinEventTime = new();
    private readonly object _stateLock = new();
    private static readonly TimeSpan _debounceTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _startupIgnoreTime = TimeSpan.FromMilliseconds(1500);
    private DateTime _lastInitializationTime = DateTime.MinValue;

    private GpioController? _gpiController;
    private AppSettings _settings;
    private List<Source> _sources = new();
    private int _outputCount;
    private GpioPin? _inactiveRelayPin;

    public SwitcherState(AppSettings settings, IHubContext<RelayHub> hubContext, ILogger<SwitcherState> logger, TcpMessageService tcpMessageService)
    {
        _hubContext = hubContext;
        _logger = logger;
        _tcpMessageService = tcpMessageService;
        InitializeState(settings, isStartup: true);
    }

    public AppSettings GetSettings()
    {
        lock (_stateLock)
        {
            return _settings;
        }
    }

    public Dictionary<string, string> GetSystemState()
    {
        lock (_stateLock)
        {
            return GetSystemStateInternal();
        }
    }

    public async Task ApplySettingsAsync(AppSettings newSettings, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> stateSnapshot;
        lock (_stateLock)
        {
            var previousState = GetSystemStateInternal();

            SetInactiveRelayToInactiveStateInternal();
            CleanupController();
            InitializeStateInternal(newSettings, isStartup: false);

            // Restore active routes if they are still valid in the new configuration
            foreach (var route in previousState)
            {
                if (!string.IsNullOrEmpty(route.Value))
                {
                    if (newSettings.Routes.Any(r => 
                        string.Equals(r.SourceName, route.Key, StringComparison.OrdinalIgnoreCase) && 
                        string.Equals(r.OutputName, route.Value, StringComparison.OrdinalIgnoreCase)))
                    {
                        Console.WriteLine($"Restoring active route: {route.Key} -> {route.Value}");
                        SwitchSource(route.Key, route.Value);
                    }
                }
            }

            stateSnapshot = GetSystemStateInternal();
        }

        try
        {
            await _hubContext.Clients.All.SendAsync("Configuration", _settings, cancellationToken).ConfigureAwait(false);
            await _hubContext.Clients.All.SendAsync("SystemState", stateSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast configuration update.");
        }
    }

    public void SwitchSource(string source, string output)
    {
        lock (_stateLock)
        {
            Console.WriteLine($"SwitchSource called: source='{source}', output='{output}', outputCount={_outputCount}");

            if (_outputCount == 1)
            {
                foreach (var x in _sources)
                {
                    if (x._sourceName == source)
                    {
                        Console.WriteLine($"Enabling output '{output}' for source '{x._sourceName}'");
                        x.EnableOutput(output);
                    }
                    else
                    {
                        Console.WriteLine($"Disabling outputs for source '{x._sourceName}'");
                        x.DisableOutput();
                    }
                }
            }
            else
            {
                foreach (var x in _sources)
                {
                    if (x._sourceName == source)
                    {
                        Console.WriteLine($"Enabling output '{output}' for source '{x._sourceName}' (multi-output mode)");
                        x.EnableOutput(output);
                    }
                    else
                    {
                        var currentRoute = x.GetCurrentRoute();
                        if (!string.IsNullOrEmpty(currentRoute) && string.Equals(currentRoute, output, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"Clearing output '{output}' from source '{x._sourceName}' due to reassignment.");
                            x.DisableOutput();
                        }
                    }
                }
            }

            // Send TCP message if configured for this route
            SendTcpMessageForRoute(source, output);
        }
    }

    public void ClearSource(string sourceName)
    {
        lock (_stateLock)
        {
            Console.WriteLine($"ClearSource called: sourceName='{sourceName}'");

            var source = _sources.FirstOrDefault(x =>
                string.Equals(x._sourceName, sourceName, StringComparison.OrdinalIgnoreCase));

            if (source != null)
            {
                Console.WriteLine($"Disabling all outputs for source '{sourceName}'");
                source.DisableOutput();
            }
            else
            {
                Console.WriteLine($"ClearSource: source '{sourceName}' not found");
            }
        }
    }

    private void SendTcpMessageForRoute(string source, string output)
    {
        // Check if TCP endpoint is configured
        if (string.IsNullOrWhiteSpace(_settings.TcpMirrorAddress) ||
            _settings.TcpMirrorPort is null or <= 0)
        {
            return;
        }

        // Find the route and check if it has a message
        var route = _settings.Routes.FirstOrDefault(r =>
            r.SourceName == source && r.OutputName == output);

        if (route != null && !string.IsNullOrWhiteSpace(route.TcpMessage))
        {
            Console.WriteLine($"Sending TCP message '{route.TcpMessage}' to {_settings.TcpMirrorAddress}:{_settings.TcpMirrorPort}");
            _ = _tcpMessageService.SendMessageAsync(
                _settings.TcpMirrorAddress,
                _settings.TcpMirrorPort.Value,
                route.TcpMessage);
        }
    }

    public void SetInactiveRelayToInactiveState()
    {
        lock (_stateLock)
        {
            SetInactiveRelayToInactiveStateInternal();
        }
    }

    /// <summary>
    /// Tests an individual GPIO pin by setting it to the specified state.
    /// Used during setup to verify pin assignments.
    /// </summary>
    public void TestPin(int pin, bool activeLow, bool active)
    {
        if (pin <= 0)
        {
            Console.WriteLine($"TestPin: Invalid pin number {pin}");
            return;
        }

        lock (_stateLock)
        {
            if (_gpiController == null)
            {
                Console.WriteLine("TestPin: GPIO controller not initialized");
                return;
            }

            try
            {
                // Determine the value to write based on activeLow and desired state
                PinValue valueToWrite;
                if (active)
                {
                    valueToWrite = activeLow ? PinValue.Low : PinValue.High;
                }
                else
                {
                    valueToWrite = activeLow ? PinValue.High : PinValue.Low;
                }

                // Open pin if not already open
                if (!_gpiController.IsPinOpen(pin))
                {
                    _gpiController.OpenPin(pin, PinMode.Output);
                }
                else
                {
                    // Ensure it's in output mode
                    var currentMode = _gpiController.GetPinMode(pin);
                    if (currentMode != PinMode.Output)
                    {
                        _gpiController.SetPinMode(pin, PinMode.Output);
                    }
                }

                _gpiController.Write(pin, valueToWrite);
                MockGpioDriver.UpdatePinState(pin, valueToWrite);
                Console.WriteLine($"TestPin: Set pin {pin} to {valueToWrite} (active={active}, activeLow={activeLow})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TestPin: Error setting pin {pin}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            CleanupController();
        }
    }

    private void InitializeState(AppSettings settings, bool isStartup = false)
    {
        lock (_stateLock)
        {
            InitializeStateInternal(settings, isStartup);
        }
    }

    private void InitializeStateInternal(AppSettings settings, bool isStartup = false)
    {
        _settings = settings;

        // Try to initialize real GPIO, fallback to mock if it fails
        if (IsGpiEnvironment())
        {
            try
            {
                _gpiController = new GpioController();
                Console.WriteLine("GPIO controller initialized successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARNING: Failed to initialize GPIO controller: {ex.Message}");
                Console.WriteLine("Falling back to mock GPIO mode.");
                _gpiController = new GpioController(new MockGpioDriver());
            }
        }
        else
        {
            _gpiController = new GpioController(new MockGpioDriver());
            Console.WriteLine("Using mock GPIO mode (not on GPIO-capable hardware or UseMockGpio=true).");
        }

        _sources = new List<Source>();

        foreach (var source in _settings.Sources)
        {
            var newSource = new Source(source);
            foreach (var outputName in _settings.Routes.Where(x => x.SourceName == source).Select(x => x.OutputName).Distinct())
            {
                var relayConfig = _settings.Routes.First(x => x.SourceName == source && x.OutputName == outputName);
                newSource.AddOutputPin(_gpiController, relayConfig);
            }
            _sources.Add(newSource);
        }

        _outputCount = _settings.Outputs.Count;

        if (isStartup)
        {
            ApplyDefaultRoutes();
        }
        
        InitializeInactiveRelayPin();
        SetupPhysicalButtons();
        
        _lastInitializationTime = DateTime.UtcNow;
    }

    private void ApplyDefaultRoutes()
    {
        if (_settings.DefaultRoutes != null && _settings.DefaultRoutes.Count > 0)
        {
            Console.WriteLine("Applying default routes from configuration...");
            var groupedByOutput = _settings.DefaultRoutes
               .Where(route => !string.IsNullOrWhiteSpace(route.Key) && !string.IsNullOrWhiteSpace(route.Value))
               .GroupBy(route => route.Value, StringComparer.OrdinalIgnoreCase)
               .ToDictionary(group => group.Key,
                  group => group.Select(x => x.Key).ToList(),
                  StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupedByOutput)
            {
                if (group.Value.Count > 1)
                {
                    Console.WriteLine($"Warning: multiple default sources configured for output '{group.Key}'. Only one can be active at startup. Selecting '{group.Value.First()}'.");
                }
            }

            foreach (var route in _settings.DefaultRoutes)
            {
                if (string.IsNullOrWhiteSpace(route.Key) || string.IsNullOrWhiteSpace(route.Value))
                    continue;

                if (groupedByOutput.TryGetValue(route.Value, out var sourcesForOutput)
                    && sourcesForOutput.Count > 1
                    && !string.Equals(sourcesForOutput.First(), route.Key, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Skipping default route {route.Key} -> {route.Value} because output already assigned to '{sourcesForOutput.First()}'.");
                    continue;
                }

                if (!_settings.Routes.Any(x => x.SourceName == route.Key && x.OutputName == route.Value))
                {
                    Console.WriteLine($"Skipping default route {route.Key} -> {route.Value} because it is not defined in routes list.");
                    continue;
                }

                Console.WriteLine($"Setting default route {route.Key} -> {route.Value}");
                SwitchSource(route.Key, route.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(_settings.DefaultSource))
        {
            Console.WriteLine($"Setting default source to: {_settings.DefaultSource}");
            var defaultRoute = _settings.Routes.FirstOrDefault(x => x.SourceName == _settings.DefaultSource);
            if (defaultRoute != null)
            {
                Console.WriteLine($"Default output: {defaultRoute.OutputName}");
                SwitchSource(_settings.DefaultSource, defaultRoute.OutputName);
            }
            else
            {
                Console.WriteLine($"No route found for default source {_settings.DefaultSource}. Skipping initial switch.");
            }
        }
    }

    private void InitializeInactiveRelayPin()
    {
        _inactiveRelayPin = null;

        if (_settings.InactiveRelay == null || _settings.InactiveRelay.Pin <= 0 || _gpiController == null)
        {
            return;
        }

        try
        {
            _inactiveRelayPin = _gpiController.OpenPin(_settings.InactiveRelay.Pin, PinMode.Output);
            _inactiveRelayPin.Write(_settings.InactiveRelay.GetActivePinValue());
            Console.WriteLine($"Inactive relay pin {_settings.InactiveRelay.Pin} initialized to active state ({_settings.InactiveRelay.GetActivePinValue()}).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing inactive relay pin {_settings.InactiveRelay.Pin}: {ex.Message}");
            _inactiveRelayPin = null;
        }
    }

    private void SetupPhysicalButtons()
    {
        _lastPinEventTime.Clear();

        if (_settings.PhysicalSourceButtons == null || _gpiController == null)
        {
            return;
        }

        foreach (var buttonEntry in _settings.PhysicalSourceButtons)
        {
            var sourceNameForButton = buttonEntry.Key;
            var buttonConfig = buttonEntry.Value;

            if (buttonConfig.PinNumber <= 0)
            {
                Console.WriteLine($"Skipping physical button for source '{sourceNameForButton}' due to invalid pin number: {buttonConfig.PinNumber}");
                continue;
            }

            Console.WriteLine($"Setting up physical button for source '{sourceNameForButton}' on pin {buttonConfig.PinNumber} with trigger state {buttonConfig.TriggerState}");
            try
            {
                if (!_gpiController.IsPinOpen(buttonConfig.PinNumber))
                {
                    _gpiController.OpenPin(buttonConfig.PinNumber, PinMode.InputPullUp);
                }
                else
                {
                    _gpiController.SetPinMode(buttonConfig.PinNumber, PinMode.InputPullUp);
                }

                _gpiController.RegisterCallbackForPinValueChangedEvent(
                   buttonConfig.PinNumber,
                   buttonConfig.GetTriggerEventType(),
                   HandlePhysicalButtonChangeEvent);
                Console.WriteLine($"Successfully registered callback for pin {buttonConfig.PinNumber} for source '{sourceNameForButton}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting up physical button for source '{sourceNameForButton}' on pin {buttonConfig.PinNumber}: {ex.Message}");
            }
        }
    }

    private void HandlePhysicalButtonChangeEvent(object sender, PinValueChangedEventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastInitializationTime < _startupIgnoreTime)
        {
            Console.WriteLine($"Ignoring startup transient event on pin {e.PinNumber}.");
            return;
        }

        if (_lastPinEventTime.TryGetValue(e.PinNumber, out DateTime lastEventTime))
        {
            if (now - lastEventTime < _debounceTime)
            {
                Console.WriteLine($"Bounce detected on pin {e.PinNumber}. Ignoring event.");
                return;
            }
        }
        _lastPinEventTime[e.PinNumber] = now;

        Console.WriteLine($"Pin change event: Pin {e.PinNumber}, Type {e.ChangeType} (debounced)");

        KeyValuePair<string, PhysicalButtonConfig> matchedButtonEntry;
        lock (_stateLock)
        {
            matchedButtonEntry = _settings.PhysicalSourceButtons
               .FirstOrDefault(b => b.Value.PinNumber == e.PinNumber);
        }

        if (matchedButtonEntry.Key == null)
        {
            Console.WriteLine($"Error: No configured button found for pin {e.PinNumber}.");
            return;
        }

        var sourceName = matchedButtonEntry.Key;
        var buttonConfig = matchedButtonEntry.Value;

        if (e.ChangeType == buttonConfig.GetTriggerEventType())
        {
            Console.WriteLine($"Physical button pressed for source '{sourceName}' on pin {e.PinNumber}.");

            string? targetOutputName;
            lock (_stateLock)
            {
                targetOutputName = _settings.Routes.FirstOrDefault(r => r.SourceName == sourceName)?.OutputName;
            }

            if (targetOutputName == null)
            {
                Console.WriteLine($"Error: No route found for source '{sourceName}' triggered by pin {e.PinNumber}.");
                return;
            }

            SwitchSource(sourceName, targetOutputName);
            Console.WriteLine($"Switched to source '{sourceName}' output '{targetOutputName}' due to physical button press on pin {e.PinNumber}.");

            var _ = _hubContext.Clients.All.SendAsync("SystemState", GetSystemState());
        }
        else
        {
            Console.WriteLine($"Pin event {e.ChangeType} for pin {e.PinNumber} (source '{sourceName}') was not the configured trigger event type ({buttonConfig.GetTriggerEventType()}).");
        }
    }

    private Dictionary<string, string> GetSystemStateInternal()
    {
        var state = new Dictionary<string, string>();
        foreach (var x in _sources)
        {
            var currentRoute = x.GetCurrentRoute();
            state.Add(x._sourceName, currentRoute);
            Console.WriteLine($"Source '{x._sourceName}' current route: '{currentRoute}'");
        }

        Console.WriteLine($"System state: {string.Join(", ", state.Select(kvp => $"{kvp.Key}='{kvp.Value}'"))}");
        return state;
    }

    private void SetInactiveRelayToInactiveStateInternal()
    {
        if (_inactiveRelayPin != null && _settings.InactiveRelay != null)
        {
            try
            {
                var inactiveValue = _settings.InactiveRelay.GetInactivePinValue();
                _inactiveRelayPin.Write(inactiveValue);
                Console.WriteLine($"Inactive relay pin {_settings.InactiveRelay.Pin} set to inactive state ({inactiveValue}).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting inactive relay pin {_settings.InactiveRelay.Pin} to inactive state: {ex.Message}");
            }
        }
    }

    private void CleanupController()
    {
        if (_inactiveRelayPin != null)
        {
            try
            {
                _inactiveRelayPin.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing inactive relay pin");
            }
            finally
            {
                _inactiveRelayPin = null;
            }
        }

        foreach (var buttonEntry in _settings.PhysicalSourceButtons ?? new Dictionary<string, PhysicalButtonConfig>())
        {
            try
            {
                if (_gpiController?.IsPinOpen(buttonEntry.Value.PinNumber) == true)
                {
                    _gpiController.UnregisterCallbackForPinValueChangedEvent(
                       buttonEntry.Value.PinNumber,
                       HandlePhysicalButtonChangeEvent);
                    _gpiController.ClosePin(buttonEntry.Value.PinNumber);
                }
            }
            catch
            {
                // Ignore cleanup errors for buttons
            }
        }

        _sources.Clear();
        _lastPinEventTime.Clear();

        if (_gpiController != null)
        {
            try
            {
                _gpiController.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing GPIO controller");
            }
            finally
            {
                _gpiController = null;
            }
        }
    }

    private bool IsGpiEnvironment()
    {
        if (_settings.UseMockGpio)
            return false;

        if (Environment.OSVersion.Platform != PlatformID.Unix)
            return false;

        // Check if GPIO is actually available on the system
        // On Linux, GPIO hardware is typically exposed via /sys/class/gpio
        return Directory.Exists("/sys/class/gpio");
    }
}