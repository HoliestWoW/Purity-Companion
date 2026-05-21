using System.Threading;
using System.Windows;

namespace PurityCompanion
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string appName = "PurityCompanionApp_UniqueInstance";
            bool createdNew;

            _mutex = new Mutex(true, appName, out createdNew);

            if (!createdNew)
            {
                // Explicitly defining the WPF MessageBox to avoid ambiguity errors
                System.Windows.MessageBox.Show("The Purity Companion is already running in the background. Check your system tray!",
                                "Already Running",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);

                System.Windows.Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);
        }
    }
}