using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamepadEmu.Protocol;
using GamepadEmuHost.Core;

namespace GamepadEmuHost.Ui;

public class DeviceCardViewModel : INotifyPropertyChanged
{
    private readonly DiscoveredDevice _device;
    private bool _isConnected;
    private ControllerMode _mode = ControllerMode.Xbox360;

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
        }
    }

    public List<ControllerMode> ControllerModes { get; } = [ControllerMode.Xbox360, ControllerMode.Ds4];

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
