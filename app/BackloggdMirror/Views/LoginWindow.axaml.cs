using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
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

        private async void OnCopyInstallDepsClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is not LoginViewModel vm || Clipboard == null) return;

            try
            {
                await Clipboard.SetTextAsync(vm.InstallDepsCommand);
                vm.IsInstallDepsCommandCopied = true;
            }
            catch
            {
                // The command stays selectable, so copying by hand is still possible.
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
