using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteRelay.Common;

namespace RemoteRelay.Server.Configuration;

public class ConfigurationWatcher : IHostedService, IDisposable
{
    private const int ReadRetryCount = 5;
    private const int ReadRetryDelayMilliseconds = 200;

    private readonly string _configPath;
    private readonly SwitcherState _switcherState;
    private readonly ILogger<ConfigurationWatcher> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private FileSystemWatcher? _watcher;

    public ConfigurationWatcher(string configPath, SwitcherState switcherState, ILogger<ConfigurationWatcher> logger)
    {
        _configPath = configPath;
        _switcherState = switcherState;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_configPath);
        var fileName = Path.GetFileName(_configPath);

        if (directory == null || fileName == null)
        {
            throw new InvalidOperationException($"Unable to parse configuration path '{_configPath}'.");
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };

        _watcher.Changed += OnConfigChanged;
        _watcher.Created += OnConfigChanged;
        _watcher.Renamed += OnConfigRenamed;
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation("Watching configuration file at {ConfigPath}", _configPath);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigChanged;
            _watcher.Created -= OnConfigChanged;
            _watcher.Renamed -= OnConfigRenamed;
            _watcher.Dispose();
            _watcher = null;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadLock.Dispose();
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        _ = ReloadConfigurationAsync();
    }

    private void OnConfigRenamed(object sender, RenamedEventArgs e)
    {
        if (e.FullPath.Equals(_configPath, StringComparison.OrdinalIgnoreCase))
        {
            _ = ReloadConfigurationAsync();
        }
    }

    private async Task ReloadConfigurationAsync()
    {
        await _reloadLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await Task.Delay(300).ConfigureAwait(false);

            var newSettings = await ReadSettingsAsync().ConfigureAwait(false);
            if (newSettings is not AppSettings settings)
            {
                return;
            }

            var currentSettings = _switcherState.GetSettings();
            if (settings.ServerPort != currentSettings.ServerPort)
            {
                _logger.LogWarning("ServerPort change detected in configuration file. Restart the server to apply the new port. The running instance will continue using port {Port}.", currentSettings.ServerPort);
                settings.ServerPort = currentSettings.ServerPort;
            }

            // Skip if the file matches what is already applied. Re-applying tears down and
            // re-creates the relay driver, which physically drops every active relay, so a
            // save made through the hub (which applies directly) or duplicate FileSystemWatcher
            // events must not trigger a second apply.
            if (JsonSerializer.Serialize(settings, _serializerOptions) ==
                JsonSerializer.Serialize(currentSettings, _serializerOptions))
            {
                _logger.LogDebug("Configuration file change matches the currently applied settings; skipping reload.");
                return;
            }

            await _switcherState.ApplySettingsAsync(settings).ConfigureAwait(false);
            _logger.LogInformation("Configuration reload completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload configuration file.");
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private async Task<AppSettings?> ReadSettingsAsync()
    {
        for (var attempt = 1; attempt <= ReadRetryCount; attempt++)
        {
            try
            {
                await using var stream = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _serializerOptions).ConfigureAwait(false);

                if (settings.Routes == null)
                {
                    _logger.LogWarning("Configuration file did not contain a valid AppSettings payload.");
                    return null;
                }

                if (!AppSettingsValidator.TryValidate(settings, out var summary))
                {
                    _logger.LogWarning(summary);
                    return null;
                }

                return settings;
            }
            catch (IOException) when (attempt < ReadRetryCount)
            {
                await Task.Delay(ReadRetryDelayMilliseconds).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Configuration file contains invalid JSON.");
                return null;
            }
        }

        _logger.LogError("Unable to read configuration file after {Attempts} attempts.", ReadRetryCount);
        return null;
    }
}
