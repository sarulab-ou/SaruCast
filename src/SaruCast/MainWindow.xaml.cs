using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using Microsoft.Win32;

namespace SaruCast;

public partial class MainWindow : Window
{
    private readonly Settings settings = Settings.Load();
    private readonly CancellationTokenSource life = new();
    private CancellationTokenSource? search, start;
    private CastSession? session;
    private Task? starting;
    private Task? stopTask;
    private bool stopping, probing, closing, allowClose;
    public MainWindow()
    {
        InitializeComponent();
        Sound.IsChecked = settings.Audio; Hardware.IsChecked = settings.Hardware; Gdi.IsChecked = settings.Gdi;
        Quality.SelectedIndex = settings.Height <= 720 ? 1 : 0;
        ManualIp.Text = settings.ManualTelevision;
        Screens.ItemsSource = Displays.List();
        Screens.SelectedItem = Screens.Items.Cast<DisplayInfo>().FirstOrDefault(d => d.DeviceName == settings.Display) ?? Screens.Items.Cast<DisplayInfo>().FirstOrDefault();
        Televisions.DropDownClosed += (_, _) => { if (Televisions.SelectedItem != null) ManualIp.Clear(); };
        Loaded += async (_, _) => await SearchAsync();
        Closing += OnClosing;
        SystemEvents.PowerModeChanged += PowerChanged;
        NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
    }
    private async Task SearchAsync()
    {
        search?.Cancel(); search?.Dispose(); search = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        var token = search.Token; Refresh.IsEnabled = false;
        var currentSearch = search;
        State.Text = "検索中…";
        try
        {
            var devices = await Mdns.DiscoverAsync(token);
            if (token.IsCancellationRequested || search != currentSearch || session != null || probing) return;
            Televisions.ItemsSource = devices;
            Televisions.SelectedItem = devices.FirstOrDefault(d => d.Address == settings.Television) ?? devices.FirstOrDefault();
            State.Text = devices.Count > 0 ? "テレビと共有する画面を選択してください。" : "テレビが見つかりません。同じLANを確認するか、詳細設定からIPを入力してください。";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { ShowError(e); }
        finally { if (!life.IsCancellationRequested && search == currentSearch) Refresh.IsEnabled = session == null && !probing; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private void ReadOptions()
    {
        settings.Audio = Sound.IsChecked == true; settings.Hardware = Hardware.IsChecked == true; settings.Gdi = Gdi.IsChecked == true;
        settings.Height = Quality.SelectedIndex == 1 ? 720 : 1080;
        settings.ManualTelevision = ManualIp.Text.Trim();
        if (Screens.SelectedItem is DisplayInfo d) settings.Display = d.DeviceName;
    }
    private CastDevice Device()
    {
        if (!string.IsNullOrWhiteSpace(ManualIp.Text))
        {
            if (!IPAddress.TryParse(ManualIp.Text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] >= 224)
                throw new InvalidOperationException("テレビのIPv4アドレスを入力してください。");
            return new("手動指定", ip.ToString());
        }
        return Televisions.SelectedItem as CastDevice ?? throw new InvalidOperationException("テレビを選択するか、詳細設定でIPアドレスを入力してください。");
    }
    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (stopping || probing || closing) return;
        if (session != null || start != null) { await StopAsync("停止しました。"); return; }
        try
        {
            CastDevice device = Device();
            var display = Screens.SelectedItem as DisplayInfo ?? throw new InvalidOperationException("共有する画面がありません。");
            ReadOptions(); settings.Television = device.Address; settings.Save();
            search?.Cancel(); start = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            session = new(); var currentSession = session;
            session.Status += status => Dispatcher.InvokeAsync(() => { if (session == currentSession && !stopping) State.Text = status; });
            session.Disconnected += message => Dispatcher.InvokeAsync(async () => { if (session == currentSession && !stopping) await StopAsync(message); });
            ToggleInputs(false); StartStop.Content = "キャンセル";
            starting = session.StartAsync(device, display, settings, start.Token);
            await starting;
            if (start?.IsCancellationRequested == false && !stopping) StartStop.Content = "キャスト停止";
        }
        catch (OperationCanceledException) { if (!stopping) await StopAsync("停止しました。"); }
        catch (Exception error)
        {
            string message = FriendlyError(error);
            if (!stopping) await StopAsync(message);
        }
    }
    private Task StopAsync(string message)
    {
        if (stopTask != null) return stopTask;
        return stopTask = StopCoreAsync(message);
    }
    private async Task StopCoreAsync(string message)
    {
        stopping = true; StartStop.IsEnabled = false;
        State.Text = "停止中…";
        await Task.Yield();
        try
        {
            start?.Cancel();
            if (starting != null) try { await starting; } catch (Exception) { }
            if (session != null) await session.DisposeAsync();
        }
        catch (Exception e) { Log.Write($"Stop error {e.GetType().Name}"); message = "停止処理でエラーが発生しました。診断ログを確認してください。"; }
        finally
        {
            session = null; starting = null; start?.Dispose(); start = null;
            stopping = false; StartStop.Content = "キャスト開始"; StartStop.IsEnabled = true; ToggleInputs(true); State.Text = message;
            stopTask = null;
        }
    }
    private void ToggleInputs(bool enabled)
    { Televisions.IsEnabled = Screens.IsEnabled = Sound.IsEnabled = Refresh.IsEnabled = Advanced.IsEnabled = enabled; }
    private static string FriendlyError(Exception e)
    {
        Log.Write($"Operation failed {e.GetType().Name}");
        return e switch
        {
            SocketException => "接続できません。テレビの電源、同じLAN、VPNとWindowsのプライベートネットワーク許可を確認してください。",
            TimeoutException => "応答を待つ時間を超えました。テレビの電源とネットワーク許可を確認してください。",
            _ => "接続できません: " + e.Message
        };
    }
    private void ShowError(Exception e) => State.Text = FriendlyError(e);
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        if (probing || session != null || stopping || closing) return;
        probing = true; search?.Cancel(); Probe.IsEnabled = false; Refresh.IsEnabled = false; StartStop.IsEnabled = false;
        try
        {
            var device = Device(); State.Text = "接続先を確認中…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(life.Token); timeout.CancelAfter(8000);
            await using var probe = new CastClient(); await probe.ConnectAsync(device, timeout.Token);
            State.Text = "Google Castの応答を確認しました。キャスト開始を押してください。";
        }
        catch (Exception error) { ShowError(error); }
        finally { probing = false; Probe.IsEnabled = true; Refresh.IsEnabled = session == null; StartStop.IsEnabled = true; }
    }
    private void Logs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", Path.Combine(Paths.Root, "logs")) { UseShellExecute = true });
    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "licenses");
        var text = new System.Windows.Controls.TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Padding = new Thickness(16), Text = Directory.Exists(path) ? string.Join("\n\n", Directory.GetFiles(path, "*.txt").Select(f => File.ReadAllText(f))) : "開発ビルド。READMEとlicensesを参照してください。" };
        new Window { Title = "SaruCast · ライセンス", Owner = this, Width = 740, Height = 600, Content = text }.Show();
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    { if (e.Mode == PowerModes.Suspend) Dispatcher.InvokeAsync(async () => { if (session != null) await StopAsync("スリープのため停止しました。復帰後に再開始してください。"); }); }
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    { if (!e.IsAvailable) Dispatcher.InvokeAsync(async () => { if (session != null) await StopAsync("ネットワークが切断されました。接続後に再開始してください。"); }); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return; e.Cancel = true; if (closing) return; closing = true;
        // WPF forbids a second Close() while the original Closing event is on the stack.
        await Task.Yield();
        life.Cancel(); search?.Cancel(); await StopAsync("終了中…");
        ReadOptions(); try { settings.Save(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        SystemEvents.PowerModeChanged -= PowerChanged; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
        allowClose = true; Close();
    }
}
