using System.Windows;
using System.Windows.Controls;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Views;

public sealed class RestorePointRow
{
    public int Sequence { get; init; }

    public string SequenceLabel => $"#{Sequence}";

    public string Description { get; init; } = string.Empty;

    public string Created { get; init; } = string.Empty;
}

public partial class RestorePage : UserControl, IRefreshablePage
{
    private Action? _confirmAction;
    private bool _busy;

    public RestorePage()
    {
        InitializeComponent();
    }

    public async void OnActivated()
    {
        await RefreshAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Motion.SetVisible(BusyRing, busy);
        Motion.SetVisible(BusyBar, busy);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true);

        try
        {
            await Task.Run(() => AppServices.System.Refresh());

            var enabled = AppServices.System.RestoreProtectionEnabled;
            ProtectionTitle.Text = enabled ? "Proteção do Sistema ativa" : "Proteção do Sistema desativada";
            ProtectionText.Text = enabled
                ? "Pontos de restauração podem ser criados e o Windows está pronto para reverter mudanças."
                : "Ative a proteção para que o Windows possa criar pontos antes de otimizar.";
            ProtectionGlyph.Text = enabled ? "\uE930" : "\uE7BA";
            ProtectionGlyph.Foreground = enabled
                ? (System.Windows.Media.Brush)FindResource("App.SuccessText")
                : (System.Windows.Media.Brush)FindResource("App.Warning");

            var points = await AppServices.Restore.GetPointsAsync();
            PointsList.ItemsSource = points
                .Select(p => new RestorePointRow { Sequence = p.Sequence, Description = p.Description, Created = p.Created })
                .ToList();

            NoPoints.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NoPoints.Text = Shell.IsAdmin
                ? "Nenhum ponto de restauração encontrado."
                : "Reinicie como administrador para listar os pontos de restauração.";
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[RESTAURAÇÃO] Falha ao atualizar: {ex.Message}", LogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshPoints_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void EnableProtection_Click(object sender, RoutedEventArgs e)
    {
        if (!await Shell.RequireAdminAsync()) return;

        SetBusy(true);
        ProtectionText.Text = "Ativando Proteção do Sistema em segundo plano...";
        await Task.Run(() => AppServices.Restore.EnsureProtection());
        SetBusy(false);
        await RefreshAsync();
    }

    private async void CreatePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!await Shell.RequireAdminAsync()) return;

        ShowConfirm(
            "Criar ponto de restauração",
            "Um ponto chamado \"Klyra Optimizer - manual\" será criado agora em segundo plano.\n\n" +
            "Isso pode levar alguns segundos.",
            async () =>
            {
                SetBusy(true);
                ProtectionText.Text = "Criando ponto de restauração...";
                await AppServices.Restore.CreatePointAsync("Klyra Optimizer - manual");
                SetBusy(false);
                await RefreshAsync();
            });
    }

    private async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RestorePointRow row }) return;
        if (!await Shell.RequireAdminAsync()) return;

        ShowConfirm(
            "Restaurar o sistema",
            $"O Windows será restaurado para o ponto #{row.Sequence} — {row.Description} ({row.Created}).\n\n" +
            "O computador será reiniciado automaticamente para concluir.",
            async () =>
            {
                SetBusy(true);
                await AppServices.Restore.RestoreToPointAsync(row.Sequence);
                SetBusy(false);
            });
    }

    private async void RevertAll_Click(object sender, RoutedEventArgs e)
    {
        if (!await Shell.RequireAdminAsync()) return;

        ShowConfirm(
            "Reverter todas as otimizações",
            "Todas as chaves, serviços, valores de BCD e o MSI Mode das GPUs voltarão ao padrão do Windows.\n\n" +
            "Recomendamos reiniciar o computador ao concluir.",
            async () =>
            {
                SetBusy(true);
                RevertDetailText.Text = "Revertendo todas as camadas para o padrão do Windows...";
                await Task.Run(() => AppServices.Optimizer.RevertAll());
                AppServices.RefreshSystem();
                SetBusy(false);
                RevertDetailText.Text = "Reversão concluída. Reinicie o computador para finalizar.";
                await RefreshAsync();
            });
    }

    private void OpenWizard_Click(object sender, RoutedEventArgs e) => AppServices.Restore.OpenRestoreWizard();

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
}
