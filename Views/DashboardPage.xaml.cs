using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Views;

public partial class DashboardPage : UserControl, IRefreshablePage
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshState();
    }

    public void OnActivated() => RefreshState();

    private void RefreshState()
    {
        var system = AppServices.System;

        SubtitleText.Text = $"{system.MachineName} • usuário {system.UserName} • {(Shell.IsAdmin ? "executando como administrador" : "modo limitado")}";

        HeroOsChip.Text = $"{system.OsLabel} • build {system.BuildNumber}";
        HeroModeChip.Text = $"Modo: {SystemModeShort(system.ActiveMode)}";
        HeroAdminText.Text = Shell.IsAdmin ? "Administrador" : "Sem elevação";

        SystemValue.Text = system.OsLabel;
        SystemDetail.Text = $"{system.FullVersion} • {(system.IsWindows11 ? "Windows 11" : "Windows 10 21H2+")}";

        ModeValue.Text = system.ActiveModeLabel;
        ModeDetail.Text = system.ActiveMode == ActiveMode.None
            ? "Nenhum ajuste aplicado ainda pelo Klyra Optimizer."
            : "As camadas deste perfil estão ativas no sistema.";

        WingetValue.Text = system.WingetInstalled ? "Instalado" : "Não encontrado";
        WingetDetail.Text = system.WingetInstalled
            ? $"winget {system.WingetVersion} • pronto para a Loja embutida"
            : "A Loja pode instalar o winget automaticamente para você.";

        RestoreValue.Text = system.RestoreProtectionEnabled ? "Ativa" : "Desativada";
        RestoreDetail.Text = system.RestoreProtectionEnabled
            ? "Pontos de restauração habilitados na unidade do sistema."
            : "Ativamos automaticamente antes de aplicar qualquer otimização.";
    }

    private static string SystemModeShort(ActiveMode mode) => mode switch
    {
        ActiveMode.Standard => "Standard",
        ActiveMode.Medium => "Medium",
        ActiveMode.Advanced => "Avançado",
        _ => "nenhum"
    };

    private void GoOptimizer_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("optimizer");

    private void GoTool_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("tool");

    private void GoStore_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("store");

    private void GoRestore_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("restore");

    private void GoSettings_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("settings");

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        Motion.SetVisible(ScanRing, true);
        ScanFeedback.Text = "Verificando conflitos em segundo plano...";

        AppServices.Log.Phase("SCANNER DE CONFLITOS");
        var items = await Task.Run(() => AppServices.Optimizer.ScanConflicts());

        foreach (var item in items)
        {
            AppServices.Log.Write(
                item.Detected
                    ? $"  [CONFLITO] {item.Title} — {item.Detail}"
                    : $"  [OK] {item.Title} — {item.Detail}",
                item.Detected ? LogKind.Warning : LogKind.Info);
        }

        var detected = items.Count(i => i.Detected);
        Motion.SetVisible(ScanRing, false);
        ScanFeedback.Text = detected == 0
            ? "Sistema limpo — nenhum conflito prévio encontrado."
            : $"{detected} ajuste(s) prévio(s) detectado(s) (detalhes registrados nos logs).";
    }
}
