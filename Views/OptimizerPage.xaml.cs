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

        var restoreLine = options.CreateRestorePoint
            ? "Ponto de restauração: será verificado/criado antes de aplicar."
            : "Ponto de restauração: desativado nesta execução.";

        ConfirmSummary.Text =
            $"Modo selecionado: {options.ModeName}\n" +
            $"CompactOS: {CompactLabel(options.Compact)}\n" +
            $"Windows Update: {(options.Update == UpdateChoice.Disable ? "desativar" : "manter ativo com proteção de drivers")}\n" +
            $"Microsoft Store: {StoreLabel(options.Store)}\n" +
            $"Bluetooth: {(options.DisableBluetooth ? "desativar serviços" : "manter ativo")}\n" +
            restoreLine;

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

        if (!Shell.RequireAdmin()) return;

        await RunOptimizationAsync(options);
    }

    private async Task RunOptimizationAsync(OptimizerOptions options)
    {
        SetBusy(true);
        StatusText.Text = $"Aplicando {options.ModeName} em segundo plano...";

        OptimizerSummary summary;

        try
        {
            if (options.CreateRestorePoint)
            {
                StatusText.Text = "Verificando Proteção do Sistema e criando Ponto de Restauração...";
                await Task.Run(() => AppServices.Restore.EnsureProtection());
                await AppServices.Restore.CreatePointAsync($"Klyra Optimizer - {options.Mode}");
            }

            summary = await Task.Run(() => AppServices.Optimizer.Apply(options, CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"   [ERRO FATAL] {ex.Message}", LogKind.Failed);
            SetBusy(false);
            StatusText.Text = "Falha durante a otimização: " + ex.Message;
            return;
        }

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
        return new OptimizerOptions
        {
            Mode = ModeAdvanced.IsChecked == true ? ActiveMode.Advanced
                : ModeMedium.IsChecked == true ? ActiveMode.Medium
                : ActiveMode.Standard,

            Compact = CompactNever.IsChecked == true ? CompactMode.Never
                : CompactAlways.IsChecked == true ? CompactMode.Always
                : CompactMode.Skip,

            Update = UpdateDisable.IsChecked == true ? UpdateChoice.Disable : UpdateChoice.KeepActive,

            Store = StoreRemove.IsChecked == true ? StoreChoice.RemoveApp
                : StoreServices.IsChecked == true ? StoreChoice.ServicesOnly
                : StoreChoice.Keep,

            DisableBluetooth = BluetoothDisable.IsChecked == true,

            CreateRestorePoint = RestoreCheck.IsChecked == true
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
