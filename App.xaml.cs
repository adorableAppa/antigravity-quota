using System.Windows;
using ModernWpf;

namespace AntigravityQuota
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        }
    }
}
