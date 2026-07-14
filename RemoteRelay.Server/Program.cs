using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR; // Added for IHubContext
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteRelay.Common;
using RemoteRelay.Server.Configuration;
using RemoteRelay.Server.Drivers;
using RemoteRelay.Server.Services;

namespace RemoteRelay.Server;

public class Program
{
    private static readonly string ErrorLogPath = AppPaths.ServerLogPath;
    private static readonly DateTime StartTime = DateTime.UtcNow;

    public static void Main(string[] args)
    {
        // Set up global exception handlers for crash prevention
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine(VersionHelper.GetVersion());
            return;
        }
        if (args.Length == 5 && args[0] == "set-inactive-relay" && args[1] == "--pin" && args[3] == "--state")
        {
            IRelayDriver? driver = null;
            try
            {
                if (!int.TryParse(args[2], out int pin) || pin <= 0)
                {
                    Console.Error.WriteLine($"Invalid pin number: {args[2]}. Must be a positive integer.");
                    Environment.Exit(1);
                    return;
                }

                string stateArg = args[4];
                bool energized;

                if (stateArg.Equals("High", StringComparison.OrdinalIgnoreCase))
                {
                    // "High" historically meant pin held High; with activeLow=true that is the de-energized state.
                    energized = false;
                }
                else if (stateArg.Equals("Low", StringComparison.OrdinalIgnoreCase))
                {
                    energized = true;
                }
                else
                {
                    Console.Error.WriteLine($"Invalid state: {stateArg}. Must be 'High' or 'Low'.");
                    Environment.Exit(1);
                    return;
                }

                var configPath = AppPaths.ServerConfigPath;
                AppSettings settings = File.Exists(configPath)
                    ? LoadInitialSettings(configPath)
                    : new AppSettings();
                driver = RelayDriverFactory.Create(settings, NullLogger<Program>.Instance);
                driver.RegisterChannel(new RelayConfig
                {
                    RelayPin = pin,
                    ActiveLow = true,
                    SourceName = "_inactive",
                    OutputName = "_inactive"
                });
                driver.SetRelay(pin, energized);
                Console.WriteLine($"Successfully set channel {pin} to {stateArg} (energized={energized}) via {driver.Name}");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error setting pin via command line: {ex.Message}");
                Environment.Exit(1);
            }
            finally
            {
                driver?.Dispose();
            }
            return; // Exit Main after handling command-line operation
        }

