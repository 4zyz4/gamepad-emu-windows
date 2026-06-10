using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GamepadEmu.Protocol;
using Nefarius.ViGEm.Client;

namespace GamepadEmuHost.Core;

public class NetworkService : IDisposable
{
    private const int DefaultPort = 37284;
    private const string BroadcastPrefix = "GAMEPAD_SERVER:";

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new();
    private readonly ConcurrentDictionary<string, GamepadSession> _sessions = new();
    private readonly ViGEmClient _viGEm = new();
    private CancellationTokenSource? _discoveryCts;
    private Task? _discoveryTask;
    private bool _disposed;

    public IReadOnlyCollection<DiscoveredDevice> DiscoveredDevices =>
        _discovered.Values.Where(d => (DateTime.UtcNow - d.LastSeen).TotalSeconds < 15).ToList();

    public IReadOnlyCollection<GamepadSession> ActiveSessions => _sessions.Values.ToList();

    public event Action<DiscoveredDevice>? OnDeviceDiscovered;
    public event Action<GamepadSession>? OnSessionStarted;
    public event Action<GamepadSession>? OnSessionEnded;

    public void StartDiscovery()
    {
        if (_discoveryCts != null) return;
        _discoveryCts = new CancellationTokenSource();
        _discoveryTask = Task.Run(() => DiscoveryLoop(_discoveryCts.Token));
    }

    public void StopDiscovery()
    {
        if (_discoveryCts != null)
        {
            _discoveryCts.Cancel();
            _discoveryCts.Dispose();
            _discoveryCts = null;
        }
        try
        {
            _discoveryTask?.GetAwaiter().GetResult();
        }
        catch { }
        _discoveryTask = null;
    }

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient(DefaultPort);
                udp.EnableBroadcast = true;

                while (!ct.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(ct);
                    var msg = System.Text.Encoding.UTF8.GetString(result.Buffer);

                    if (msg.StartsWith(BroadcastPrefix))
                    {
                        var deviceName = msg[BroadcastPrefix.Length..];
                        var key = $"{result.RemoteEndPoint.Address}:{DefaultPort}";

                        var device = _discovered.GetOrAdd(key, _ => new DiscoveredDevice
                        {
                            DeviceName = deviceName,
                            IpAddress = result.RemoteEndPoint.Address,
                            Port = DefaultPort
                        });
                        device.LastSeen = DateTime.UtcNow;

                        OnDeviceDiscovered?.Invoke(device);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    public async Task<GamepadSession?> ConnectAsync(DiscoveredDevice device)
    {
        try
        {
            var tcp = new TcpClient();
            tcp.NoDelay = true;
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            await tcp.ConnectAsync(device.IpAddress, device.Port);

            var session = new GamepadSession(device, tcp, _viGEm);
            session.OnReady += s =>
            {
                OnSessionStarted?.Invoke(s);
            };
            session.OnDisconnected += s =>
            {
                _sessions.TryRemove(s.Device.Key, out _);
                OnSessionEnded?.Invoke(s);
            };

            session.Start();
            _sessions[device.Key] = session;

            return session;
        }
        catch
        {
            return null;
        }
    }

    public void DisconnectAll()
    {
        foreach (var session in _sessions.Values)
        {
            session.Disconnect();
        }
        _sessions.Clear();
    }

    public void Refresh()
    {
        StopDiscovery();

        foreach (var session in _sessions.Values.Where(s => !s.IsConnected).ToList())
        {
            session.Disconnect();
        }

        var connectedKeys = new HashSet<string>(_sessions.Keys);
        foreach (var key in _discovered.Keys.Where(k => !connectedKeys.Contains(k)).ToList())
        {
            _discovered.TryRemove(key, out _);
        }

        StartDiscovery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDiscovery();
        DisconnectAll();
        _viGEm.Dispose();
    }
}
