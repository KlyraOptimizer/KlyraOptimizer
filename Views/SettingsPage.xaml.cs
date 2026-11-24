using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;
using Microsoft.Win32;

namespace KlyraOptimizer.Views;

public partial class SettingsPage : UserControl, IRefreshablePage
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => OnActivated();
    }

    public void OnActivated()
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

        AboutVersion.Text = $"Versão {AppServices.Version} — Windows 10 Fluent Edition";

        ElevateButton.Visibility = Shell.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        UninstallButton.IsEnabled = FindUninstallString() != null;

        ThemeSystem.IsChecked = ThemeService.Mode == ThemeMode.System;
        ThemeLight.IsChecked = ThemeService.Mode == ThemeMode.Light;
        ThemeDark.IsChecked = ThemeService.Mode == ThemeMode.Dark;

        ThemeDetail.Text = ThemeService.Mode switch
        {
            ThemeMode.Light => "Tema claro ativo nesta sessão.",
            ThemeMode.Dark => "Tema escuro ativo nesta sessão.",
            _ => "Segue automaticamente o tema claro/escuro e a cor de destaque do Windows."
        };
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
        OnActivated();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.RelaunchElevated("--elevated-launch"))
        {
            Application.Current.Shutdown();
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

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var command = FindUninstallString();
        if (string.IsNullOrWhiteSpace(command))
        {
            MessageBox.Show(
                "O desinstalador não foi encontrado. Use \"Aplicativos e recursos\" do Windows para remover o Klyra Optimizer.",
                AppServices.AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            "Deseja desinstalar o Klyra Optimizer?",
            AppServices.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = command, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir o desinstalador: " + ex.Message,
                AppServices.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
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
