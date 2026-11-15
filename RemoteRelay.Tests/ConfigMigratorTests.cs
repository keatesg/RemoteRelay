using System.Text.Json;
using System.Text.Json.Nodes;
using RemoteRelay.Common;
using Xunit;

namespace RemoteRelay.Tests;

public class ConfigMigratorTests
{
    [Fact]
    public void Migrate_LegacyPreV1ConfigWithIntegerPhysicalButtons_ConvertsToPhysicalButtonConfigAndDeserializes()
    {
        const string legacyJson = """
            {
              "Routes": [
                {
                  "SourceName": "Studio 1",
                  "OutputName": "Newsroom 1",
                  "RelayPin": 26,
                  "ActiveLow": true
                },
                {
                  "SourceName": "Studio 2",
                  "OutputName": "Newsroom 1",
                  "RelayPin": 20,
                  "ActiveLow": true
                }
              ],
              "DefaultSource": "Studio 1",
              "PhysicalSourceButtons": {
                "Studio 1": 10,
                "Studio 2": 9
              },
              "ServerPort": 33101,
              "InactiveRelay": {
                "Pin": 12,
                "InactiveState": "High"
              },
              "FlashOnSelect": true,
              "ShowIpOnScreen": true,
              "Logging": true,
              "LogoFile": ""
            }
            """;

        var obj = JsonNode.Parse(legacyJson)!.AsObject();
        var changed = ConfigMigrator.Migrate(obj);

        Assert.True(changed);
        Assert.Equal(AppSettings.CurrentConfigVersion, obj["ConfigVersion"]!.GetValue<int>());

        var settings = JsonSerializer.Deserialize<AppSettings>(obj.ToJsonString(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.Equal(2, settings.PhysicalSourceButtons.Count);
        Assert.Equal(10, settings.PhysicalSourceButtons["Studio 1"].PinNumber);
        Assert.Equal("Low", settings.PhysicalSourceButtons["Studio 1"].TriggerState);
        Assert.Equal(9, settings.PhysicalSourceButtons["Studio 2"].PinNumber);
        Assert.Equal("Low", settings.PhysicalSourceButtons["Studio 2"].TriggerState);
        Assert.True(AppSettingsValidator.TryValidate(settings, out _));
    }

    [Fact]
    public void Migrate_CaseInsensitiveKeysAndMixedPhysicalButtonEntries_PreservesObjectEntries()
    {
        const string json = """
            {
              "physicalsourcebuttons": {
                "Studio 1": 10,
                "Studio 2": { "PinNumber": 9, "TriggerState": "High" }
              },
              "ServerPort": 33101
            }
            """;

        var obj = JsonNode.Parse(json)!.AsObject();
        var changed = ConfigMigrator.Migrate(obj);

        Assert.True(changed);
        var buttons = obj["physicalsourcebuttons"]!.AsObject();
        Assert.Equal(10, buttons["Studio 1"]!["PinNumber"]!.GetValue<int>());
        Assert.Equal("Low", buttons["Studio 1"]!["TriggerState"]!.GetValue<string>());
        Assert.Equal(9, buttons["Studio 2"]!["PinNumber"]!.GetValue<int>());
        Assert.Equal("High", buttons["Studio 2"]!["TriggerState"]!.GetValue<string>());
    }

    [Fact]
    public void Migrate_CurrentVersionConfig_ReturnsFalseAndDoesNotModify()
    {
        const string json = """
            {
              "ConfigVersion": 1,
              "ServerPort": 33101
            }
            """;

        var obj = JsonNode.Parse(json)!.AsObject();
        Assert.False(ConfigMigrator.Migrate(obj));
    }

    [Fact]
    public void Migrate_NewerVersionConfig_ReturnsFalseAndLeavesUntouched()
    {
        const string json = """
            {
              "ConfigVersion": 99,
              "ServerPort": 33101,
              "FutureField": "keep-me"
            }
            """;

        var obj = JsonNode.Parse(json)!.AsObject();
        Assert.False(ConfigMigrator.Migrate(obj));
        Assert.Equal(99, obj["ConfigVersion"]!.GetValue<int>());
        Assert.Equal("keep-me", obj["FutureField"]!.GetValue<string>());
    }
}
