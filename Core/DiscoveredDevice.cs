using System.Net;

namespace GamepadEmuHost.Core;

public class DiscoveredDevice
{
    public required string DeviceName { get; init; }
    public required IPAddress IpAddress { get; init; }
    public string IpString => IpAddress.ToString();
    public int Port { get; init; } = 37284;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string Key => $"{IpAddress}:{Port}";
}
