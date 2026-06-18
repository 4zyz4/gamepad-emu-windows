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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reconnectCts = new();
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
    public event Action<string>? OnSessionConnectionLost;

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
                _reconnectCts.TryRemove(s.Device.Key, out var oldCts);
                oldCts?.Cancel();
                oldCts?.Dispose();
                OnSessionStarted?.Invoke(s);
            };
            session.OnDisconnected += s =>
            {
                _sessions.TryRemove(s.Device.Key, out _);
                _reconnectCts.TryRemove(s.Device.Key, out var cts);
                cts?.Cancel();
                cts?.Dispose();
                OnSessionEnded?.Invoke(s);
            };
            session.OnConnectionLost += s =>
            {
                Task.Run(() => ReconnectAsync(s.Device, s.Device.Key));
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

    public void CancelReconnect(string deviceKey)
    {
        if (_reconnectCts.TryRemove(deviceKey, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task ReconnectAsync(DiscoveredDevice device, string key)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts[key] = cts;

        // Notify UI that connection is lost and reconnecting
        UiInvoke(() => OnSessionConnectionLost?.Invoke(device.IpString));

        while (!cts.Token.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(3000, cts.Token);

                if (cts.IsCancellationRequested || _disposed) break;

                var tcp = new TcpClient();
                tcp.NoDelay = true;
                tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                await tcp.ConnectAsync(device.IpAddress, device.Port);

                if (cts.IsCancellationRequested || _disposed)
                {
                    tcp.Close();
                    return;
                }

                // Old session may have been closed by RequestReconnect(),
                // but it's still in _sessions. We replace it.
                var oldSession = _sessions.GetValueOrDefault(key);
                if (oldSession != null)
                {
                    oldSession.RequestCancelReconnect();
                    oldSession.Dispose();
                }

                var newSession = new GamepadSession(device, tcp, _viGEm);
                newSession.OnReady += s =>
                {
                    OnSessionStarted?.Invoke(s);
                };
                newSession.OnDisconnected += s =>
                {
                    _sessions.TryRemove(s.Device.Key, out _);
                    _reconnectCts.TryRemove(s.Device.Key, out var rCts);
                    rCts?.Cancel();
                    rCts?.Dispose();
                    OnSessionEnded?.Invoke(s);
                };
                newSession.OnConnectionLost += s =>
                {
                    Task.Run(() => ReconnectAsync(device, s.Device.Key));
                };

                newSession.Start();
                _sessions[key] = newSession;

                return;
            }
            catch
            {
                if (cts.IsCancellationRequested || _disposed) return;
            }
        }
    }

    private static void UiInvoke(Action action)
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                action();
            else
                System.Windows.Application.Current?.Dispatcher.Invoke(action);
        }
        catch { }
    }

    public void DisconnectAll()
    {
        foreach (var kvp in _reconnectCts)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _reconnectCts.Clear();

        foreach (var session in _sessions.Values)
        {
            session.Disconnect();
        }
        _sessions.Clear();
    }

    public void Refresh()
    {
        foreach (var kvp in _reconnectCts)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _reconnectCts.Clear();

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
