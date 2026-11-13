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
    public static async Task RunAsync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string target = Path.Combine(Paths.Root, "update-window-target");
        UpdateChecks.WritePayload(target, UpdateChecks.Payload("original"));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; result.TrySetException(e.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(async () =>
            {
                var window = new MainWindow(new AppUpdateService(new PendingDownload(requested)), target);
                window.Closed += (_, _) => { result.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                await window.CheckUpdatesAsync();
                var update = (Button)window.FindName("UpdateButton");
                if (!update.IsEnabled || !update.Content.ToString()!.Contains("0.1.6")) throw new Exception("Update UI did not offer newer version");
                update.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (((Button)window.FindName("StartStop")).IsEnabled || ((Button)window.FindName("CancelUpdate")).Visibility != Visibility.Visible) throw new Exception("Update UI allows casting during application update");
                window.Close();
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!thread.Join(1000) || File.ReadAllText(Path.Combine(target, "docs", "notes.txt")) != "original") throw new Exception("Closing download changed application files");
    }
    private sealed class PendingDownload(TaskCompletionSource requested) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                object[] assets = new[] { AppUpdateService.ZipName, AppUpdateService.ZipName + ".sha256" }.Select(name => (object)new
                { name, state = "uploaded", size = 88, browser_download_url = AppUpdateService.AssetUrl("v0.1.6", name).AbsoluteUri }).ToArray();
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new { draft = false, tag_name = "v0.1.6", published_at = "2026-11-13", assets } })) };
            }
            requested.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); throw new Exception("Unexpected request completion");
        }
    }
}
