using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server;
using RemoteRelay.Server.Services;
using Xunit;

namespace RemoteRelay.Tests;

public class ConfigPinTests
{
    [Fact]
    public void AppSettings_SerializesAndDeserializesConfigPin()
    {
        var settings = new AppSettings
        {
            ServerPort = 33101,
            ConfigPin = "1234"
        };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

        Assert.Equal("1234", deserialized.ConfigPin);
    }

    [Fact]
    public void AppSettings_DefaultsConfigPinToNull()
    {
        var settings = new AppSettings();
        Assert.Null(settings.ConfigPin);
    }

    [Fact]
    public async Task RelayHub_IsPinRequired_ReflectsConfigPin()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { ServerPort = 33101, RelayDriver = "Mock" };
            using var switcherState = new SwitcherState(settings, null!, NullLogger<SwitcherState>.Instance, new TcpMessageService(NullLogger<TcpMessageService>.Instance));
            var configService = new ConfigurationService(tempFile);
            var hub = new RelayHub(switcherState, configService);

            Assert.False(hub.IsPinRequired());

            settings.ConfigPin = "secret";
            await switcherState.ApplySettingsAsync(settings);

            Assert.True(hub.IsPinRequired());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RelayHub_SaveConfiguration_AllowsSaveWhenNoPinConfigured()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { ServerPort = 33101, RelayDriver = "Mock", ConfigPin = null };
            using var switcherState = new SwitcherState(settings, null!, NullLogger<SwitcherState>.Instance, new TcpMessageService(NullLogger<TcpMessageService>.Instance));
            var configService = new ConfigurationService(tempFile);
            var hub = new RelayHub(switcherState, configService);

            var newSettings = settings;
            newSettings.ServerPort = 33105;

            var response = await hub.SaveConfiguration(newSettings, null);

            Assert.True(response.Success);
            Assert.Null(response.Error);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RelayHub_SaveConfiguration_RejectsMissingOrWrongPin()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { ServerPort = 33101, RelayDriver = "Mock", ConfigPin = "9876" };
            using var switcherState = new SwitcherState(settings, null!, NullLogger<SwitcherState>.Instance, new TcpMessageService(NullLogger<TcpMessageService>.Instance));
            var configService = new ConfigurationService(tempFile);
            var hub = new RelayHub(switcherState, configService);

            // Attempt without pin
            var respNoPin = await hub.SaveConfiguration(settings, null);
            Assert.False(respNoPin.Success);
            Assert.Equal("Invalid or missing configuration PIN.", respNoPin.Error);

            // Attempt with wrong pin
            var respWrongPin = await hub.SaveConfiguration(settings, "0000");
            Assert.False(respWrongPin.Success);
            Assert.Equal("Invalid or missing configuration PIN.", respWrongPin.Error);

            // Attempt with correct pin
            var respCorrect = await hub.SaveConfiguration(settings, "9876");
            Assert.True(respCorrect.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RelayHub_SaveConfiguration_AllowsUpdatingPinWithCurrentPin()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var settings = new AppSettings { ServerPort = 33101, RelayDriver = "Mock", ConfigPin = "1111" };
            using var switcherState = new SwitcherState(settings, null!, NullLogger<SwitcherState>.Instance, new TcpMessageService(NullLogger<TcpMessageService>.Instance));
            var configService = new ConfigurationService(tempFile);
            var hub = new RelayHub(switcherState, configService);

            var updatedSettings = settings;
            updatedSettings.ConfigPin = "2222";

            var resp = await hub.SaveConfiguration(updatedSettings, "1111");
            Assert.True(resp.Success);

            // New pin should now be in effect
            Assert.Equal("2222", switcherState.GetSettings().ConfigPin);

            // Old pin no longer works
            var respOld = await hub.SaveConfiguration(updatedSettings, "1111");
            Assert.False(respOld.Success);

            // New pin works
            var respNew = await hub.SaveConfiguration(updatedSettings, "2222");
            Assert.True(respNew.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
