using System;
using System.Text.Json.Nodes;

namespace RemoteRelay.Common;

/// <summary>
/// Upgrades config.json documents written by older releases to the current
/// schema before they are deserialized into <see cref="AppSettings"/>.
///
/// To change the schema: bump <see cref="AppSettings.CurrentConfigVersion"/>,
/// then add an <c>if (version &lt; N)</c> step below that reshapes version N-1
/// into N. Steps run in order, so arbitrarily old files upgrade in one pass.
/// </summary>
public static class ConfigMigrator
{
    /// <summary>
    /// Migrates the document in place. Returns true if anything changed and the
    /// file should be rewritten.
    /// </summary>
    public static bool Migrate(JsonObject config)
    {
        var version = ReadVersion(config);

        if (version > AppSettings.CurrentConfigVersion)
        {
            // Written by a newer release (e.g. after a rollback). Don't touch it:
            // unknown keys are ignored on load and must survive a later upgrade.
            return false;
        }

        if (version == AppSettings.CurrentConfigVersion)
        {
            return false;
        }

        if (version < 1)
        {
            // v0 -> v1: pre-versioning files could store PhysicalSourceButtons as
            // { "<source>": <pinInt> } instead of { "<source>": { "PinNumber": <pinInt>, "TriggerState": "Low" } }.
            MigrateLegacyPhysicalButtons(config);
        }

        SetVersion(config, AppSettings.CurrentConfigVersion);
        return true;
    }

    private static void MigrateLegacyPhysicalButtons(JsonObject config)
    {
        foreach (var property in config)
        {
            if (!string.Equals(property.Key, nameof(AppSettings.PhysicalSourceButtons), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value is not JsonObject buttons)
            {
                return;
            }

            var keysToMigrate = new System.Collections.Generic.List<(string Key, int Pin)>();
            foreach (var entry in buttons)
            {
                if (entry.Value is JsonValue val && val.TryGetValue<int>(out var pin))
                {
                    keysToMigrate.Add((entry.Key, pin));
                }
            }

            foreach (var (key, pin) in keysToMigrate)
            {
                buttons[key] = new JsonObject
                {
                    [nameof(PhysicalButtonConfig.PinNumber)] = pin,
                    [nameof(PhysicalButtonConfig.TriggerState)] = "Low"
                };
            }

            return;
        }
    }

    // Property-name matching is case-insensitive everywhere else the config is
    // read, so tolerate any casing of "ConfigVersion" here too.
    private static int ReadVersion(JsonObject config)
    {
        foreach (var property in config)
        {
            if (string.Equals(property.Key, "ConfigVersion", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
            }
        }

        return 0;
    }

    private static void SetVersion(JsonObject config, int version)
    {
        string key = "ConfigVersion";
        foreach (var property in config)
        {
            if (string.Equals(property.Key, "ConfigVersion", StringComparison.OrdinalIgnoreCase))
            {
                key = property.Key;
                break;
            }
        }

        config[key] = version;
    }
}
