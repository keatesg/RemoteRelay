using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server.Drivers;
using Xunit;

namespace RemoteRelay.Tests;

public class SainSmartTests
{
    [Fact]
    public void BuildCommand_GeneratesExpected3ByteProtocol()
    {
        // Channel 1 ON: [0xFF, 0x01, 0x01]
        var cmd1On = SainSmartRelayDriver.BuildCommand(1, energized: true);
        Assert.Equal(new byte[] { 0xFF, 0x01, 0x01 }, cmd1On);

        // Channel 1 OFF: [0xFF, 0x01, 0x00]
        var cmd1Off = SainSmartRelayDriver.BuildCommand(1, energized: false);
        Assert.Equal(new byte[] { 0xFF, 0x01, 0x00 }, cmd1Off);

        // Channel 2 ON: [0xFF, 0x02, 0x01]
        var cmd2On = SainSmartRelayDriver.BuildCommand(2, energized: true);
        Assert.Equal(new byte[] { 0xFF, 0x02, 0x01 }, cmd2On);

        // Channel 4 ON: [0xFF, 0x04, 0x01]
        var cmd4On = SainSmartRelayDriver.BuildCommand(4, energized: true);
        Assert.Equal(new byte[] { 0xFF, 0x04, 0x01 }, cmd4On);

        // Channel 4 OFF: [0xFF, 0x04, 0x00]
        var cmd4Off = SainSmartRelayDriver.BuildCommand(4, energized: false);
        Assert.Equal(new byte[] { 0xFF, 0x04, 0x00 }, cmd4Off);
    }

    [Fact]
    public void RegisterChannel_EnforcesChannelBounds()
    {
        // Use a dummy port name; driver will attempt connection in background
        using var driver = new SainSmartRelayDriver("DUMMY_PORT", channels: 4, baudRate: 9600, NullLogger.Instance);

        // Valid channels 1-4 should not throw
        for (int ch = 1; ch <= 4; ch++)
        {
            driver.RegisterChannel(new RelayConfig
            {
                RelayPin = ch,
                SourceName = $"Src{ch}",
                OutputName = $"Out{ch}"
            });
        }

        // Out of range channel (< 1) throws
        Assert.Throws<InvalidOperationException>(() =>
            driver.RegisterChannel(new RelayConfig { RelayPin = 0, SourceName = "S", OutputName = "O" }));

        // Out of range channel (> 4) throws
        Assert.Throws<InvalidOperationException>(() =>
            driver.RegisterChannel(new RelayConfig { RelayPin = 5, SourceName = "S", OutputName = "O" }));
    }

    [Theory]
    [InlineData("SainSmart")]
    [InlineData("sainsmart")]
    [InlineData("SainsmartUsb")]
    [InlineData("sainsmartusb")]
    [InlineData("KMtronic")]
    [InlineData("kmtronic")]
    public void RelayDriverFactory_ResolvesSainSmart(string driverName)
    {
        var settings = new AppSettings
        {
            RelayDriver = driverName
        };

        var kind = RelayDriverFactory.ResolveKind(settings);
        Assert.Equal(RelayDriverKind.SainSmart, kind);
    }

    [Fact]
    public void Validator_WarnsWhenSainSmartPortIsMissing()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "SainSmart",
            SainSmart = new SainSmartSettings { Port = "" },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 1 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid);
        Assert.Contains(warnings, w => w.Contains("SainSmart.Port is not set"));
    }

    [Fact]
    public void Validator_RejectsRouteChannelExceedingMaxChannels()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "SainSmart",
            SainSmart = new SainSmartSettings { Port = "COM3", Channels = 4 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 5 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.False(valid);
        Assert.Contains("SainSmart only supports channels 1-4", summary);
    }

    [Fact]
    public void Validator_AcceptsValidSainSmartConfiguration()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "SainSmart",
            SainSmart = new SainSmartSettings { Port = "COM3", Channels = 4 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 1 },
                new() { SourceName = "Mic2", OutputName = "PGM", RelayPin = 2 },
                new() { SourceName = "Mic3", OutputName = "PGM", RelayPin = 3 },
                new() { SourceName = "Mic4", OutputName = "PGM", RelayPin = 4 }
            },
            InactiveRelay = new InactiveRelaySettings { Pin = 4 }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid, summary);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Validator_SupportsExtended8ChannelSainSmart()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "SainSmart",
            SainSmart = new SainSmartSettings { Port = "/dev/ttyUSB0", Channels = 8 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Line8", OutputName = "Mon", RelayPin = 8 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid, summary);
    }
}
