using System.Threading;
using System.Windows.Threading;
using SaruCast;

// Regression of actual WPF Closing events, with an isolated, never-shown test window.
// This does not automate or interact with any user's open application.
static class ClosingChecks
{
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
