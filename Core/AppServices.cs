using KlyraOptimizer.Services;

namespace KlyraOptimizer.Core;

/// <summary>Container de servicos compartilhados pela interface.</summary>
public static class AppServices
{
    public static AppLog Log { get; private set; } = null!;

    public static TweakEngine Engine { get; private set; } = null!;

    public static SystemInfo System { get; private set; } = null!;

    public static OptimizationService Optimizer { get; private set; } = null!;

    public static StoreService Store { get; private set; } = null!;

    public static WingetService Winget { get; private set; } = null!;

    public static RestoreService Restore { get; private set; } = null!;

    public static GpuMsiService Gpu { get; private set; } = null!;

    public static TrayIconService? Tray { get; set; }

    public const string AppName = "Klyra Optimizer";

    public const string Version = "1.6.2";

    /// <summary>Pedido de navegacao vindo de qualquer ponto do app.</summary>
    public static Action<string>? NavigationRequested { get; set; }

    /// <summary>Exibe um ContentDialog modal nativo do Windows 10 na MainWindow.</summary>
    public static Func<string, string, string, string?, bool, Task<bool>>? DialogRequested { get; set; }

    public static Task<bool> ShowDialogAsync(
        string title,
        string content,
        string primaryButtonText = "OK",
        string? closeButtonText = null,
        bool isDanger = false)
    {
        if (DialogRequested != null)
        {
            return DialogRequested(title, content, primaryButtonText, closeButtonText, isDanger);
        }
        return Task.FromResult(false);
    }

    public static event Action? StateChanged;

    public static void Initialize()
    {
        Log = new AppLog();
        Engine = new TweakEngine(Log);
        System = new SystemInfo();
        System.Refresh();
        Gpu = new GpuMsiService(Log);
        Optimizer = new OptimizationService(Log, Engine, System, Gpu);
        Store = new StoreService(Log);
        Winget = new WingetService(Log);
        Restore = new RestoreService(Log, System);

        Log.Write($"=== {AppName} V{Version} iniciado ===", LogKind.Phase);
        Log.Write($"   Sistema: {System.OsLabel} ({System.FullVersion}) • Administrador: {(Shell.IsAdmin ? "sim" : "nao")}", LogKind.Info);
        Log.Write($"   Log desta sessão: {Log.FilePath}", LogKind.Info);
    }

    public static void NotifyStateChanged() => Ui.Post(() => StateChanged?.Invoke());

    public static void RefreshSystem()
    {
        System.Refresh();
        NotifyStateChanged();
    }

    public static void Navigate(string key) => NavigationRequested?.Invoke(key);
}