        // --- Web Server Startup Code ---
        try
        {
            RunWebServer(args);
        }
        catch (Exception ex)
        {
            LogException("Server startup crash", ex);
            throw;
        }
    }

    private static void RunWebServer(string[] args)
    {
        var configPath = AppPaths.ServerConfigPath;
        var initialSettings = LoadInitialSettings(configPath);

        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseWindowsService(options => options.ServiceName = "RemoteRelay Server");
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<TcpMessageService>();
        builder.Services.AddSingleton<SwitcherState>(sp =>
        {
            var hubContext = sp.GetRequiredService<IHubContext<RelayHub>>();
            var logger = sp.GetRequiredService<ILogger<SwitcherState>>();
            var tcpMessageService = sp.GetRequiredService<TcpMessageService>();
            return new SwitcherState(initialSettings, hubContext, logger, tcpMessageService);
        });
        builder.Services.AddSingleton(sp =>
        {
            var switcherState = sp.GetRequiredService<SwitcherState>();
            var logger = sp.GetRequiredService<ILogger<ConfigurationWatcher>>();
            return new ConfigurationWatcher(configPath, switcherState, logger);
        });
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigurationWatcher>());
        builder.Services.AddSingleton(new ConfigurationService(configPath));

        // Register UDP listener service if port is configured
        if (initialSettings.UdpApiPort.HasValue)
        {
            var udpPort = initialSettings.UdpApiPort.Value;
            builder.Services.AddHostedService(sp =>
            {
                var switcherState = sp.GetRequiredService<SwitcherState>();
                var hubContext = sp.GetRequiredService<IHubContext<RelayHub>>();
                var logger = sp.GetRequiredService<ILogger<UdpListenerService>>();
                return new UdpListenerService(switcherState, hubContext, logger, udpPort);
            });
        }

        // Register mDNS Beacon Service
        builder.Services.AddHostedService<MdnsBeaconService>();

        builder.WebHost.ConfigureKestrel(options => { options.ListenAnyIP(initialSettings.ServerPort); });

        var app = builder.Build();


        app.MapGet("/", () => "This is a SignalR Server for Remote Relay, the hub is hosted at /relay");
        app.MapHub<RelayHub>("/relay");

        // Health check endpoint for monitoring
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            version = VersionHelper.GetVersion(),
            uptime = (DateTime.UtcNow - StartTime).ToString(@"d\.hh\:mm\:ss"),
            timestamp = DateTime.UtcNow.ToString("o")
        }));

        // Configure application shutdown behavior
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var switcherStateInstance = app.Services.GetRequiredService<SwitcherState>();

        lifetime.ApplicationStopping.Register(() =>
        {
            Console.WriteLine("Application is stopping. Setting inactive relay to inactive state.");
            switcherStateInstance.SetInactiveRelayToInactiveState();
          // Assuming GpioController is disposed of when SwitcherState is disposed or app shuts down.
      });

        app.MapGet("/logo", async (SwitcherState switcherState, ILogger<Program> logger, IWebHostEnvironment env, CancellationToken cancellationToken) =>
        {
            var appSettings = switcherState.GetSettings();
            if (string.IsNullOrWhiteSpace(appSettings.LogoFile))
            {
                logger.LogWarning("Logo file path is not configured.");
                return Results.NotFound("Logo file path is not configured.");
            }

            var logoPath = appSettings.LogoFile;
            if (!Path.IsPathRooted(logoPath))
            {
                logoPath = Path.Combine(env.ContentRootPath, logoPath);
            }

            if (!File.Exists(logoPath))
            {
                logger.LogError($"Logo file not found at path: {logoPath}");
                return Results.NotFound($"Logo file not found at path: {logoPath}");
            }

            try
            {
                var (data, contentType) = await ImageCompressor.LoadAndCompressAsync(logoPath, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Successfully served compressed logo file: {LogoPath} (content-type: {ContentType})", logoPath, contentType);
                return Results.File(data, contentType);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error reading logo file: {logoPath}");
                return Results.Problem($"Error reading logo file: {ex.Message}");
            }
        });

        // HTTP API endpoint for external switching
        // Usage: GET /switch?input=<name|index>&output=<name|index>
        app.MapGet("/switch", async (HttpContext context, SwitcherState switcherState, IHubContext<RelayHub> hubContext, ILogger<Program> logger) =>
        {
            var inputParam = context.Request.Query["input"].ToString();
            var outputParam = context.Request.Query["output"].ToString();

            if (string.IsNullOrWhiteSpace(inputParam) || string.IsNullOrWhiteSpace(outputParam))
            {
                return Results.BadRequest(new { success = false, error = "Both 'input' and 'output' query parameters are required." });
            }

            var settings = switcherState.GetSettings();
            var sources = settings.Sources.ToList();
            var outputs = settings.Outputs.ToList();

          // Resolve input name (by index or name)
          string? inputName = null;
            if (int.TryParse(inputParam, out int inputIndex) && inputIndex >= 1 && inputIndex <= sources.Count)
            {
                inputName = sources[inputIndex - 1];
            }
            else
            {
                inputName = sources.FirstOrDefault(s => s.Equals(inputParam, StringComparison.OrdinalIgnoreCase));
            }

          // Resolve output name (by index or name)
          string? outputName = null;
            if (int.TryParse(outputParam, out int outputIndex) && outputIndex >= 1 && outputIndex <= outputs.Count)
            {
                outputName = outputs[outputIndex - 1];
            }
            else
            {
                outputName = outputs.FirstOrDefault(o => o.Equals(outputParam, StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrEmpty(inputName))
            {
                return Results.BadRequest(new { success = false, error = $"Input '{inputParam}' not found. Available: {string.Join(", ", sources)}" });
            }

            if (string.IsNullOrEmpty(outputName))
            {
                return Results.BadRequest(new { success = false, error = $"Output '{outputParam}' not found. Available: {string.Join(", ", outputs)}" });
            }

          // Verify route exists
          var routeExists = settings.Routes.Any(r =>
             string.Equals(r.SourceName, inputName, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(r.OutputName, outputName, StringComparison.OrdinalIgnoreCase));

            if (!routeExists)
            {
                return Results.BadRequest(new { success = false, error = $"Route '{inputName}' -> '{outputName}' does not exist." });
            }

          // Execute the switch
          switcherState.SwitchSource(inputName, outputName);
            logger.LogInformation("HTTP switch executed: {Input} -> {Output}", inputName, outputName);

          // Broadcast state update to all SignalR clients
          var state = switcherState.GetSystemState();
            await hubContext.Clients.All.SendAsync("SystemState", state);

            return Results.Ok(new { success = true, message = $"Switched {inputName} to {outputName}" });
        });

        app.Run();
    }

    private static AppSettings LoadInitialSettings(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"No configuration file found at '{configPath}'. Creating default (unconfigured) config.");
            var defaults = new AppSettings { ServerPort = 33101, ShowIpOnScreen = true, ShowClockOnScreen = true, FlashOnSelect = true, Logging = true };
            var defaultJson = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            File.WriteAllText(configPath, defaultJson);
        }

        var json = File.ReadAllText(configPath);
        json = MigrateConfigIfNeeded(configPath, json);

        var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (settings.Routes == null)
        {
            throw new InvalidOperationException("Unable to deserialize configuration file into AppSettings.");
        }

        if (!AppSettingsValidator.TryValidate(settings, out var summary, out var warnings))
        {
            throw new InvalidOperationException(summary);
        }

        foreach (var warning in warnings)
        {
            Console.WriteLine($"Configuration warning: {warning}");
        }

        return settings;
    }

    /// <summary>
    /// Runs ConfigMigrator over the raw config document and, if it changed,
    /// rewrites the file (keeping a .pre-migration backup) and returns the
    /// upgraded JSON. Runs before the ConfigurationWatcher starts, so the
    /// rewrite can't trigger a reload. Any failure here is non-fatal: the
    /// original text is returned and deserialization reports real problems.
    /// </summary>
    private static string MigrateConfigIfNeeded(string configPath, string json)
    {
        try
        {
            var documentOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            if (JsonNode.Parse(json, null, documentOptions) is not JsonObject configObject ||
                !ConfigMigrator.Migrate(configObject))
            {
                return json;
            }

            File.Copy(configPath, configPath + ".pre-migration", overwrite: true);
            var upgraded = configObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, upgraded);
            Console.WriteLine($"Configuration upgraded to schema v{AppSettings.CurrentConfigVersion} (previous file kept at {configPath}.pre-migration).");
            return upgraded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Configuration migration skipped: {ex.Message}");
            return json;
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException("Unhandled exception", ex);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("Unobserved task exception", e.Exception);
        e.SetObserved(); // Prevent crash from unobserved task exceptions
    }

    private static void LogException(string context, Exception ex)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logEntry = $"[{timestamp}] {context}: {ex}\n\n";
            File.AppendAllText(ErrorLogPath, logEntry);
            Console.Error.WriteLine($"[{timestamp}] {context}: {ex.Message}");
        }
        catch
        {
            // Don't throw from the exception handler
        }
    }
}