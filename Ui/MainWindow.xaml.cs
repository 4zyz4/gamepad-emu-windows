using System.Windows;
using System.Windows.Controls;
using GamepadEmu.Protocol;

namespace GamepadEmuHost.Ui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        DataContext = vm;
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    private async void OnManualConnectClick(object sender, RoutedEventArgs e)
    {
        await _vm.ConnectByIpAsync();
    }

    private async void OnCardConnectClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.ConnectAsync(card);
    }

    private async void OnCardDisconnectClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card != null)
            await _vm.DisconnectAsync(card);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _vm.Refresh();
    }

    private async void OnCardModeClick(object sender, RoutedEventArgs e)
    {
        var card = (sender as FrameworkElement)?.DataContext as DeviceCardViewModel;
        if (card == null) return;
        var mode = (sender as FrameworkElement)?.Tag as string == "DS4"
            ? ControllerMode.Ds4 : ControllerMode.Xbox360;
        await _vm.SetModeAsync(card, mode);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _vm.Shutdown(); }
        catch { }
        base.OnClosed(e);
        try { Application.Current.Shutdown(); }
        catch { }
        Environment.Exit(0);
    }
}
