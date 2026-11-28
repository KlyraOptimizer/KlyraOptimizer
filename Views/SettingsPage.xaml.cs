using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Views;

public partial class SettingsPage : UserControl, IRefreshablePage
{
    private string? _cachedUninstallCommand;
    private bool _uninstallChecked;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public async void OnActivated()
    {
        var system = AppServices.System;

        InfoOs.Text = $"{system.OsLabel} ({(system.IsWindows11 ? "Windows 11" : "Windows 10 21H2+")})";
        InfoBuild.Text = $"build {system.BuildNumber}.{system.BuildRevision}";
        InfoMachine.Text = $"{system.MachineName} • {system.UserName}";
        InfoAdmin.Text = Shell.IsAdmin ? "Administrador (elevado)" : "Usuário padrão (sem elevação)";
        InfoWinget.Text = system.WingetInstalled
            ? $"Instalado — versão {system.WingetVersion}"
            : "Não encontrado (a Loja pode instalar automaticamente)";
        InfoMode.Text = system.ActiveModeLabel;
        InfoLogPath.Text = AppServices.Log.FilePath;

        AboutVersion.Text = $"Versão {AppServices.Version} — Windows 10 Fluent Edition (1809/1903)";

        ElevateButton.Visibility = Shell.IsAdmin ? Visibility.Collapsed : Visibility.Visible;

        StartWithWindowsSwitch.IsChecked = AppSettings.Current.StartWithWindows;
        CloseToTraySwitch.IsChecked = AppSettings.Current.CloseToTray;

        ThemeSystem.IsChecked = ThemeService.Mode == ThemeMode.System;
        ThemeLight.IsChecked = ThemeService.Mode == ThemeMode.Light;
        ThemeDark.IsChecked = ThemeService.Mode == ThemeMode.Dark;

        ThemeDetail.Text = ThemeService.Mode switch
        {
            ThemeMode.Light => "Tema claro ativo nesta sessão.",
            ThemeMode.Dark => "Tema escuro ativo nesta sessão.",
            _ => "Segue automaticamente o tema claro/escuro e a cor de destaque do Windows."
        };

        if (!_uninstallChecked)
        {
            _cachedUninstallCommand = await Task.Run(FindUninstallString);
            _uninstallChecked = true;
        }
        UninstallButton.IsEnabled = !string.IsNullOrWhiteSpace(_cachedUninstallCommand);
    }

    private void StartWithWindows_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.StartWithWindows = StartWithWindowsSwitch.IsChecked == true;
        AppSettings.Current.Save();
        AppSettings.Current.SyncStartupRegistration();
    }

    private void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        var enabled = CloseToTraySwitch.IsChecked == true;
        AppSettings.Current.CloseToTray = enabled;
        AppSettings.Current.Save();

        if (enabled)
        {
            AppServices.Tray?.EnsureVisible();
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;

        var mode = tag switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        ThemeService.Apply(mode);
        AppSettings.Current.Theme = mode;
        AppSettings.Current.Save();
        OnActivated();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.RelaunchElevated("--elevated-launch"))
        {
            App.ExitCompletely();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await Task.Run(() => AppServices.System.Refresh());
        OnActivated();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Shell.OpenFolder(AppServices.Log.Directory);

    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => Shell.Open(AppServices.Log.FilePath);

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        Shell.OpenFolder(AppContext.BaseDirectory);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var command = FindUninstallString();
        if (string.IsNullOrWhiteSpace(command))
        {
            await AppServices.ShowDialogAsync(
                AppServices.AppName,
                "O desinstalador não foi encontrado. Use \"Aplicativos e recursos\" do Windows para remover o Klyra Optimizer.",
                "Entendi");
            return;
        }

        var confirm = await AppServices.ShowDialogAsync(
            "Desinstalar Klyra Optimizer?",
            "O assistente de desinstalação será iniciado e o aplicativo será encerrado.",
            primaryButtonText: "Desinstalar",
            closeButtonText: "Cancelar",
            isDanger: true);

        if (!confirm) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = command, UseShellExecute = true });
            App.ExitCompletely();
        }
        catch (Exception ex)
        {
            await AppServices.ShowDialogAsync(
                AppServices.AppName,
                "Não foi possível abrir o desinstalador: " + ex.Message,
                "Fechar");
        }
    }

    private static string? FindUninstallString()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var parent = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (parent == null) continue;

            foreach (var name in parent.GetSubKeyNames())
            {
                using var key = parent.OpenSubKey(name);
                if (key?.GetValue("DisplayName") is not string display) continue;
                if (!display.StartsWith("Klyra Optimizer", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.GetValue("UninstallString") is string command && command.Length > 0) return command;
            }
        }

        return null;
    }
}
