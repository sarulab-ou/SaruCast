using System.Threading;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using SaruCast;

// Regression of actual WPF Closing events, with an isolated, never-shown test window.
// This does not automate or interact with any user's open application.
static class ClosingChecks
{
    public static async Task RunConnectingAsync()
    {
        await using var receiver = new NativeReceiver(false, holdOffer: true);
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; result.TrySetException(e.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(async () =>
            {
                var window = new MainWindow();
                for (int i = 0; i < 120; i++) window.AppendActivity($"buffer clear {i}");
                string text = ((TextBox)window.FindName("ActivityLog")).Text;
                if (text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 100 || !text.Contains("buffer clear 119")
                    || text.Contains("buffer clear 19\n") || !((Expander)window.FindName("ActivityPanel")).IsExpanded)
                    throw new Exception("Visible activity log must expand and retain latest 100 entries");
                window.Closed += (_, _) => { result.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                var televisions = (ComboBox)window.FindName("Televisions");
                televisions.ItemsSource = new[] { new CastDevice("mock", "127.0.0.1", receiver.Port) }; televisions.SelectedIndex = 0;
                ((TextBox)window.FindName("ManualIp")).Clear();
                ((CheckBox)window.FindName("Sound")).IsChecked = false;
                ((Button)window.FindName("StartStop")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await receiver.Offered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window.Close(); window.Close();
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!thread.Join(1000) || !await receiver.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("Connecting close did not stop receiver");
    }
    public static async Task RunAsync(bool twice)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; result.TrySetException(e.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            try
            {
                var window = new MainWindow();
                window.Closed += (_, _) => { result.TrySetResult(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); };
                window.Close();
                if (twice) window.Close();
                Dispatcher.Run();
            }
            catch (Exception e) { result.TrySetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!thread.Join(1000)) throw new Exception("Window dispatcher did not terminate");
    }
}
