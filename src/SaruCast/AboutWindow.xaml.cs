using System.Windows;

namespace SaruCast;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        AboutVersion.Text = $"バージョン {AppUpdateService.CurrentVersion}";
    }
}
