using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Views;

public sealed class ConflictRow
{
    public string Glyph { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool Detected { get; init; }

    public string StatusText => Detected ? "Detectado" : "OK";
}

public partial class ToolsPage : UserControl, IRefreshablePage
{
    private bool _busy;

    public ToolsPage()
    {
        InitializeComponent();
    }

    public void OnActivated()
    {
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        Motion.SetVisible(BusyRing, true);

        try
        {
            var conflicts = await Task.Run(() => AppServices.Optimizer.ScanConflicts());
            ConflictList.ItemsSource = conflicts
                .Select(c => new ConflictRow
                {
                    Glyph = c.Detected ? "\uE7BA" : "\uE930",
                    Title = c.Title,
                    Detail = c.Detail,
                    Detected = c.Detected
                })
                .ToList();

            var gpus = await Task.Run(() => AppServices.Gpu.GetGpus());
            GpuList.ItemsSource = gpus;
            AppServices.Log.Write($"[FERRAMENTAS] GPUs PCI detectadas: {gpus.Count}.", LogKind.Info);

            if (gpus.Count == 0)
            {
                GpuStatus.Text = "Nenhuma GPU PCI detectada neste computador.";
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[FERRAMENTAS] Falha ao atualizar: {ex.Message}", LogKind.Warning);
        }
        finally
        {
            _busy = false;
            Motion.SetVisible(BusyRing, false);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Log.Phase("SCANNER DE CONFLITOS");

        var items = await Task.Run(() => AppServices.Optimizer.ScanConflicts());

        foreach (var item in items)
        {
            AppServices.Log.Write(
                item.Detected
                    ? $"   [CONFLITO] {item.Title} — {item.Detail}"
                    : $"   [OK] {item.Title} — {item.Detail}",
                item.Detected ? LogKind.Warning : LogKind.Info);
        }

        await RefreshAsync();
    }

    private void GoRestore_Click(object sender, RoutedEventArgs e) => AppServices.Navigate("restore");

    private void WinUtil_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Log.Write("   [*] Abrindo Chris Titus Tech WinUtil em uma janela do PowerShell...", LogKind.Info);
        ToolActionStatus.Text = "Abrindo Chris Titus WinUtil...";

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"irm 'https://christitus.com/win' | iex\"",
                UseShellExecute = true,
                Verb = Shell.IsAdmin ? string.Empty : "runas"
            };

            Process.Start(info);
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"   [ERRO] Não foi possível abrir o WinUtil: {ex.Message}", LogKind.Failed);
            ToolActionStatus.Text = "Não foi possível abrir o WinUtil.";
        }
    }

    private async void BleachBit_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Log.Phase("INSTALANDO BLEACHBIT VIA WINGET");

        if (!AppServices.System.WingetInstalled)
        {
            AppServices.Log.Write("   [ERRO] winget não encontrado. Instale pelo botão da página Microsoft Store.", LogKind.Failed);
            ToolActionStatus.Text = "winget não encontrado — instale pela aba Microsoft Store.";
            return;
        }

        Motion.SetVisible(BusyRing, true);
        ToolActionStatus.Text = "Instalando BleachBit em segundo plano...";
        var ok = await AppServices.Winget.InstallAsync("BleachBit.BleachBit", "winget", CancellationToken.None);
        Motion.SetVisible(BusyRing, false);
        ToolActionStatus.Text = ok ? "BleachBit instalado com sucesso." : "Falha na instalação (detalhes no log).";
    }

    private async void GpuApply_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.RequireAdmin()) return;

        Motion.SetVisible(BusyRing, true);
        AppServices.Log.Phase("APLICANDO MSI MODE NAS GPUS");
        await Task.Run(() => AppServices.Gpu.Apply(AppServices.Engine));
        Motion.SetVisible(BusyRing, false);
        GpuStatus.Text = "MSI Mode aplicado. Reinicie o computador para efetivar.";
    }

    private async void GpuRevert_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.RequireAdmin()) return;

        Motion.SetVisible(BusyRing, true);
        AppServices.Log.Phase("REVERTENDO MSI MODE");
        await Task.Run(() => AppServices.Gpu.Revert(AppServices.Engine));
        Motion.SetVisible(BusyRing, false);
        GpuStatus.Text = "Valores anteriores de MSI Mode restaurados.";
    }
}
