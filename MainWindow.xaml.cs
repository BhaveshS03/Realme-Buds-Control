using System.IO.Ports;
using System.Net.Sockets;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace RealmeBuds;

public sealed record DeviceItem(string Name, ulong Address, bool IsConnected)
{
    public string Label => $"{Name}  ({MainWindow.FormatMac(Address)}){(IsConnected ? "  · connected" : "")}";
}

public sealed record FuncItem(string Name, int Value);

public sealed partial class MainWindow : Window
{
    private const int TimeoutMs = 3000;
    private const string AutoLabel = "Auto (earbuds' COM port)";
    private const string SocketModeLabel = "RFCOMM socket (no COM port)";

    private static readonly FuncItem[] FuncItems =
        new[] { new FuncItem("— leave unchanged —", -1) }
        .Concat(Proto.Funcs.Select(kv => new FuncItem(kv.Value, kv.Key)))
        .ToArray();

    private readonly RealmeClient _client = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };          // battery / liveness
    private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(5) };  // keeps trying to connect
    private readonly Dictionary<(int dev, int act), ComboBox> _gestureBoxes = new();
    private readonly (TextBlock pct, ProgressBar bar, TextBlock chg)[] _bat = new (TextBlock, ProgressBar, TextBlock)[4];

    private bool _busy, _suppress, _userDisconnected, _flyoutOpen;
    private int _failCount, _ticksSinceAttempt, _pollFailures;
    private string _connectedKey = "", _keyAtOpen = "";

    public MainWindow()
    {
        InitializeComponent();
        Title = "Realme Buds";
        SystemBackdrop = new MicaBackdrop();

        // Windows-style title bar: content extends into it, Mica shows through, no white bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        _bat[1] = (LeftPct, LeftBar, LeftChg);
        _bat[2] = (RightPct, RightBar, RightChg);
        _bat[3] = (CasePct, CaseBar, CaseChg);

        BuildGestureGrid();

        LenModeBox.SelectedIndex = Math.Clamp(_settings.LenMode, 0, 2);
        ChannelBox.Value = _settings.Channel;
        LogSwitch.IsOn = _settings.EnableLog;
        ApplyLogTab();

        _client.Log += msg => DispatcherQueue.TryEnqueue(() => AppendLog(msg));

        // Battery poll doubles as a liveness check: two silent polls in a row mean the link is gone.
        _timer.Tick += async (_, _) =>
        {
            if (!_client.Connected || _busy) return;
            bool ok = false;
            await RunAsync(async () => ok = await RefreshBatteryCoreAsync(), background: true);
            if (ok) _pollFailures = 0;
            else if (_client.Connected && ++_pollFailures >= 2)
            {
                _client.Disconnect();
                _pollFailures = 0;
                SetStatus("Connection lost. Reconnecting…", InfoBarSeverity.Warning);
                UpdateUi();
            }
        };

        // Always try to (re)connect unless the user disconnected on purpose.
        _reconnectTimer.Tick += async (_, _) =>
        {
            if (_busy || _flyoutOpen) return;

            if (_client.Connected)
            {
                if (CurrentKey() != _connectedKey)   // settings changed while an attempt was in flight
                {
                    _client.Disconnect();
                    UpdateUi();
                }
                return;
            }

            if (_userDisconnected) return;
            // back off gradually: 5 s, 10 s ... up to 30 s between silent attempts
            if (++_ticksSinceAttempt < Math.Min(1 + _failCount, 6)) return;
            _ticksSinceAttempt = 0;
            await AttemptConnectAsync(userInitiated: false);
        };

        Closed += (_, _) =>
        {
            _timer.Stop();
            _reconnectTimer.Stop();
            _client.Dispose();
        };

        LoadComPorts();
        UpdateUi();
        AutoRefresh.IsOn = true;      // starts the 30 s battery / liveness timer
        _reconnectTimer.Start();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        await LoadDevicesAsync();
        await AttemptConnectAsync(userInitiated: false);
    }

    // ------------------------------------------------------------ helpers --
    public static string FormatMac(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((a >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    private string CurrentKey() =>
        $"{(DeviceBox.SelectedItem as DeviceItem)?.Address}|{PortBox.SelectedItem}|{ChannelBox.Value}|{LenModeBox.SelectedIndex}";

    private void SaveSettings()
    {
        _settings.DeviceAddress = (DeviceBox.SelectedItem as DeviceItem)?.Address ?? _settings.DeviceAddress;
        _settings.Connection = PortBox.SelectedItem as string ?? "";
        _settings.Channel = double.IsNaN(ChannelBox.Value) ? 0 : (int)ChannelBox.Value;
        _settings.LenMode = Math.Max(0, LenModeBox.SelectedIndex);
        _settings.EnableLog = LogSwitch.IsOn;
        _settings.Save();
    }

    private void SetStatus(string msg, InfoBarSeverity sev = InfoBarSeverity.Informational)
    {
        StatusBar.Message = msg;
        StatusBar.Severity = sev;
    }

    /// <summary>The Log tab only exists while logging is enabled in the connection settings.</summary>
    private void ApplyLogTab()
    {
        bool present = Tabs.Items.Contains(LogTab);
        if (_settings.EnableLog && !present) Tabs.Items.Add(LogTab);
        else if (!_settings.EnableLog && present) Tabs.Items.Remove(LogTab);
    }

    private void LogSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.EnableLog = LogSwitch.IsOn;
        ApplyLogTab();
        _settings.Save();
    }

    private void AppendLog(string msg)
    {
        if (!_settings.EnableLog) return;
        LogBox.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\r\n";
        if (LogBox.Text.Length > 60000) LogBox.Text = LogBox.Text[^40000..];
        LogBox.SelectionStart = LogBox.Text.Length;
    }

    private void UpdateUi()
    {
        bool connected = _client.Connected;
        Busy.IsActive = _busy;
        ConnectBtn.Content = connected ? "Disconnect" : "Connect";
        ConnectBtn.IsEnabled = !_busy;
        RefreshDevicesBtn.IsEnabled = !_busy;
        Tabs.IsEnabled = connected && !_busy;
        DeviceTitle.Text = connected && DeviceBox.SelectedItem is DeviceItem d ? d.Name : "Realme Buds";

        if (connected)
        {
            ConnText.Text = $"Connected · {_client.ComPort ?? "RFCOMM"}";
            ConnDot.Fill = new SolidColorBrush(Colors.LimeGreen);
        }
        else if (_busy)
        {
            ConnText.Text = "Connecting…";
            ConnDot.Fill = new SolidColorBrush(Colors.Orange);
        }
        else
        {
            ConnText.Text = _userDisconnected ? "Disconnected" : "Waiting for earbuds…";
            ConnDot.Fill = new SolidColorBrush(Colors.Gray);
        }
    }

    private async Task RunAsync(Func<Task> op, bool background = false)
    {
        if (_busy) return;
        _busy = true;
        if (!background) UpdateUi();      // background polls shouldn't flicker the UI
        try
        {
            await op();
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _client.Disconnect();
            SetStatus($"Connection lost: {ex.Message}. Reconnecting…", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            UpdateUi();
        }
    }

    // ------------------------------------------------------------ devices --
    private void LoadComPorts()
    {
        string? previous = PortBox.SelectedItem as string ?? _settings.Connection;
        var items = new List<string> { AutoLabel, SocketModeLabel };
        items.AddRange(SerialPort.GetPortNames().OrderBy(p => p.Length).ThenBy(p => p));
        PortBox.ItemsSource = items;
        PortBox.SelectedItem = !string.IsNullOrEmpty(previous) && items.Contains(previous) ? previous : items[0];
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            ulong keep = (DeviceBox.SelectedItem as DeviceItem)?.Address ?? _settings.DeviceAddress;
            var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            var items = new List<DeviceItem>();
            foreach (var info in infos)
            {
                using var dev = await BluetoothDevice.FromIdAsync(info.Id);
                if (dev == null) continue;
                items.Add(new DeviceItem(dev.Name, dev.BluetoothAddress,
                                         dev.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
            items = items.OrderByDescending(i => i.IsConnected).ThenBy(i => i.Name).ToList();
            DeviceBox.ItemsSource = items;
            DeviceBox.SelectedItem =
                (keep != 0 ? items.FirstOrDefault(i => i.Address == keep) : null)
                ?? items.FirstOrDefault(i => i.Name.Contains("realme", StringComparison.OrdinalIgnoreCase) ||
                                             i.Name.Contains("buds", StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault();

            if (items.Count == 0)
                SetStatus("No paired devices found. Pair your earbuds in Windows settings first.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not list devices: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        LoadComPorts();
        await LoadDevicesAsync();
        SetStatus("Device list updated.");
    }

    // ---------------------------------------------------------- connection --
    private void SettingsFlyout_Opened(object? sender, object e)
    {
        _flyoutOpen = true;
        _keyAtOpen = CurrentKey();
    }

    private async void SettingsFlyout_Closed(object? sender, object e)
    {
        _flyoutOpen = false;
        SaveSettings();

        if (CurrentKey() != _keyAtOpen)          // user changed something: reconnect with the new settings
        {
            _userDisconnected = false;
            if (_client.Connected)
            {
                _client.Disconnect();
                UpdateUi();
            }
        }

        if (!_client.Connected && !_userDisconnected && !_busy)
        {
            _failCount = 0;
            _ticksSinceAttempt = 0;
            await AttemptConnectAsync(userInitiated: false);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_client.Connected)
        {
            _userDisconnected = true;           // stay disconnected until the user connects again
            _client.Disconnect();
            SetStatus("Disconnected.");
            UpdateUi();
            return;
        }

        _userDisconnected = false;
        _failCount = 0;
        var attempt = AttemptConnectAsync(userInitiated: true);
        SettingsFlyout.Hide();
        await attempt;
    }

    private async Task AttemptConnectAsync(bool userInitiated)
    {
        if (_busy || _client.Connected) return;

        string? choice = PortBox.SelectedItem as string;
        bool auto = choice == AutoLabel, socket = choice == SocketModeLabel;
        string? com = auto || socket ? null : choice;
        DeviceItem? dev = DeviceBox.SelectedItem as DeviceItem;

        if ((auto || socket) && dev == null)
        {
            if (userInitiated)
                SetStatus("Select your earbuds in the connection settings first.", InfoBarSeverity.Warning);
            return;
        }

        _client.LenMode = Math.Max(0, LenModeBox.SelectedIndex);
        int channel = double.IsNaN(ChannelBox.Value) ? 0 : (int)ChannelBox.Value;
        string key = CurrentKey();
        SaveSettings();

        await RunAsync(async () =>
        {
            if (auto)
            {
                SetStatus("Looking for the earbuds' COM port…");
                com = await ComPortFinder.FindAsync(dev!.Address);
                if (com == null)
                    AppendLog("No COM port found for this device; using an RFCOMM socket instead.");
            }

            SetStatus("Connecting…");
            bool ok = await _client.ConnectAsync(dev?.Address ?? 0, channel, com, userInitiated ? 8000 : 4000,
                                                 new Progress<string>(m => SetStatus(m)), CancellationToken.None);
            if (!ok)
            {
                _failCount++;
                if (userInitiated)
                    SetStatus(com != null
                        ? $"Could not open {com}. Make sure the earbuds are connected to this PC."
                        : "Could not connect. Make sure the earbuds are connected to this PC. If you have no COM port for them, " +
                          "add an Outgoing one in Windows > More Bluetooth settings > COM Ports.",
                        InfoBarSeverity.Error);
                else
                    SetStatus("Earbuds not reachable. Retrying automatically…");
                return;
            }

            _failCount = 0;
            _pollFailures = 0;
            _connectedKey = key;
            SetStatus(com != null ? $"Connected via {com}." : "Connected via RFCOMM socket.", InfoBarSeverity.Success);
            await RefreshBatteryCoreAsync();
            await ReadStateCoreAsync(quiet: true);
        });
    }

    // ------------------------------------------------------------ battery --
    private async Task<bool> RefreshBatteryCoreAsync()
    {
        var list = await _client.GetBatteryAsync(TimeoutMs);
        if (list == null)
        {
            SetStatus("No battery response from the earbuds.", InfoBarSeverity.Warning);
            return false;
        }
        foreach (var e in list)
        {
            if (e.Device < 1 || e.Device > 3) continue;
            var (pct, bar, chg) = _bat[e.Device];
            if (e.Percent is int p)
            {
                pct.Text = $"{p}%";
                bar.Value = p;
                chg.Text = e.Charging ? "⚡ Charging" : "";
            }
            else
            {
                pct.Text = "—";
                bar.Value = 0;
                chg.Text = "Disconnected";
            }
        }
        SetStatus($"Battery updated {DateTime.Now:HH:mm:ss}.", InfoBarSeverity.Success);
        return true;
    }

    private async void RefreshBattery_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => RefreshBatteryCoreAsync());

    private void AutoRefresh_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsOn) _timer.Start();
        else _timer.Stop();
    }

    // ------------------------------------------------------ noise control --
    private static string ModeName(int mode) => Proto.AncModes.GetValueOrDefault(mode, $"unknown ({mode})");

    private static void SetSwitch(ToggleSwitch ts, bool? value)
    {
        if (value is bool b) ts.IsOn = b;
    }

    /// <summary>Reads the active noise mode and the live feature switches and reflects them in the UI.</summary>
    private async Task ReadStateCoreAsync(bool quiet = false)
    {
        int? mode = await _client.GetNoiseModeAsync(TimeoutMs);
        var feats = await _client.QueryFeaturesAsync(new[] { Proto.FeatWind, Proto.FeatGame }, TimeoutMs);

        _suppress = true;
        try
        {
            if (mode is int m)
            {
                AncStatusText.Text = $"Current mode: {ModeName(m)}";
                foreach (var item in AncModes.Items.OfType<RadioButton>())
                    if (item.Tag is string t && int.Parse(t) == m) AncModes.SelectedItem = item;
            }
            else AncStatusText.Text = "Current mode: unavailable";

            SetSwitch(WindSwitch, feats[Proto.FeatWind]);
            SetSwitch(GameSwitch, feats[Proto.FeatGame]);
        }
        finally { _suppress = false; }

        if (!quiet)
            SetStatus(mode is int mm ? $"Noise control: {ModeName(mm)}." : "No response to the noise-control query.",
                      mode is null ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private async void ReadState_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ReadStateCoreAsync());

    private async void AncModes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || !_client.Connected) return;
        if (AncModes.SelectedItem is not RadioButton rb || rb.Tag is not string tag) return;
        int mode = int.Parse(tag);

        await RunAsync(async () =>
        {
            int? current = await _client.GetNoiseModeAsync(TimeoutMs);
            if (current == mode)
            {
                SetStatus($"Already in {ModeName(mode)}.", InfoBarSeverity.Success);
                return;
            }

            var ack = await _client.SetAncAsync(mode, TimeoutMs);
            if (!ack.Ok)
            {
                ReportAck($"Noise control → {ModeName(mode)}", ack);
                return;
            }

            await Task.Delay(50);
            int? now = await _client.GetNoiseModeAsync(TimeoutMs);
            AncStatusText.Text = now is int n ? $"Current mode: {ModeName(n)}" : "Current mode: unavailable";
            if (now == mode) SetStatus($"Noise control: {ModeName(mode)}.", InfoBarSeverity.Success);
            else SetStatus($"Noise control change not confirmed (earbuds report {(now is int x ? ModeName(x) : "nothing")}).",
                           InfoBarSeverity.Warning);
        });
    }

    private async void Feature_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || !_client.Connected || sender is not ToggleSwitch ts || ts.Tag is not string tag) return;
        int id = int.Parse(tag);
        bool wanted = ts.IsOn;
        string label = ts.Header?.ToString() ?? "Feature";

        await RunAsync(async () =>
        {
            var ack = await _client.SetFeatureAsync(id, wanted, TimeoutMs);
            if (!ack.Responded)
            {
                SetStatus($"{label}: no response from the earbuds", InfoBarSeverity.Warning);
                _suppress = true; ts.IsOn = !wanted; _suppress = false;
                return;
            }

            // Any ack means "accepted"; the live state tells what really happened.
            var live = await _client.QueryFeaturesAsync(new[] { id }, TimeoutMs);
            bool? actual = live.GetValueOrDefault(id);
            _suppress = true;
            ts.IsOn = actual ?? wanted;
            _suppress = false;

            if (actual is bool a)
                SetStatus($"{label}: {(a ? "on" : "off")}", a == wanted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            else
                SetStatus($"{label}: {(wanted ? "on" : "off")}", InfoBarSeverity.Success);
        });
    }

    private void ReportAck(string what, Ack ack)
    {
        if (ack.Ok) SetStatus($"{what}: OK", InfoBarSeverity.Success);
        else if (!ack.Responded) SetStatus($"{what}: no response from the earbuds", InfoBarSeverity.Warning);
        else SetStatus($"{what}: earbuds returned status {ack.Status}", InfoBarSeverity.Error);
    }

    // ------------------------------------------------------------ gestures --
    private void BuildGestureGrid()
    {
        GestureGrid.ColumnSpacing = 12;
        GestureGrid.RowSpacing = 12;
        GestureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int c = 0; c < 3; c++)
            GestureGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++)
            GestureGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int[] actions = { 2, 3, 4 };
        int[] devices = { 1, 2 };

        for (int c = 0; c < actions.Length; c++)
        {
            var h = new TextBlock { Text = Proto.Actions[actions[c]], Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            Grid.SetRow(h, 0); Grid.SetColumn(h, c + 1);
            GestureGrid.Children.Add(h);
        }

        for (int r = 0; r < devices.Length; r++)
        {
            var label = new TextBlock { Text = Proto.Devices[devices[r]], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r + 1); Grid.SetColumn(label, 0);
            GestureGrid.Children.Add(label);

            for (int c = 0; c < actions.Length; c++)
            {
                var box = new ComboBox
                {
                    ItemsSource = FuncItems,
                    DisplayMemberPath = "Name",
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Grid.SetRow(box, r + 1); Grid.SetColumn(box, c + 1);
                GestureGrid.Children.Add(box);
                _gestureBoxes[(devices[r], actions[c])] = box;
            }
        }
    }

    private void SelectFunc(int dev, int act, int func)
    {
        if (_gestureBoxes.TryGetValue((dev, act), out var box))
            box.SelectedItem = FuncItems.FirstOrDefault(f => f.Value == func) ?? FuncItems[0];
    }

    private async Task ReadGesturesCoreAsync(string successPrefix)
    {
        var q = await _client.QueryGesturesAsync(TimeoutMs);
        if (q == null)
        {
            SetStatus("No response to the gesture query.", InfoBarSeverity.Warning);
            return;
        }
        if (q.Entries == null)
        {
            SetStatus("Unexpected gesture response. Enable the log in the connection settings to see the raw payload.", InfoBarSeverity.Warning);
            AppendLog("Gesture payload: " + RealmeClient.Hex(q.Raw));
            return;
        }
        foreach (var g in q.Entries) SelectFunc(g.Device, g.Action, g.Func);
        SetStatus($"{successPrefix}{q.Entries.Count} gesture slots read from the earbuds.", InfoBarSeverity.Success);
    }

    private async void ReadGestures_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(() => ReadGesturesCoreAsync(""));

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string name || !Proto.Presets.TryGetValue(name, out var funcs)) return;
        int i = 0;
        foreach (int dev in new[] { 1, 2 })
            foreach (int act in new[] { 2, 3, 4 })
                SelectFunc(dev, act, funcs[i++]);
        SetStatus($"{name} preset loaded into the grid. Press “Apply changes” to send it.");
    }

    private async void ApplyGestures_Click(object sender, RoutedEventArgs e)
    {
        var updates = new Dictionary<(int dev, int act), int>();
        foreach (var ((dev, act), box) in _gestureBoxes)
            if (box.SelectedItem is FuncItem f && f.Value >= 0)
                updates[(dev, act)] = f.Value;

        if (updates.Count == 0)
        {
            SetStatus("Nothing to apply: every slot is set to “leave unchanged”.", InfoBarSeverity.Warning);
            return;
        }

        await RunAsync(async () =>
        {
            var ack = await _client.ApplyGesturesAsync(updates, TimeoutMs);
            ReportAck("Set gestures", ack);
            if (ack.Ok) await ReadGesturesCoreAsync("Applied. Verified: ");
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Text = "";
}
