using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;
using KlyraOptimizer.Services;

namespace KlyraOptimizer.Views;

public sealed class ToggleRow : INotifyPropertyChanged
{
    private bool _optimized;

    public ToggleId Id { get; init; }

    public string Glyph { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool IsOptimized
    {
        get => _optimized;
        set
        {
            if (_optimized == value) return;
            _optimized = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOptimized)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
        }
    }

    public string StateText => IsOptimized ? "Otimizado" : "Padrão do Windows";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class KlyraToolPage : UserControl, IRefreshablePage
{
    private readonly List<ToggleRow> _rows = new();
    private Action? _confirmAction;
    private bool _busy;

    public KlyraToolPage()
    {
        InitializeComponent();
        BuildRows();
    }

    private void BuildRows()
    {
        _rows.AddRange(new[]
        {
            new ToggleRow { Id = ToggleId.WindowsUpdate, Glyph = "\uE895", Title = "Windows Update",
                Description = "Reativar o padrão com proteção de drivers ou desativar por completo." },
            new ToggleRow { Id = ToggleId.MicrosoftStore, Glyph = "\uE719", Title = "Microsoft Store oficial",
                Description = "Reinstalar a loja oficial ou remover/desativar os serviços em segundo plano." },
            new ToggleRow { Id = ToggleId.Bluetooth, Glyph = "\uE702", Title = "Serviços de Bluetooth",
                Description = "Controla bthserv, BTAGService e BthAvctpSvc." },
            new ToggleRow { Id = ToggleId.GameDvr, Glyph = "\uE7FC", Title = "GameDVR e Xbox Game Bar",
                Description = "Reativar a gravação em segundo plano ou otimizar para jogos." },
            new ToggleRow { Id = ToggleId.Copilot, Glyph = "\uE734", Title = "Copilot, Recall e Bing",
                Description = "Reativar os recursos de IA e pesquisa web ou desativar tudo." },
            new ToggleRow { Id = ToggleId.Transparency, Glyph = "\uE790", Title = "Transparência e animações",
                Description = "Efeitos visuais do sistema e animações da barra de tarefas." },
            new ToggleRow { Id = ToggleId.SysMain, Glyph = "\uE945", Title = "Superfetch (SysMain)",
                Description = "Cache de aplicativos em memória RAM: reativar ou desativar." },
            new ToggleRow { Id = ToggleId.VbsHvci, Glyph = "\uE72E", Title = "Isolamento de núcleo (VBS/HVCI)",
                Description = "Segurança baseada em virtualização: reativar ou ganhar desempenho. Requer reinício." },
            new ToggleRow { Id = ToggleId.Hibernation, Glyph = "\uE7E8", Title = "Hibernação e Fast Startup",
                Description = "Reativar a hibernação ou desativar para liberar espaço em disco." }
        });

        ToggleList.ItemsSource = _rows;
    }

    public void OnActivated()
    {
        RefreshState();
        RefreshRows();

        AppServices.Log.Write(
            "Klyra Tool aberto: funções ao vivo prontas.",
            LogKind.Info);
    }

    private void RefreshState()
    {
        var system = AppServices.System;
        CurrentModeText.Text = system.ActiveModeLabel;
        CurrentModeDetail.Text = system.ActiveMode == ActiveMode.None
            ? "Aplique um modo no Otimizador ou migre diretamente abaixo."
            : "Trocar de modo aplica ou reverte somente as camadas necessárias.";

        SwitchStandard.IsEnabled = system.ActiveMode != ActiveMode.Standard;
        SwitchMedium.IsEnabled = system.ActiveMode != ActiveMode.Medium;
        SwitchAdvanced.IsEnabled = system.ActiveMode != ActiveMode.Advanced;
    }

    private void RefreshRows()
    {
        foreach (var row in _rows)
        {
            row.IsOptimized = AppServices.Optimizer.IsOptimized(row.Id);
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Motion.SetVisible(BusyRing, busy);
        Motion.SetVisible(BusyBar, busy);
        if (status != null) ActionStatus.Text = status;
    }

    private void SwitchMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || _busy) return;
        if (!int.TryParse(tag, out var modeValue)) return;

        var target = (ActiveMode)modeValue;
        if (target == AppServices.System.ActiveMode) return;
        if (!Shell.RequireAdmin()) return;

        ShowConfirm(
            "Trocar o perfil de otimização",
            $"O sistema vai migrar de \"{AppServices.System.ActiveModeLabel}\" para \"{ModeName(target)}\".\n\n" +
            "As camadas necessárias serão aplicadas e as que não pertencem ao novo modo serão revertidas.",
            async () =>
            {
                SetBusy(true, $"Migrando para {ModeName(target)}...");
                try
                {
                    await Task.Run(() => AppServices.Optimizer.SwitchMode(target));
                    AppServices.RefreshSystem();
                    SetBusy(false, $"{ModeName(target)} ativo.");
                }
                catch (Exception ex)
                {
                    SetBusy(false, "Falha ao trocar de modo: " + ex.Message);
                }

                RefreshState();
                RefreshRows();
            });
    }

