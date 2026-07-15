using Microsoft.UI.Xaml;
using System;

namespace direct_module
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_window == null)
            {
                var window = new MainWindow();
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_window, window))
                    {
                        _window = null;
                    }
                };
                _window = window;
            }
            _window.Activate();
        }
    }
}
