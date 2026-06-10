using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamepadEmu.Protocol;
using GamepadEmuHost.Core;

namespace GamepadEmuHost.Ui;

public class DeviceCardViewModel : INotifyPropertyChanged
{
    private readonly DiscoveredDevice _device;
    private bool _isConnected;
    private ControllerMode _mode = ControllerMode.Ds4;

    public string IpAddress => _device.IpString;
    public string DeviceName => _device.DeviceName;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (_isConnected == value) return;
            _isConnected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowConnect));
            OnPropertyChanged(nameof(ShowControls));
        }
    }

    public bool ShowConnect => !IsConnected;
    public bool ShowControls => IsConnected;

    public ControllerMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsXbox360Active));
            OnPropertyChanged(nameof(IsDs4Active));
        }
    }

    public bool IsXbox360Active => _mode == ControllerMode.Xbox360;
    public bool IsDs4Active => _mode == ControllerMode.Ds4;

    internal DiscoveredDevice Device => _device;

    public DeviceCardViewModel(DiscoveredDevice device)
    {
        _device = device;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
