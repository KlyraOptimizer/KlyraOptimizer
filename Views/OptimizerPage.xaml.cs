using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;
using KlyraOptimizer.Services;

namespace KlyraOptimizer.Views;

public partial class OptimizerPage : UserControl, IRefreshablePage
{
    private OptimizerOptions? _pending;

    public OptimizerPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            AppServices.Engine.CountersChanged += UpdateCounters;
            AppServices.Log.Logged += OnBackgroundLog;
            UpdateCounters();
        };

        Unloaded += (_, _) =>
        {
            AppServices.Engine.CountersChanged -= UpdateCounters;
            AppServices.Log.Logged -= OnBackgroundLog;
        };
    }

    private void MenuDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MenuDelayLabel != null)
        {
            MenuDelayLabel.Text = $"{(int)Math.Round(e.NewValue)} ms";
        }
    }

    private void OnBackgroundLog(string line, LogKind kind)
    {
        if (!AppServices.Optimizer.Busy) return;
        if (kind is LogKind.Phase or LogKind.Applied)
        {
            StatusText.Text = line;
        }
    }

    public void OnActivated()
    {
        UpdateCounters();

        switch (AppServices.System.ActiveMode)
        {
            case ActiveMode.Standard:
                ModeStandard.IsChecked = true;
                break;
            case ActiveMode.Medium:
                ModeMedium.IsChecked = true;
                break;
            case ActiveMode.Advanced:
                ModeAdvanced.IsChecked = true;
                break;
        }

        if (!AppServices.Optimizer.Busy)
        {
            StatusText.Text = AppServices.System.ActiveMode == ActiveMode.None
                ? "Pronto para otimizar. O perfil ativo será registrado ao final."
                : $"Modo atual gravado no sistema: {AppServices.System.ActiveModeLabel}.";
        }
    }

    private DateTime _lastPulse = DateTime.MinValue;

    private void UpdateCounters()
    {
        var engine = AppServices.Engine;

        var appliedText = $"Aplicados {engine.Applied}";
        var skippedText = $"Pulados {engine.Skipped}";
        var failedText = $"Erros {engine.Failed}";

        var appliedChanged = AppliedChip.Text != appliedText;
        var skippedChanged = SkippedChip.Text != skippedText;
        var failedChanged = FailedChip.Text != failedText;

        AppliedChip.Text = appliedText;
        SkippedChip.Text = skippedText;
        FailedChip.Text = failedText;

        if (AppServices.Optimizer.Busy)
        {
            var total = engine.Applied + engine.Skipped + engine.Failed;
            CompletionBar.Visibility = Visibility.Visible;
            CompletionBar.Value = Math.Min(96, total * 1.6);
        }

        if (!appliedChanged && !skippedChanged && !failedChanged) return;

        var now = DateTime.UtcNow;
        if ((now - _lastPulse).TotalMilliseconds < 260) return;
        _lastPulse = now;

        var target = failedChanged ? FailedChip : appliedChanged ? AppliedChip : SkippedChip;
        Motion.Pulse(target);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Optimizer.Busy) return;

        var options = BuildOptions();
        _pending = options;

        ConfirmSummary.Text =
            $"Modo selecionado: {options.ModeName}\n" +
            $"CompactOS: {CompactLabel(options.Compact)}\n" +
            $"Windows Update: {(options.Update == UpdateChoice.Disable ? "desativar" : "manter ativo com proteção de drivers")}\n" +
            $"Microsoft Store: {StoreLabel(options.Store)}\n" +
            $"Bluetooth: {(options.DisableBluetooth ? "desativar serviços" : "manter ativo")}\n" +
            $"MenuShowDelay: {options.MenuShowDelayMs} ms\n" +
            "Ponto de restauração: proteção verificada/ativada e ponto criado automaticamente antes de aplicar.";

        Motion.ShowOverlay(ConfirmOverlay);
    }

    private void CancelConfirm_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(ConfirmOverlay);
        _pending = null;
    }

    private async void ConfirmApply_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(ConfirmOverlay);
        var options = _pending;
        if (options == null) return;

        if (!await Shell.RequireAdminAsync()) return;

        await RunOptimizationAsync(options);
    }

    private async Task RunOptimizationAsync(OptimizerOptions options)
    {
        SetBusy(true);
        CompletionBar.Value = 5;
        CompletionBar.Visibility = Visibility.Visible;
        StatusText.Text = $"Aplicando {options.ModeName} em segundo plano...";

        OptimizerSummary summary;

        try
        {
            StatusText.Text = "Verificando/ativando Proteção do Sistema e criando Ponto de Restauração...";
            var rpCreated = await AppServices.Restore.EnsureAndCreatePointAsync($"Klyra Optimizer - {options.Mode}");
            StatusText.Text = rpCreated
                ? $"Ponto de Restauração criado. Aplicando {options.ModeName}..."
                : $"Aplicando {options.ModeName} em segundo plano...";

            summary = await Task.Run(() => AppServices.Optimizer.Apply(options, CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"   [ERRO FATAL] {ex.Message}", LogKind.Failed);
            SetBusy(false);
            StatusText.Text = "Falha durante a otimização: " + ex.Message;
            return;
        }

        CompletionBar.Value = 100;
        SetBusy(false);
        AppServices.RefreshSystem();
        UpdateCounters();

        StatusText.Text =
            $"Concluído: {summary.Applied} alterações aplicadas, {summary.Skipped} já estavam ativas, {summary.Failed} bloqueios/erros.";

        RebootSummary.Text =
            $"{options.ModeName} aplicado com sucesso.\n\n" +
            $"Aplicados: {summary.Applied}   •   Já ativos: {summary.Skipped}   •   Erros: {summary.Failed}";
        Motion.ShowOverlay(RebootOverlay);
    }

    private void SetBusy(bool busy)
    {
        ApplyButton.IsEnabled = !busy;
        Motion.SetVisible(BusyRing, busy);
        Motion.SetVisible(BusyBar, busy);
    }

    private void RebootLater_Click(object sender, RoutedEventArgs e)
        => Motion.HideOverlay(RebootOverlay);

    private void RebootNow_Click(object sender, RoutedEventArgs e)
    {
        Motion.HideOverlay(RebootOverlay);
        AppServices.Optimizer.Reboot();
    }

    private OptimizerOptions BuildOptions()
    {
        var compactTag = (CompactCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "skip";
        var storeTag = (StoreCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "keep";

        return new OptimizerOptions
        {
            Mode = ModeAdvanced.IsChecked == true ? ActiveMode.Advanced
                : ModeMedium.IsChecked == true ? ActiveMode.Medium
                : ActiveMode.Standard,

            Compact = compactTag switch
            {
                "never" => CompactMode.Never,
                "always" => CompactMode.Always,
                _ => CompactMode.Skip
            },

            Update = UpdateKeepSwitch.IsChecked == true ? UpdateChoice.KeepActive : UpdateChoice.Disable,

            Store = storeTag switch
            {
                "remove" => StoreChoice.RemoveApp,
                "services" => StoreChoice.ServicesOnly,
                _ => StoreChoice.Keep
            },

            DisableBluetooth = BluetoothKeepSwitch.IsChecked != true,

            MenuShowDelayMs = (int)Math.Round(MenuDelaySlider.Value)
        };
    }

    private static string CompactLabel(CompactMode mode) => mode switch
    {
        CompactMode.Never => "descomprimir (never)",
        CompactMode.Always => "comprimir (always)",
        _ => "manter atual"
    };

    private static string StoreLabel(StoreChoice choice) => choice switch
    {
        StoreChoice.RemoveApp => "remover aplicativo + desativar serviços",
        StoreChoice.ServicesOnly => "apenas desativar serviços em segundo plano",
        _ => "manter Microsoft Store intacta"
    };
}
