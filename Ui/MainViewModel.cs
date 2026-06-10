using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using GamepadEmu.Protocol;
using GamepadEmuHost.Core;

namespace GamepadEmuHost.Ui;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly NetworkService? _network;
    private string _statusText = "就绪";
    private bool _driverOk;
    private string _manualIp = "";
    private readonly Dictionary<string, GamepadSession> _sessionMap = new();

    public ObservableCollection<DeviceCardViewModel> DeviceCards { get; } = new();
    public ObservableCollection<GamepadSession> Sessions { get; } = new();

    public bool HasActiveSession => Sessions.Count > 0;

    public string ManualIp
    {
        get => _manualIp;
        set { _manualIp = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public MainViewModel(NetworkService? network)
    {
        _network = network;
        if (_network == null)
        {
            _driverOk = false;
            StatusText = "viGEm 驱动错误: 无法创建服务";
            return;
        }

        _driverOk = true;
        _network.OnDeviceDiscovered += OnDeviceDiscovered;
        _network.OnSessionStarted += OnSessionStarted;
        _network.OnSessionEnded += OnSessionEnded;
    }

    public Task<bool> InitializeAsync()
    {
        if (_driverOk)
            StartAutoScan();
        return Task.FromResult(_driverOk);
    }

    public void StartAutoScan()
    {
        if (!_driverOk || _network == null) return;
        StatusText = "正在扫描...";
        _network.StartDiscovery();
    }

    public async Task ConnectAsync(DeviceCardViewModel card)
    {
        if (_network == null) return;
        var device = card.Device;
        StatusText = $"正在连接到 {device.IpString}...";
        var session = await _network.ConnectAsync(device);
        if (session != null)
        {
            _sessionMap[device.IpString] = session;
            card.IsConnected = true;
            StatusText = $"已连接: {device.IpString}";
        }
        else
        {
            StatusText = $"连接失败: {device.IpString}";
        }
    }

    public Task DisconnectAsync(DeviceCardViewModel card)
    {
        var ip = card.IpAddress;
        if (_sessionMap.TryGetValue(ip, out var session))
        {
            session.Disconnect();
        }
        return Task.CompletedTask;
    }

    public async Task SetModeAsync(DeviceCardViewModel card, ControllerMode mode)
    {
        var ip = card.IpAddress;
        if (_sessionMap.TryGetValue(ip, out var session))
        {
            await session.SetControllerModeAsync(mode);
            card.Mode = mode;
        }
    }

    public void Refresh()
    {
        if (_network == null) return;
        StatusText = "正在刷新...";
        _network.Refresh();

        DeviceCards.Clear();
        Sessions.Clear();
        _sessionMap.Clear();

        foreach (var session in _network.ActiveSessions)
        {
            Sessions.Add(session);
            _sessionMap[session.Device.IpString] = session;
            DeviceCards.Add(new DeviceCardViewModel(session.Device));
        }

        foreach (var device in _network.DiscoveredDevices)
        {
            if (!_sessionMap.ContainsKey(device.IpString))
            {
                DeviceCards.Add(new DeviceCardViewModel(device));
            }
        }

        OnPropertyChanged(nameof(HasActiveSession));
        StatusText = "就绪";
    }

    public async Task ConnectByIpAsync()
    {
        var ip = ManualIp?.Trim();
        if (string.IsNullOrEmpty(ip) || _network == null)
        {
            StatusText = "请输入有效的 IP 地址";
            return;
        }

        if (!IPAddress.TryParse(ip, out var addr))
        {
            StatusText = "IP 地址格式无效";
            return;
        }

        if (_sessionMap.ContainsKey(ip))
        {
            StatusText = $"已连接到 {ip}";
            return;
        }

        var device = new DiscoveredDevice
        {
            DeviceName = ip,
            IpAddress = addr,
        };

        var existing = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
        DeviceCardViewModel card;
        if (existing != null)
        {
            card = existing;
        }
        else
        {
            card = new DeviceCardViewModel(device);
            DeviceCards.Add(card);
        }

        StatusText = $"正在连接到 {ip}...";
        var session = await _network.ConnectAsync(device);
        if (session != null)
        {
            _sessionMap[ip] = session;
            card.IsConnected = true;
            StatusText = $"已连接: {ip}";
        }
        else
        {
            StatusText = $"连接失败: {ip}";
        }
    }

    private void OnDeviceDiscovered(DiscoveredDevice device)
    {
        UiInvoke(() =>
        {
            var ip = device.IpString;
            if (!DeviceCards.Any(c => c.IpAddress == ip))
            {
                DeviceCards.Add(new DeviceCardViewModel(device));
                StatusText = $"发现设备: {device.IpString}";
            }
        });
    }

    private void OnSessionStarted(GamepadSession session)
    {
        UiInvoke(() =>
        {
            Sessions.Add(session);
            OnPropertyChanged(nameof(HasActiveSession));
            var card = DeviceCards.FirstOrDefault(c => c.IpAddress == session.Device.IpString);
            if (card != null)
                card.Mode = session.Mode;
        });
    }

    private void OnSessionEnded(GamepadSession session)
    {
        UiInvoke(() =>
        {
            Sessions.Remove(session);
            OnPropertyChanged(nameof(HasActiveSession));
            var ip = session.Device.IpString;
            _sessionMap.Remove(ip);
            var card = DeviceCards.FirstOrDefault(c => c.IpAddress == ip);
            if (card != null)
            {
                card.IsConnected = false;
                card.Mode = ControllerMode.Ds4;
            }
            StatusText = $"已断开: {ip}";
        });
    }

    private static void UiInvoke(Action action)
    {
        if (Application.Current?.Dispatcher.CheckAccess() == true)
            action();
        else
            Application.Current?.Dispatcher.Invoke(action);
    }

    public void Shutdown()
    {
        if (_network == null) return;
        _network.OnDeviceDiscovered -= OnDeviceDiscovered;
        _network.OnSessionStarted -= OnSessionStarted;
        _network.OnSessionEnded -= OnSessionEnded;
        _network.StopDiscovery();
        _network.DisconnectAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
