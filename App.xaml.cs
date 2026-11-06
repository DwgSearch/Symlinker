namespace Symlinker
{
    using System.Windows;

    using ControlzEx.Theming;

    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Optional "--lang <culture>" (e.g. --lang ja) forces the UI language,
            // for testing translations without changing the Windows display language.
            // Must run before base.OnStartup, which creates the main window.
            for (int i = 0; i + 1 < e.Args.Length; i++)
            {
                if (string.Equals(e.Args[i], "--lang", System.StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var culture = new System.Globalization.CultureInfo(e.Args[i + 1]);
                        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    }
                    catch (System.Globalization.CultureNotFoundException)
                    {
                        // Unknown culture name: keep the Windows language.
                    }
                    break;
                }
            }

            base.OnStartup(e);

            // Follow the Windows light/dark mode and accent color.
            ThemeManager.Current.ThemeSyncMode = ThemeSyncMode.SyncAll;
            ThemeManager.Current.SyncTheme();
        }
    }
}
