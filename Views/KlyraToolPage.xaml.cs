using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
        }
    }

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
            new ToggleRow { Id = ToggleId.WindowsUpdate, Glyph = "\uE895", Title = "Bloquear Windows Update automático",
                Description = "Ativado: para e desativa serviços de update. Desativado: restaura com proteção de drivers." },
            new ToggleRow { Id = ToggleId.MicrosoftStore, Glyph = "\uE719", Title = "Desativar serviços da Microsoft Store oficial",
                Description = "Ativado: bloqueia a loja oficial em segundo plano. Desativado: reativa os serviços." },
            new ToggleRow { Id = ToggleId.Bluetooth, Glyph = "\uE702", Title = "Desativar serviços de Bluetooth",
                Description = "Controla bthserv, BTAGService e BthAvctpSvc para economizar memória." },
            new ToggleRow { Id = ToggleId.GameDvr, Glyph = "\uE7FC", Title = "Otimizar GameDVR e desativar captura em segundo plano",
                Description = "Reduz consumo de GPU/CPU em jogos desativando a gravação contínua da Game Bar." },
            new ToggleRow { Id = ToggleId.Copilot, Glyph = "\uE734", Title = "Desativar Copilot, Recall e pesquisas Bing",
                Description = "Bloqueia recursos de IA e sugestões web no Menu Iniciar e Explorer." },
            new ToggleRow { Id = ToggleId.Transparency, Glyph = "\uE790", Title = "Reduzir transparência e animações da barra de tarefas",
                Description = "Alivia a composição DWM em computadores mais modestos." },
            new ToggleRow { Id = ToggleId.SysMain, Glyph = "\uE945", Title = "Desativar Superfetch (SysMain)",
                Description = "Evita pré-carregamento constante em disco/RAM em sistemas com SSD NVMe." },
            new ToggleRow { Id = ToggleId.VbsHvci, Glyph = "\uE72E", Title = "Desativar Isolamento de Núcleo (VBS/HVCI)",
                Description = "Libera desempenho bruto de CPU em jogos competitivos. Requer reinicialização." },
            new ToggleRow { Id = ToggleId.Hibernation, Glyph = "\uE7E8", Title = "Desativar Hibernação e Fast Startup",
                Description = "Remove o arquivo hiberfil.sys liberando vários gigabytes no SSD." }
        });

        ToggleList.ItemsSource = _rows;
    }

    public void OnActivated()
    {
        RefreshState();
        _ = RefreshRowsAsync();
        AppServices.Log.Write("Klyra Tool aberto: chaves ToggleSwitch prontas.", LogKind.Info);
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

    private async Task RefreshRowsAsync()
    {
        var ids = _rows.Select(r => r.Id).ToArray();
        var states = await Task.Run(() =>
        {
            var dict = new Dictionary<ToggleId, bool>(ids.Length);
            foreach (var id in ids)
            {
                dict[id] = AppServices.Optimizer.IsOptimized(id);
            }
            return dict;
        });

        foreach (var row in _rows)
        {
            if (states.TryGetValue(row.Id, out var opt))
            {
                row.IsOptimized = opt;
            }
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Motion.SetVisible(BusyRing, busy);
        Motion.SetVisible(BusyBar, busy);
        if (status != null) ActionStatus.Text = status;
    }

    private async void SwitchMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || _busy) return;
        if (!int.TryParse(tag, out var modeValue)) return;

        var target = (ActiveMode)modeValue;
        if (target == AppServices.System.ActiveMode) return;
        if (!await Shell.RequireAdminAsync()) return;

        ShowConfirm(
            "Trocar o perfil de otimização",
            $"O sistema vai migrar de \"{AppServices.System.ActiveModeLabel}\" para \"{ModeName(target)}\".\n\n" +
            "A Proteção do Sistema será verificada/ativada, um Ponto de Restauração será criado automaticamente e as camadas serão ajustadas.",
            async () =>
            {
                SetBusy(true, "Verificando Proteção do Sistema e criando Ponto de Restauração...");
                try
                {
                    await AppServices.Restore.EnsureAndCreatePointAsync($"Klyra Optimizer - {target}");
                    SetBusy(true, $"Migrando para {ModeName(target)}...");
                    await Task.Run(() => AppServices.Optimizer.SwitchMode(target));
                    AppServices.RefreshSystem();
                    SetBusy(false, $"{ModeName(target)} ativo.");
                }
                catch (Exception ex)
                {
                    SetBusy(false, "Falha ao trocar de modo: " + ex.Message);
                }

                RefreshState();
                await RefreshRowsAsync();
            });
    }

    private async void RowSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ToggleRow row } box) return;

        // O estado alvo e a inversao do estado atual sincronizado no modelo.
        // IMPORTANTE: nunca atribuir "box.IsChecked = ..." diretamente, pois isso define um valor
        // local na DependencyProperty e destroi o Binding OneWay do XAML!
        var desired = !row.IsOptimized;

        if (_busy)
        {
            BindingOperations.GetBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }

        if (!await Shell.RequireAdminAsync())
        {
            BindingOperations.GetBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }

        row.IsOptimized = desired;
        BindingOperations.GetBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();

        SetBusy(true, $"Ajustando {row.Title}...");
        box.IsEnabled = false;
        try
        {
            await Task.Run(() => AppServices.Optimizer.SetToggle(row.Id, desired));
            AppServices.RefreshSystem();

            var actual = await Task.Run(() => AppServices.Optimizer.IsOptimized(row.Id));
            row.IsOptimized = actual;
            BindingOperations.GetBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();

            SetBusy(false, $"{row.Title}: {(actual ? "Ativado" : "Padrão do Windows")}.");
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"   [ERRO] {ex.Message}", LogKind.Failed);
            await RefreshRowsAsync();
            BindingOperations.GetBindingExpression(box, ToggleButton.IsCheckedProperty)?.UpdateTarget();
            SetBusy(false, "Erro: " + ex.Message);
        }
        finally
        {
            box.IsEnabled = true;
        }
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

    private async void ResetNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (!await Shell.RequireAdminAsync()) return;

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

    private async void GpuMsi_Click(object sender, RoutedEventArgs e)
    {
        if (!await Shell.RequireAdminAsync()) return;

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
