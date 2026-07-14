using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Services;

/// <summary>
/// Service for saving and managing server configuration.
/// </summary>
public class ConfigurationService
{
    private readonly string _configPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public ConfigurationService(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Saves the provided settings to the configuration file.
    /// </summary>
    /// <returns>True if save was successful, false otherwise.</returns>
    public async Task<(bool Success, string? Error)> SaveAsync(AppSettings settings)
    {
        // Validate settings first
        if (!AppSettingsValidator.TryValidate(settings, out var validationSummary))
        {
            return (false, validationSummary);
        }

        // Whatever schema version the caller (e.g. an older client) held, the
        // file we write is in this build's schema.
        settings.ConfigVersion = AppSettings.CurrentConfigVersion;

        try
        {
            var json = JsonSerializer.Serialize(settings, _serializerOptions);
            await File.WriteAllTextAsync(_configPath, json).ConfigureAwait(false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to write configuration file: {ex.Message}");
        }
    }
}
