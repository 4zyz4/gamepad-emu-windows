using System.Net.Sockets;
using GamepadEmu.Protocol;
using Google.Protobuf;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;

namespace GamepadEmuHost.Core;

public class GamepadSession : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ViGEmClient _viGEm;
    private readonly CancellationTokenSource _cts = new();
    private Task _readTask = Task.CompletedTask;
    private Task _vibTask = Task.CompletedTask;
    private Task _keepaliveTask = Task.CompletedTask;
    private bool _disposed;
    private bool _readyFired;
    private byte _lastLargeMotor;
    private byte _lastSmallMotor;
    private ulong _lastSeq;
    private DateTime _lastInputTime = DateTime.MinValue;
    private int _inputCountThisSecond;
    private int _measuredInputRate;


    public DiscoveredDevice Device { get; }
    public ControllerMode Mode { get; private set; }
    public object? Controller { get; private set; }
    public bool IsConnected => _tcp.Connected && !_disposed;
    public bool IsReady => Controller != null;

    public event Action<GamepadSession>? OnReady;
    public event Action<GamepadSession>? OnDisconnected;
    public event Action<GamepadSession>? OnConnectionLost;

    public bool IsReconnectRequested { get; private set; }

    public string ReconnectIp => Device.IpAddress.ToString();
    public int ReconnectPort => Device.Port;

    public GamepadSession(DiscoveredDevice device, TcpClient tcp, ViGEmClient viGEm)
    {
        Device = device;
        _tcp = tcp;
        _stream = tcp.GetStream();
        _viGEm = viGEm;
    }

    public void Start()
    {
        _readTask = Task.Run(() => ReadLoop(_cts.Token));
        _vibTask = Task.Run(() => VibrationSendLoop(_cts.Token));
        _keepaliveTask = Task.Run(() => KeepaliveLoop(_cts.Token));
    }

    public async Task SendAsync(IMessage message)
    {
        if (_disposed) return;
        try
        {
            var data = message.ToByteArray();
            var len = data.Length;
            var header = new byte[4]
            {
                (byte)((len >> 24) & 0xFF),
                (byte)((len >> 16) & 0xFF),
                (byte)((len >> 8) & 0xFF),
                (byte)(len & 0xFF),
            };
            await _stream.WriteAsync(header, _cts.Token);
            await _stream.WriteAsync(data, _cts.Token);
            await _stream.FlushAsync(_cts.Token);
        }
        catch
        {
            if (IsReconnectRequested) return;
            Disconnect();
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var lengthBuf = new byte[4];
        var buffer = new byte[65536];
        bool connectionAbnormal = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var headerRead = await ReadExactAsync(_stream, lengthBuf, 4, ct);
                if (headerRead < 4) { connectionAbnormal = true; break; }

                var msgLen = (lengthBuf[0] << 24) | (lengthBuf[1] << 16) |
                             (lengthBuf[2] << 8) | lengthBuf[3];
                if (msgLen < 0 || msgLen > buffer.Length) { connectionAbnormal = true; break; }

                var bodyRead = await ReadExactAsync(_stream, buffer, msgLen, ct);
                if (bodyRead < msgLen) { connectionAbnormal = true; break; }

                ProcessMessage(buffer.AsSpan(0, msgLen));
            }
        }
        catch (OperationCanceledException)
        {
            if (_disposed && IsReconnectRequested)
            {
                return;
            }
            connectionAbnormal = true;
        }
        catch
        {
            connectionAbnormal = true;
        }

        if (connectionAbnormal)
        {
            if (IsReconnectRequested || _disposed)
            {
                return;
            }
            RequestReconnect();
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer, offset, count - offset, ct);
            if (read <= 0) return offset;
            offset += read;
        }
        return offset;
    }

    private void ProcessMessage(ReadOnlySpan<byte> data)
    {
        try
        {
            var wrapper = ClientToServer.Parser.ParseFrom(data.ToArray());
            switch (wrapper.PayloadCase)
            {
                case ClientToServer.PayloadOneofCase.Hello:
                    HandleHello(wrapper.Hello);
                    break;
                case ClientToServer.PayloadOneofCase.GamepadInput:
                    if (IsReady) HandleGamepadInput(wrapper.GamepadInput);
                    break;
                case ClientToServer.PayloadOneofCase.KeepAlive:
                    break;
            }
        }
        catch
        {
            // parse error
        }
    }

    private void HandleHello(Hello hello)
    {
        var newMode = hello.ControllerMode;

        if (IsReady && Mode != newMode)
        {
            DestroyController();
        }

        Mode = newMode;

        if (Controller == null)
        {
            CreateController(newMode);
        }

        _ = SendAsync(new ServerToClient
        {
            ServerHello = new ServerHello
            {
                ProtocolVersion = 1,
                HostName = Environment.MachineName,
                MaxDownlinkRateHz = 1000,
                RecommendedUplinkIntervalUs = 0,
            }
        });

        if (!_readyFired)
        {
            _readyFired = true;
            OnReady?.Invoke(this);
        }
    }

    private void CreateController(ControllerMode mode)
    {
        switch (mode)
        {
            case ControllerMode.Ds4:
            {
                var ctrl = _viGEm.CreateDualShock4Controller();
                ctrl.FeedbackReceived += OnDs4Feedback;
                ctrl.Connect();
                Controller = ctrl;
                break;
            }
            default:
            {
                var ctrl = _viGEm.CreateXbox360Controller();
                ctrl.FeedbackReceived += OnXbox360Feedback;
                ctrl.Connect();
                ctrl.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                ctrl.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                ctrl.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                ctrl.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                ctrl.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                ctrl.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                Controller = ctrl;
                break;
            }
        }
    }

    private void OnDs4Feedback(object? sender, DualShock4FeedbackReceivedEventArgs e)
    {
        _lastLargeMotor = e.LargeMotor;
        _lastSmallMotor = e.SmallMotor;
        _ = SendVibrationAsync(e.LargeMotor, e.SmallMotor);
    }

    private void OnXbox360Feedback(object? sender, Xbox360FeedbackReceivedEventArgs e)
    {
        _lastLargeMotor = e.LargeMotor;
        _lastSmallMotor = e.SmallMotor;
        _ = SendVibrationAsync(e.LargeMotor, e.SmallMotor);
    }

    private void HandleGamepadInput(GamepadInput input)
    {
        if (Controller == null) return;

        _lastSeq = input.Seq;
        var now = DateTime.UtcNow;
        if ((now - _lastInputTime).TotalSeconds < 1)
            _inputCountThisSecond++;
        else
        {
            _measuredInputRate = _inputCountThisSecond;
            _inputCountThisSecond = 1;
        }
        _lastInputTime = now;

        switch (Mode)
        {
            case ControllerMode.Ds4:
                HandleDs4Input(input);
                break;
            default:
                HandleXbox360Input(input);
                break;
        }
    }

    private void HandleXbox360Input(GamepadInput input)
    {
        var ctrl = (IXbox360Controller)Controller!;

        ctrl.SetButtonState(Xbox360Button.A, (input.Buttons & (1 << 0)) != 0);
        ctrl.SetButtonState(Xbox360Button.B, (input.Buttons & (1 << 1)) != 0);
        ctrl.SetButtonState(Xbox360Button.X, (input.Buttons & (1 << 2)) != 0);
        ctrl.SetButtonState(Xbox360Button.Y, (input.Buttons & (1 << 3)) != 0);
        ctrl.SetButtonState(Xbox360Button.LeftShoulder, (input.Buttons & (1 << 4)) != 0);
        ctrl.SetButtonState(Xbox360Button.RightShoulder, (input.Buttons & (1 << 5)) != 0);
        ctrl.SetButtonState(Xbox360Button.LeftThumb, (input.Buttons & (1 << 10)) != 0);
        ctrl.SetButtonState(Xbox360Button.RightThumb, (input.Buttons & (1 << 11)) != 0);
        ctrl.SetButtonState(Xbox360Button.Back, (input.Buttons & (1 << 8)) != 0);
        ctrl.SetButtonState(Xbox360Button.Start, (input.Buttons & (1 << 9)) != 0);
        ctrl.SetButtonState(Xbox360Button.Guide, (input.Buttons & (1 << 16)) != 0);

        ctrl.SetButtonState(Xbox360Button.Up, (input.Dpad & 1) != 0);
        ctrl.SetButtonState(Xbox360Button.Down, (input.Dpad & 2) != 0);
        ctrl.SetButtonState(Xbox360Button.Left, (input.Dpad & 4) != 0);
        ctrl.SetButtonState(Xbox360Button.Right, (input.Dpad & 8) != 0);

        ctrl.SetAxisValue(Xbox360Axis.LeftThumbX, (short)input.LeftStickX);
        ctrl.SetAxisValue(Xbox360Axis.LeftThumbY, (short)-input.LeftStickY);
        ctrl.SetAxisValue(Xbox360Axis.RightThumbX, (short)input.RightStickX);
        ctrl.SetAxisValue(Xbox360Axis.RightThumbY, (short)-input.RightStickY);

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        ctrl.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)Math.Clamp(lt, 0, 255));
        ctrl.SetSliderValue(Xbox360Slider.RightTrigger, (byte)Math.Clamp(rt, 0, 255));
    }

    private ushort _ds4Timestamp;

    private void HandleDs4Input(GamepadInput input)
    {
        var ctrl = (IDualShock4Controller)Controller!;
        var buf = new byte[63]; // DS4_REPORT_EX (no Report ID byte)

        // [0-3]: Thumb sticks (0-255, center 128)
        buf[0] = MapAxisToByte(input.LeftStickX);
        buf[1] = MapAxisToByte(input.LeftStickY);
        buf[2] = MapAxisToByte(input.RightStickX);
        buf[3] = MapAxisToByte(input.RightStickY);

        // [4-5]: wButtons (LE) — dpad + face/shoulder/menu/stick-click
        ushort w = (input.Dpad & 0x0F) switch
        {
            1 => 0,    // N
            2 => 4,    // S
            4 => 6,    // W
            8 => 2,    // E
            5 => 7,    // NW
            6 => 5,    // SW
            9 => 1,    // NE
            10 => 3,   // SE
            _ => 8,    // neutral
        };
        if ((input.Buttons & (1 << 0)) != 0) w |= 1 << 5;   // Cross
        if ((input.Buttons & (1 << 1)) != 0) w |= 1 << 6;   // Circle
        if ((input.Buttons & (1 << 2)) != 0) w |= 1 << 4;   // Square
        if ((input.Buttons & (1 << 3)) != 0) w |= 1 << 7;   // Triangle
        if ((input.Buttons & (1 << 4)) != 0) w |= 1 << 8;   // L1
        if ((input.Buttons & (1 << 5)) != 0) w |= 1 << 9;   // R1
        if ((input.Buttons & (1 << 6)) != 0) w |= 1 << 10;  // L2
        if ((input.Buttons & (1 << 7)) != 0) w |= 1 << 11;  // R2
        if ((input.Buttons & (1 << 8)) != 0) w |= 1 << 12;  // Share
        if ((input.Buttons & (1 << 9)) != 0) w |= 1 << 13;  // Options
        if ((input.Buttons & (1 << 10)) != 0) w |= 1 << 14; // L3
        if ((input.Buttons & (1 << 11)) != 0) w |= 1 << 15; // R3
        buf[4] = (byte)w;
        buf[5] = (byte)(w >> 8);

        // [6]: bSpecial (PS=bit0, TouchpadClick=bit1)
        byte special = 0;
        if ((input.Buttons & (1 << 16)) != 0) special |= 1;
        if ((input.Buttons & (1 << 17)) != 0) special |= 2;
        buf[6] = special;

        // [7-8]: Analog triggers
        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        buf[7] = (byte)Math.Clamp(lt, 0, 255);
        buf[8] = (byte)Math.Clamp(rt, 0, 255);

        // [9-10]: Timestamp
        _ds4Timestamp++;
        buf[9] = (byte)_ds4Timestamp;
        buf[10] = (byte)(_ds4Timestamp >> 8);

        // [11]: Battery level (0-10)
        buf[11] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);

        // [12-17]: Gyroscope (rad/s → int16, scale ≈940 for ±2000°/s)
        WriteInt16LE(buf, 12, (short)Math.Clamp(input.GyroX * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 14, (short)Math.Clamp(input.GyroY * 940f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 16, (short)Math.Clamp(input.GyroZ * 940f, short.MinValue, short.MaxValue));

        // [18-23]: Accelerometer (m/s² → int16, scale ≈835 for ±2g)
        WriteInt16LE(buf, 18, (short)Math.Clamp(input.AccelX * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 20, (short)Math.Clamp(input.AccelY * 835f, short.MinValue, short.MaxValue));
        WriteInt16LE(buf, 22, (short)Math.Clamp(input.AccelZ * 835f, short.MinValue, short.MaxValue));

        // [24-28]: reserved (default 0xE9,0,0,0,0 from ViGEm)
        buf[24] = 0xE9;

        // [29]: Battery/cable state (bit4 = USB connected)
        buf[29] = 0x10;

        // [30-31]: reserved (default 0x1B,0x00 from ViGEm)
        buf[30] = 0x1B;

        // [32]: Touch packet count
        buf[32] = 1;

        // [33-41]: sCurrentTouch (1 DS4_TOUCH = 2 touch points)
        if (input.TouchpadTouch)
        {
            buf[33] = 0; // packet counter
            WriteTouchData(buf, 34, (int)input.TouchpadX, (int)input.TouchpadY, true, 0);
            WriteTouchData(buf, 38, 0, 0, false, 1);
        }
        else
        {
            buf[33] = 0; // packet counter
            WriteTouchData(buf, 34, 0, 0, false, 0);
            WriteTouchData(buf, 38, 0, 0, false, 1);
        }

        // [42-50]: sPreviousTouch[0] — all inactive
        buf[42] = 0; // packet counter
        WriteTouchData(buf, 43, 0, 0, false, 2);
        WriteTouchData(buf, 47, 0, 0, false, 3);

        // [51-59]: sPreviousTouch[1] — all inactive
        buf[51] = 0; // packet counter
        WriteTouchData(buf, 52, 0, 0, false, 4);
        WriteTouchData(buf, 56, 0, 0, false, 5);

        ctrl.SubmitRawReport(buf);
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteTouchData(byte[] buf, int offset, int x, int y, bool active, byte tracking)
    {
        // Status: bit7 = 1 if inactive, bits 6-0 = tracking number
        buf[offset] = active ? (byte)(tracking & 0x7F) : (byte)(0x80 | (tracking & 0x7F));
        // 3 bytes: 12-bit X + 12-bit Y packed
        int cx = Math.Clamp(x, 0, 1919);
        int cy = Math.Clamp(y, 0, 942);
        buf[offset + 1] = (byte)(cx & 0xFF);
        buf[offset + 2] = (byte)(((cx >> 8) & 0x0F) | ((cy & 0x0F) << 4));
        buf[offset + 3] = (byte)((cy >> 4) & 0xFF);
    }

    private static byte MapAxisToByte(int value)
    {
        return (byte)Math.Clamp((value + 32768) / 256, 0, 255);
    }

    private async Task SendVibrationAsync(byte large, byte small)
    {
        await SendAsync(new ServerToClient
        {
            Vibration = new Vibration
            {
                LargeMotor = large,
                SmallMotor = small
            }
        });
    }

    private async Task VibrationSendLoop(CancellationToken ct)
    {
        var intervalMs = 30;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(intervalMs, ct);

            if (_lastSeq > 0)
            {
                await SendVibrationAsync(_lastLargeMotor, _lastSmallMotor);
                await SendAsync(new ServerToClient
                {
                    RttReport = new RttReport
                    {
                        AckSeq = _lastSeq,
                        MeasuredUplinkRateHz = (uint)_measuredInputRate,
                        RecommendedUplinkIntervalUs = (uint)(intervalMs * 1000),
                    }
                });
            }
            else
            {
                await SendVibrationAsync(_lastLargeMotor, _lastSmallMotor);
            }

            if (_measuredInputRate > 0)
                intervalMs = Math.Clamp(1000 / _measuredInputRate, 8, 50);
            else
                intervalMs = 30;
        }
    }

    private async Task KeepaliveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(10000, ct);
            try
            {
                await SendKeepaliveAsync();
            }
            catch { }
        }
    }

    private async Task SendKeepaliveAsync()
    {
        if (_disposed) return;
        try
        {
            var data = ClientToServer.Parser.ParseFrom(Array.Empty<byte>());
            var wrapper = new ClientToServer { KeepAlive = new KeepAlive { Timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } };
            var buf = wrapper.ToByteArray();
            var len = buf.Length;
            var header = new byte[4]
            {
                (byte)((len >> 24) & 0xFF),
                (byte)((len >> 16) & 0xFF),
                (byte)((len >> 8) & 0xFF),
                (byte)(len & 0xFF),
            };
            await _stream.WriteAsync(header, _cts.Token);
            await _stream.WriteAsync(buf, _cts.Token);
            await _stream.FlushAsync(_cts.Token);
        }
        catch { }
    }

    public async Task SetControllerModeAsync(ControllerMode mode)
    {
        if (_disposed) return;

        Mode = mode;
        DestroyController();
        await Task.Delay(100);
        CreateController(mode);
        try
        {
            await SendAsync(new ServerToClient
            {
                SetControllerMode = new SetControllerMode { Mode = mode }
            });
        }
        catch { }
    }

    public void Disconnect()
    {
        if (_disposed) return;
        _disposed = true;
        IsReconnectRequested = false;
        _cts.Cancel();
        DestroyController();
        _stream.Close();
        _tcp.Close();
        OnDisconnected?.Invoke(this);
    }

    public void RequestReconnect()
    {
        IsReconnectRequested = true;
        _disposed = true;
        _cts.Cancel();
        DestroyController();
        try { _stream.Close(); } catch { }
        try { _tcp.Close(); } catch { }
        OnConnectionLost?.Invoke(this);
    }

    public void RequestCancelReconnect()
    {
        IsReconnectRequested = false;
    }

    private void DestroyController()
    {
        try
        {
            switch (Controller)
            {
                case IXbox360Controller xbox:
                    xbox.Disconnect();
                    break;
                case IDualShock4Controller ds4:
                    ds4.Disconnect();
                    break;
            }
        }
        catch { }
        Controller = null;
    }

    public void Dispose()
    {
        Disconnect();
        _cts.Dispose();
    }
}
