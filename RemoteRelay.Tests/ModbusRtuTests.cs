using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server.Drivers;
using Xunit;

namespace RemoteRelay.Tests;

public class ModbusRtuTests
{
    [Fact]
    public void BuildCommand_GeneratesStandardModbusFunction05()
    {
        // Channel 1 ON: Slave 1, Func 5, Coil 0, 0xFF00, CRC (0x8C, 0x3A)
        var cmd1On = ModbusRtuRelayDriver.BuildCommand(1, 1, energized: true);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x00, 0x00, 0xFF, 0x00, 0x8C, 0x3A }, cmd1On);

        // Channel 1 OFF: Slave 1, Func 5, Coil 0, 0x0000, CRC (0xCD, 0xCA)
        var cmd1Off = ModbusRtuRelayDriver.BuildCommand(1, 1, energized: false);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0xCD, 0xCA }, cmd1Off);

        // Channel 2 ON: Slave 1, Func 5, Coil 1, 0xFF00, CRC (0xDD, 0xFA)
        var cmd2On = ModbusRtuRelayDriver.BuildCommand(1, 2, energized: true);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x00, 0x01, 0xFF, 0x00, 0xDD, 0xFA }, cmd2On);

        // Channel 2 OFF: Slave 1, Func 5, Coil 1, 0x0000, CRC (0x9C, 0x0A)
        var cmd2Off = ModbusRtuRelayDriver.BuildCommand(1, 2, energized: false);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x00, 0x01, 0x00, 0x00, 0x9C, 0x0A }, cmd2Off);
    }

    [Fact]
    public void RegisterChannel_EnforcesBounds()
    {
        using var driver = new ModbusRtuRelayDriver("DUMMY_PORT", channels: 8, baudRate: 9600, slaveId: 1, NullLogger.Instance);

        for (int ch = 1; ch <= 8; ch++)
        {
            driver.RegisterChannel(new RelayConfig
            {
                RelayPin = ch,
                SourceName = $"Src{ch}",
                OutputName = $"Out{ch}"
            });
        }

        Assert.Throws<InvalidOperationException>(() =>
            driver.RegisterChannel(new RelayConfig { RelayPin = 0, SourceName = "S", OutputName = "O" }));

        Assert.Throws<InvalidOperationException>(() =>
            driver.RegisterChannel(new RelayConfig { RelayPin = 9, SourceName = "S", OutputName = "O" }));
    }

    [Theory]
    [InlineData("ModbusRTU")]
    [InlineData("modbusrtu")]
    [InlineData("Modbus")]
    [InlineData("modbus")]
    [InlineData("Waveshare")]
    [InlineData("waveshare")]
    public void RelayDriverFactory_ResolvesModbusRtu(string driverName)
    {
        var settings = new AppSettings
        {
            RelayDriver = driverName
        };

        var kind = RelayDriverFactory.ResolveKind(settings);
        Assert.Equal(RelayDriverKind.ModbusRtu, kind);
    }

    [Fact]
    public void Validator_WarnsWhenModbusPortIsMissing()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "ModbusRTU",
            ModbusRTU = new ModbusSettings { Port = "" },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 1 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid);
        Assert.Contains(warnings, w => w.Contains("ModbusRTU.Port is not set"));
    }

    [Fact]
    public void Validator_RejectsRouteChannelExceedingMaxChannels()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "ModbusRTU",
            ModbusRTU = new ModbusSettings { Port = "COM3", Channels = 8 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 9 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.False(valid);
        Assert.Contains("ModbusRTU only supports channels 1-8", summary);
    }

    [Fact]
    public void Validator_AcceptsValidModbusConfiguration()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "ModbusRTU",
            ModbusRTU = new ModbusSettings { Port = "/dev/ttyUSB0", Channels = 16, SlaveId = 1 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Ch1", OutputName = "Main", RelayPin = 1 },
                new() { SourceName = "Ch16", OutputName = "Main", RelayPin = 16 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid, summary);
        Assert.Empty(warnings);
    }
}
