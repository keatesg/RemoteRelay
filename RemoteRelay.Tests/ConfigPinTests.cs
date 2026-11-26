using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server;
using RemoteRelay.Server.Services;
using Xunit;

namespace RemoteRelay.Tests;

public class ConfigPinTests
{
    [Fact]
    public void RelayHub_MapHub_SucceedsWithoutDuplicateMethodNames()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSignalR();
        var app = builder.Build();

        // SignalR's DefaultHubDispatcher discovers hub methods inside MapHub and
        // throws NotSupportedException if any hub method name is overloaded.
        var ex = Record.Exception(() => app.MapHub<RelayHub>("/relay"));
        Assert.Null(ex);
    }

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

            var response = await hub.SaveConfigurationWithPin(newSettings, null);

            Assert.True(response.Success);
            Assert.Null(response.Error);

            // Legacy 1-argument hub method also works when no PIN is configured
            newSettings.ServerPort = 33106;
            var legacyResponse = await hub.SaveConfiguration(newSettings);
            Assert.True(legacyResponse.Success);
            Assert.Null(legacyResponse.Error);
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

            // Attempt via legacy 1-argument method when PIN is required
            var respLegacy = await hub.SaveConfiguration(settings);
            Assert.False(respLegacy.Success);
            Assert.Equal("Invalid or missing configuration PIN.", respLegacy.Error);

            // Attempt without pin
            var respNoPin = await hub.SaveConfigurationWithPin(settings, null);
            Assert.False(respNoPin.Success);
            Assert.Equal("Invalid or missing configuration PIN.", respNoPin.Error);

            // Attempt with wrong pin
            var respWrongPin = await hub.SaveConfigurationWithPin(settings, "0000");
            Assert.False(respWrongPin.Success);
            Assert.Equal("Invalid or missing configuration PIN.", respWrongPin.Error);

            // Attempt with correct pin
            var respCorrect = await hub.SaveConfigurationWithPin(settings, "9876");
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

            var resp = await hub.SaveConfigurationWithPin(updatedSettings, "1111");
            Assert.True(resp.Success);

            // New pin should now be in effect
            Assert.Equal("2222", switcherState.GetSettings().ConfigPin);

            // Old pin no longer works
            var respOld = await hub.SaveConfigurationWithPin(updatedSettings, "1111");
            Assert.False(respOld.Success);

            // New pin works
            var respNew = await hub.SaveConfigurationWithPin(updatedSettings, "2222");
            Assert.True(respNew.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
