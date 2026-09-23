using System;
using System.IO;

namespace RemoteRelay.Common;

/// <summary>
/// Resolves where mutable application data (config files and logs) lives.
///
/// On Windows:
///   <c>%ProgramData%\RemoteRelay\{Server|Client}</c>
///
/// On Linux:
///   Server: <c>/etc/remoterelay</c> (falls back to AppContext.BaseDirectory in development/portable mode)
///   Client: <c>~/.config/RemoteRelay</c> (falls back to AppContext.BaseDirectory in development/portable mode)
/// </summary>
public static class AppPaths
{
    public const string ServerComponent = "Server";
    public const string ClientComponent = "Client";

    public static string ServerDataDir => ResolveDataDir(ServerComponent);
    public static string ClientDataDir => ResolveDataDir(ClientComponent);

    public static string ServerConfigPath => Path.Combine(ServerDataDir, "config.json");
    public static string ClientConfigPath => Path.Combine(ClientDataDir, "ClientConfig.json");

    public static string ServerLogPath => ResolveServerLogPath();
    public static string ClientLogPath => Path.Combine(ClientDataDir, "client_error.log");

    /// <summary>Resolves the data directory for a component, creating it if necessary.</summary>
    public static string ResolveDataDir(string component)
    {
        string dir;
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            dir = Path.Combine(programData, "RemoteRelay", component);
        }
        else
        {
            if (component == ServerComponent)
            {
                // In production on Linux, server configuration lives in /etc/remoterelay.
                // If /etc/remoterelay exists or /etc/remoterelay/config.json exists, use it.
                // Otherwise fall back to AppContext.BaseDirectory (for development or portable mode).
                const string etcDir = "/etc/remoterelay";
                if (Directory.Exists(etcDir) || File.Exists(Path.Combine(etcDir, "config.json")))
                {
                    dir = etcDir;
                }
                else if (File.Exists(Path.Combine(AppContext.BaseDirectory, "config.json")))
                {
                    dir = AppContext.BaseDirectory;
                }
                else
                {
                    // Attempt to create /etc/remoterelay if running with root privileges, else fallback to BaseDirectory
                    try
                    {
                        Directory.CreateDirectory(etcDir);
                        dir = etcDir;
                    }
                    catch
                    {
                        dir = AppContext.BaseDirectory;
                    }
                }
            }
            else
            {
                // Client on Linux: ~/.config/RemoteRelay (XDG_CONFIG_HOME)
                var userConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteRelay");
                var userConfigFile = Path.Combine(userConfig, "ClientConfig.json");

                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ClientConfig.json")) && !File.Exists(userConfigFile))
                {
                    dir = AppContext.BaseDirectory;
                }
                else
                {
                    dir = userConfig;

                    // Automatic migration from legacy location (~/RemoteRelay/client/ClientConfig.json or ServerDetails.json)
                    try
                    {
                        if (!File.Exists(userConfigFile))
                        {
                            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                            var legacyClientDir = Path.Combine(userProfile, "RemoteRelay", "client");
                            var legacyConfig = Path.Combine(legacyClientDir, "ClientConfig.json");
                            var legacyServerDetails = Path.Combine(legacyClientDir, "ServerDetails.json");

                            if (File.Exists(legacyConfig))
                            {
                                Directory.CreateDirectory(userConfig);
                                File.Copy(legacyConfig, userConfigFile, overwrite: false);
                            }
                            else if (File.Exists(legacyServerDetails))
                            {
                                Directory.CreateDirectory(userConfig);
                                File.Copy(legacyServerDetails, userConfigFile, overwrite: false);
                            }
                        }
                    }
                    catch
                    {
                        // Best-effort migration; ignore if unavailable
                    }
                }
            }
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // If the directory can't be created (e.g. a permission edge case), callers will
            // surface the real error when they try to read/write; don't mask it here.
        }

        return dir;
    }

    private static string ResolveServerLogPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(ServerDataDir, "server_error.log");
        }

        // On Linux, prefer /var/log/remoterelay if the directory exists
        const string logDir = "/var/log/remoterelay";
        try
        {
            if (Directory.Exists(logDir))
            {
                return Path.Combine(logDir, "server_error.log");
            }
        }
        catch
        {
        }

        return Path.Combine(ServerDataDir, "server_error.log");
    }
}
