using System.IO;
using System.Windows;

namespace SaruCast;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0) return Diagnostics.RunAsync(args).GetAwaiter().GetResult();
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) => { Log.Error("Unhandled UI error", e.Exception); MessageBox.Show("処理を続行できません。診断ログを確認して再起動してください。", "SaruCast"); e.Handled = true; app.Shutdown(1); };
            return app.Run(new MainWindow());
        }
        catch (Exception e)
        {
            Log.Write("Fatal " + e.GetType().Name);
            if (args.Length == 0) MessageBox.Show("SaruCastを起動できません。ZIP全体を展開したか確認してください。\n" + e.Message, "SaruCast");
            return 1;
        }
    }
}
