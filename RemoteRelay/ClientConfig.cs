using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteRelay;

public class ClientConfig
{
    public string? Host { get; set; } = string.Empty;
    public int? Port { get; set; } = null;
    public List<string>? ShownInputs { get; set; }
    public List<string>? ShownOutputs { get; set; }
    public bool? ShowIpOnScreen { get; set; }
    public bool? IsFullscreen { get; set; } = true;

    /// <summary>
    /// Returns true if the host is localhost, 127.0.0.1, or matches
    /// any of this machine's own IPv4 addresses (covers auto-discovery).
    /// </summary>
    public bool IsLocalConnection
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host) || 
                Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                Host == "127.0.0.1")
                return true;

            try
            {
                var localAddresses = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                    .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ua => ua.Address.ToString());

                return localAddresses.Any(addr => addr.Equals(Host, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }
    }
}