    private void ToggleDefault_Click(object sender, RoutedEventArgs e) => ApplyToggle(sender, optimize: false);

    private void ToggleOptimize_Click(object sender, RoutedEventArgs e) => ApplyToggle(sender, optimize: true);

    private async void ApplyToggle(object sender, bool optimize)
    {
        if (sender is not FrameworkElement element || element.Tag is not ToggleId id) return;
        if (_busy) return;
        if (!Shell.RequireAdmin()) return;

        var row = _rows.FirstOrDefault(r => r.Id == id);
        SetBusy(true, $"Ajustando {row?.Title ?? id.ToString()}...");
        try
        {
            await Task.Run(() => AppServices.Optimizer.SetToggle(id, optimize));
            AppServices.RefreshSystem();
            SetBusy(false, $"{row?.Title}: {(optimize ? "otimizado" : "padrão restaurado")}.");
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"   [ERRO] {ex.Message}", LogKind.Failed);
            SetBusy(false, "Erro: " + ex.Message);
        }

        RefreshRows();
    }

    private async void CleanRam_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Limpando memória RAM e arquivos temporários...");
        try
        {
            await Task.Run(() => AppServices.Optimizer.CleanMemory());
            SetBusy(false, "Memória RAM e temporários limpos.");
        }
        catch
        {
            SetBusy(false, "Limpeza concluída.");
        }
    }

    private void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.RequireAdmin()) return;

        ShowConfirm(
            "Reset completo de rede",
            "O Winsock e a pilha TCP/IP serão redefinidos e o cache DNS será limpo.\n\n" +
            "Isso exige reiniciar o computador para concluir.",
            async () =>
            {
                SetBusy(true, "Redefinindo Winsock, TCP/IP e DNS...");
                try
                {
                    await Task.Run(() => AppServices.Optimizer.ResetNetwork());
                    SetBusy(false, "Rede redefinida. Reinicie o computador.");
                }
                finally
                {
                    if (_busy) SetBusy(false);
                }
            });
    }

    private async void Reregister_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Re-registrando pacotes nativos do Windows...");
        try
        {
            await Task.Run(() => AppServices.Optimizer.ReregisterAppx());
            SetBusy(false, "Pacotes nativos re-registrados.");
        }
        finally
        {
            if (_busy) SetBusy(false);
        }
    }

    private void GpuMsi_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.RequireAdmin()) return;

        ShowConfirm(
            "MSI Mode das GPUs",
            "Vamos ativar o modo MSI (Message Signaled Interrupts) nas GPUs PCI detectadas.\n\n" +
            "O valor anterior fica registrado e pode ser restaurado em Restauração > Reverter tudo.",
            async () =>
            {
                SetBusy(true, "Aplicando MSI Mode nas GPUs...");
                try
                {
                    await Task.Run(() => AppServices.Gpu.Apply(AppServices.Engine));
                    SetBusy(false, "MSI Mode aplicado. Reinicie para efetivar.");
                }
                finally
                {
                    if (_busy) SetBusy(false);
                }
            });
    }

    private void ShowConfirm(string title, string text, Action action)
    {
        _confirmAction = action;
        ConfirmTitle.Text = title;
        ConfirmText.Text = text;
        Motion.ShowOverlay(ConfirmOverlay);
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(ConfirmOverlay);
        _confirmAction = null;
    }

    private void ConfirmAction_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(ConfirmOverlay);
        var action = _confirmAction;
        _confirmAction = null;
        action?.Invoke();
    }

    private static string ModeName(ActiveMode mode) => mode switch
    {
        ActiveMode.Standard => "Modo Standard",
        ActiveMode.Medium => "Modo Medium",
        ActiveMode.Advanced => "Modo Avançado",
        _ => "Nenhum"
    };
}

