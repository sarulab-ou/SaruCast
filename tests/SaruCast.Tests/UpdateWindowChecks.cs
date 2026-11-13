using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SaruCast;

static class UpdateWindowChecks
{
    private static string NextVersion => new Version(AppUpdateService.CurrentVersion.Major, AppUpdateService.CurrentVersion.Minor, AppUpdateService.CurrentVersion.Build + 1).ToString();
    public static async Task RunAsync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string target = Path.Combine(Paths.Root, "update-window-target");
        UpdateChecks.WritePayload(target, UpdateChecks.Payload("original"));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string stage = "starting UI dispatcher";
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; result.TrySetException(e.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(async () =>
            {
                stage = "constructing main window";
                var window = new MainWindow(new AppUpdateService(new PendingDownload(requested)), target);
                var help = (MenuItem)window.FindName("HelpMenu");
                if (help.Header.ToString() != "ヘルプ(_H)" || help.Items.OfType<MenuItem>().First().Header.ToString() != "バージョン情報" || window.FindName("UpdateButton") != null)
                    throw new Exception("Help menu must contain About and replace the main update button");
                stage = "constructing About dialog";
                var about = new AboutWindow();
                if (((TextBlock)about.FindName("AboutVersion")).Text != $"バージョン {AppUpdateService.CurrentVersion}") throw new Exception("About dialog version differs from running application");
                about.Close();
                window.Closed += (_, _) => { result.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                stage = "checking mock release";
                await window.CheckUpdatesAsync();
                var update = (MenuItem)window.FindName("UpdateMenu");
                if (!update.IsEnabled || update.Header.ToString() != "更新" || !update.ToolTip.ToString()!.Contains(NextVersion)) throw new Exception("Update menu did not offer newer version");
                stage = "starting update from menu";
                update.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (((Button)window.FindName("StartStop")).IsEnabled || ((MenuItem)window.FindName("CancelUpdateMenu")).Visibility != Visibility.Visible) throw new Exception("Update UI allows casting during application update");
                stage = "closing during download";
                window.Close();
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await result.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException error) { throw new TimeoutException($"WPF update menu check stalled while {stage}", error); }
        if (!thread.Join(1000) || File.ReadAllText(Path.Combine(target, "docs", "notes.txt")) != "original") throw new Exception("Closing download changed application files");
    }
    private sealed class PendingDownload(TaskCompletionSource requested) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                object[] assets = new[] { AppUpdateService.ZipName, AppUpdateService.ZipName + ".sha256" }.Select(name => (object)new
                { name, state = "uploaded", size = 88, browser_download_url = AppUpdateService.AssetUrl("v" + NextVersion, name).AbsoluteUri }).ToArray();
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new { draft = false, tag_name = "v" + NextVersion, published_at = "2026-11-13", assets } })) };
            }
            requested.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new Exception("Unexpected request completion");
        }
    }
}
