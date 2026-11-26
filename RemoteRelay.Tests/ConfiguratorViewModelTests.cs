using System.Collections.Generic;
using System.Linq;
using RemoteRelay.Common;
using RemoteRelay.Configurator.ViewModels;
using Xunit;

namespace RemoteRelay.Tests;

public class ConfiguratorViewModelTests
{
    [Fact]
    public void LoadAndBuild_PreservesDefaultRoutesWithoutOverwritingUnassignedInputs()
    {
        var original = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "RpiGpio",
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Studio 1", OutputName = "Air", RelayPin = 17, ActiveLow = true, TcpMessage = "S1_ON" },
                new() { SourceName = "Studio 2", OutputName = "Air", RelayPin = 18, ActiveLow = false, TcpMessage = "S2_ON" },
                new() { SourceName = "Automation", OutputName = "Air", RelayPin = 27, ActiveLow = true }
            },
            DefaultRoutes = new Dictionary<string, string>
            {
                ["Studio 1"] = "Air"
            },
            DefaultSource = "Studio 1",
            InactiveRelay = new InactiveRelaySettings { Pin = 22, InactiveState = "Low" },
            PhysicalSourceButtons = new Dictionary<string, PhysicalButtonConfig>
            {
                ["Studio 1"] = new() { PinNumber = 5, TriggerState = "High" }
            }
        };

        var vm = new ConfiguratorViewModel(original, null, () => null);

        // ComboBox item sources must contain the bound string values directly
        Assert.Contains(vm.InactiveRelayState, vm.AvailablePinStates);
        Assert.Equal("Low", vm.InactiveRelayState);
        Assert.Equal("High", vm.Inputs[0].PhysicalButtonTrigger);
        Assert.Contains(vm.Inputs[0].PhysicalButtonTrigger, vm.Inputs[0].AvailableTriggerStates);

        // Only Studio 1 should have a default output selected; the rest stay (None)
        Assert.Equal("Air", vm.DefaultRoutes.Single(d => d.SourceName == "Studio 1").SelectedOutput);
        Assert.Equal(DefaultRouteViewModel.NoDefaultOutput, vm.DefaultRoutes.Single(d => d.SourceName == "Studio 2").SelectedOutput);
        Assert.Equal(DefaultRouteViewModel.NoDefaultOutput, vm.DefaultRoutes.Single(d => d.SourceName == "Automation").SelectedOutput);

        var rebuilt = vm.BuildAppSettings();

        Assert.Single(rebuilt.DefaultRoutes);
        Assert.Equal("Air", rebuilt.DefaultRoutes["Studio 1"]);
        Assert.Equal("Studio 1", rebuilt.DefaultSource);

        // Per-route TcpMessage round-trips intact
        Assert.Equal("S1_ON", rebuilt.Routes.Single(r => r.SourceName == "Studio 1").TcpMessage);
        Assert.Equal("S2_ON", rebuilt.Routes.Single(r => r.SourceName == "Studio 2").TcpMessage);
        Assert.Null(rebuilt.Routes.Single(r => r.SourceName == "Automation").TcpMessage);

        // Inactive relay and physical button trigger round-trip intact
        Assert.NotNull(rebuilt.InactiveRelay);
        Assert.Equal(22, rebuilt.InactiveRelay!.Pin);
        Assert.Equal("Low", rebuilt.InactiveRelay.InactiveState);
        Assert.Equal("High", rebuilt.PhysicalSourceButtons["Studio 1"].TriggerState);
    }

    [Fact]
    public void LoadAndBuild_PreservesSerialDriverConfigsWhenDriverIsAuto()
    {
        var original = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "Auto",
            UseMockGpio = true,
            K8090 = new K8090Settings { Port = "/dev/ttyACM0" },
            SainSmart = new SainSmartSettings { Port = "/dev/ttyUSB0", Channels = 8, BaudRate = 19200 },
            LCUS = new LcusSettings { Port = "/dev/ttyUSB1", Channels = 4, BaudRate = 9600 },
            ModbusRTU = new ModbusSettings { Port = "/dev/ttyUSB2", Channels = 16, BaudRate = 38400, SlaveId = 3 }
        };

        var vm = new ConfiguratorViewModel(original, null, () => null);
        var rebuilt = vm.BuildAppSettings();

        Assert.Equal("Auto", rebuilt.RelayDriver);
        Assert.True(rebuilt.UseMockGpio);
        Assert.Equal("/dev/ttyACM0", rebuilt.K8090?.Port);
        Assert.Equal("/dev/ttyUSB0", rebuilt.SainSmart?.Port);
        Assert.Equal(8, rebuilt.SainSmart?.Channels);
        Assert.Equal(19200, rebuilt.SainSmart?.BaudRate);
        Assert.Equal("/dev/ttyUSB1", rebuilt.LCUS?.Port);
        Assert.Equal("/dev/ttyUSB2", rebuilt.ModbusRTU?.Port);
        Assert.Equal(38400, rebuilt.ModbusRTU?.BaudRate);
        Assert.Equal((byte)3, rebuilt.ModbusRTU?.SlaveId);
    }
}
