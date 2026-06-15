using System;
using System.IO;

namespace RemoteRelay.Common;

/// <summary>
/// Resolves where mutable application data (config files and logs) lives.
///
/// On Windows this is <c>%ProgramData%\RemoteRelay\{Server|Client}</c> so the data is
/// machine-wide, writable by the LocalSystem service and by an admin (or any user, once
/// the installer grants Modify on the folder), and survives reinstalls into Program Files.
///
/// On every other platform (the Raspberry Pi / Linux installs) the historical
/// install-directory location is kept unchanged: the systemd unit's WorkingDirectory, the
/// <c>remoterelay.sh</c> management TUI, and <c>install.sh</c>'s upgrade-time config
/// preservation all assume config lives next to the binary. Only Windows moves.
/// </summary>
public static class AppPaths
{
    public const string ServerComponent = "Server";
    public const string ClientComponent = "Client";

    public static string ServerDataDir => ResolveDataDir(ServerComponent);
    public static string ClientDataDir => ResolveDataDir(ClientComponent);

    public static string ServerConfigPath => Path.Combine(ServerDataDir, "config.json");
    public static string ClientConfigPath => Path.Combine(ClientDataDir, "ClientConfig.json");

    public static string ServerLogPath => Path.Combine(ServerDataDir, "server_error.log");
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
            dir = AppContext.BaseDirectory;
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
}
