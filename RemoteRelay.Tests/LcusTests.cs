using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server.Drivers;
using Xunit;

namespace RemoteRelay.Tests;

public class LcusTests
{
    [Fact]
    public void BuildCommand_GeneratesExpected4ByteProtocol()
    {
        // Channel 1 ON: A0 01 01 A2 (0xA0 + 0x01 + 0x01 = 0xA2)
        var cmd1On = LcusRelayDriver.BuildCommand(1, energized: true);
        Assert.Equal(new byte[] { 0xA0, 0x01, 0x01, 0xA2 }, cmd1On);

        // Channel 1 OFF: A0 01 00 A1 (0xA0 + 0x01 + 0x00 = 0xA1)
        var cmd1Off = LcusRelayDriver.BuildCommand(1, energized: false);
        Assert.Equal(new byte[] { 0xA0, 0x01, 0x00, 0xA1 }, cmd1Off);

        // Channel 2 ON: A0 02 01 A3
        var cmd2On = LcusRelayDriver.BuildCommand(2, energized: true);
        Assert.Equal(new byte[] { 0xA0, 0x02, 0x01, 0xA3 }, cmd2On);

        // Channel 2 OFF: A0 02 00 A2
        var cmd2Off = LcusRelayDriver.BuildCommand(2, energized: false);
        Assert.Equal(new byte[] { 0xA0, 0x02, 0x00, 0xA2 }, cmd2Off);

        // Channel 4 ON: A0 04 01 A5
        var cmd4On = LcusRelayDriver.BuildCommand(4, energized: true);
        Assert.Equal(new byte[] { 0xA0, 0x04, 0x01, 0xA5 }, cmd4On);

        // Channel 4 OFF: A0 04 00 A4
        var cmd4Off = LcusRelayDriver.BuildCommand(4, energized: false);
        Assert.Equal(new byte[] { 0xA0, 0x04, 0x00, 0xA4 }, cmd4Off);
    }

    [Fact]
    public void RegisterChannel_EnforcesChannelBounds()
    {
        using var driver = new LcusRelayDriver("DUMMY_PORT", channels: 4, baudRate: 9600, NullLogger.Instance);

        for (int ch = 1; ch <= 4; ch++)
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
            driver.RegisterChannel(new RelayConfig { RelayPin = 5, SourceName = "S", OutputName = "O" }));
    }

    [Theory]
    [InlineData("LCUS")]
    [InlineData("lcus")]
    [InlineData("Seeit")]
    [InlineData("seeit")]
    [InlineData("LCTech")]
    [InlineData("lctech")]
    public void RelayDriverFactory_ResolvesLcus(string driverName)
    {
        var settings = new AppSettings
        {
            RelayDriver = driverName
        };

        var kind = RelayDriverFactory.ResolveKind(settings);
        Assert.Equal(RelayDriverKind.Lcus, kind);
    }

    [Fact]
    public void Validator_WarnsWhenLcusPortIsMissing()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "LCUS",
            LCUS = new LcusSettings { Port = "" },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 1 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid);
        Assert.Contains(warnings, w => w.Contains("LCUS.Port is not set"));
    }

    [Fact]
    public void Validator_RejectsRouteChannelExceedingMaxChannels()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "LCUS",
            LCUS = new LcusSettings { Port = "COM3", Channels = 4 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 5 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.False(valid);
        Assert.Contains("LCUS only supports channels 1-4", summary);
    }

    [Fact]
    public void Validator_AcceptsValidLcusConfiguration()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            RelayDriver = "LCUS",
            LCUS = new LcusSettings { Port = "COM3", Channels = 4 },
            Routes = new List<RelayConfig>
            {
                new() { SourceName = "Mic1", OutputName = "PGM", RelayPin = 1 },
                new() { SourceName = "Mic2", OutputName = "PGM", RelayPin = 2 },
                new() { SourceName = "Mic3", OutputName = "PGM", RelayPin = 3 },
                new() { SourceName = "Mic4", OutputName = "PGM", RelayPin = 4 }
            }
        };

        var valid = AppSettingsValidator.TryValidate(settings, out var summary, out var warnings);
        Assert.True(valid, summary);
        Assert.Empty(warnings);
    }
}
