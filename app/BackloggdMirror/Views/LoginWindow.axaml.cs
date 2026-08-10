using Avalonia;
using Avalonia.Controls;
using BackloggdMirror.ViewModels;
using System;

namespace BackloggdMirror.Views
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.RequestClose += Close;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Under ShutdownMode.OnExplicitShutdown closing the last window is not enough: without
                // this the process would survive headless, still holding the single-instance mutex.
                // Once login succeeds MainWindow points elsewhere, so this only fires on a real quit.
                if (desktop.MainWindow == this)
                {
                    desktop.Shutdown();
                }
            }
        }
    }
}
