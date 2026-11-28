using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using KlyraOptimizer.Core;

namespace KlyraOptimizer;

public partial class App : Application
{
    private const string SingleInstanceEventName = @"Local\KlyraOptimizer_Wakeup_Event";

    private string? _smokeResultFile;
    private bool _smoke;
    private EventWaitHandle? _wakeupEvent;
    private CancellationTokenSource? _wakeupCts;

    public static bool IsExiting { get; private set; }
    public static bool IsSmokeTest { get; private set; }

    private static readonly string TraceFile =
        Path.Combine(Path.GetTempPath(), "klyraoptimizer-startup.txt");

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(TraceFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void ExitCompletely()
    {
        IsExiting = true;
        try
        {
            AppServices.Tray?.Dispose();
            AppServices.Tray = null;
        }
        catch { }

        Current?.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try { File.Delete(TraceFile); } catch { }
        Trace($"Iniciando - argumentos: {string.Join(' ', e.Args)}");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Trace("FATAL: " + args.ExceptionObject);

        _smoke = e.Args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
        IsSmokeTest = _smoke;
        if (_smoke)
        {
            var index = Array.FindIndex(e.Args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            _smokeResultFile = index >= 0 && index + 1 < e.Args.Length
                ? e.Args[index + 1]
                : Path.Combine(Path.GetTempPath(), "klyraoptimizer-smoke.txt");
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Teste de sanidade do motor de ajustes (nao altera configuracoes reais)
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var index = Array.FindIndex(e.Args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
            var file = index >= 0 && index + 1 < e.Args.Length
                ? e.Args[index + 1]
                : Path.Combine(Path.GetTempPath(), "klyraoptimizer-selftest.txt");

            var ok = SelfTest.Run(file);
            Shutdown(ok ? 0 : 3);
            return;
        }

        var alreadyElevatedLaunch = e.Args.Any(a => a.Equals("--elevated-launch", StringComparison.OrdinalIgnoreCase));
        var noElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));
        var isAutoStart = e.Args.Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        // Instancia unica: se ja estiver aberta na bandeja, apenas acorda a janela principal
        if (!_smoke && !alreadyElevatedLaunch)
        {
            try
            {
                _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceEventName, out var createdNew);
                if (!createdNew)
                {
                    _wakeupEvent.Set();
                    Shutdown(0);
                    return;
                }
            }
            catch { }
        }

        // Elevacao automatica (exceto no boot silencioso sem admin para nao travar o logon com UAC)
        if (!Shell.IsAdmin && !_smoke && !alreadyElevatedLaunch && !noElevate && !isAutoStart)
        {
            Trace("Solicitando elevacao...");
            _wakeupEvent?.Dispose();
            _wakeupEvent = null;

            if (Shell.RelaunchElevated("--elevated-launch"))
            {
                Trace("Processo elevado iniciado - encerrando instancia atual.");
                IsExiting = true;
                Shutdown();
                return;
            }

            Trace("Elevacao recusada - seguindo em modo limitado.");
        }

        if (_wakeupEvent == null && !_smoke)
        {
            try
            {
                _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceEventName, out _);
            }
            catch { }
        }

        try
        {
            AppServices.Initialize();
            AppSettings.LoadAndSync(isAutomatedTest: _smoke);
            Trace("Servicos e configuracoes inicializados.");
        }
        catch (Exception ex)
        {
            Trace("FALHA ao inicializar: " + ex);
            Fail("Não foi possível iniciar o Klyra Optimizer.\n\n" + ex.Message);
            return;
        }

        if (!AppServices.System.IsSupported)
        {
            Fail(
                $"O Klyra Optimizer requer Windows 10 21H2 (build 19044) ou superior.\n\n" +
                $"Este computador está com {AppServices.System.OsLabel} ({AppServices.System.FullVersion}).",
                shutdownCode: 2);
            return;
        }

        var themeArg = ParseThemeArgument(e.Args);
        ThemeService.Initialize(themeArg ?? AppSettings.Current.Theme);
        Trace("Tema aplicado.");

        MainWindow? mainWindow = null;
        try
        {
            mainWindow = new MainWindow();
            MainWindow = mainWindow;

            if (!_smoke)
            {
                var tray = new TrayIconService(
                    onOpenMainWindow: () => mainWindow.RestoreFromTray(),
                    onExitApplication: ExitCompletely);
                AppServices.Tray = tray;

                if (AppSettings.Current.CloseToTray || isAutoStart)
                {
                    tray.EnsureVisible();
                }

                StartWakeupListener(mainWindow);
            }

            if (isAutoStart && AppSettings.Current.CloseToTray && !_smoke)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Trace("Iniciado silenciosamente na bandeja do sistema (--autostart).");
            }
            else
            {
                mainWindow.Show();
                Trace("Janela principal exibida.");
            }
        }
        catch (Exception ex)
        {
            Trace("FALHA ao abrir a janela: " + ex);
            Fail("Não foi possível abrir a interface.\n\n" + ex.Message);
            return;
        }

        if (_smoke && mainWindow != null)
        {
            mainWindow.RunSmokeTest(_smokeResultFile!);
        }
    }

    private void StartWakeupListener(MainWindow mainWindow)
    {
        if (_wakeupEvent == null) return;
        var evt = _wakeupEvent;
        _wakeupCts = new CancellationTokenSource();
        var ct = _wakeupCts.Token;

        Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (evt.WaitOne(1000))
                    {
                        Dispatcher.BeginInvoke(new Action(mainWindow.RestoreFromTray));
                    }
                }
                catch
                {
                    break;
                }
            }
        }, ct);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _wakeupCts?.Cancel(); } catch { }
        try { _wakeupEvent?.Dispose(); } catch { }
        try { AppServices.Tray?.Dispose(); } catch { }
        base.OnExit(e);
    }

    private static ThemeMode? ParseThemeArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;

        return args[index + 1].ToLowerInvariant() switch
        {
            "light" or "claro" => ThemeMode.Light,
            "dark" or "escuro" => ThemeMode.Dark,
            _ => ThemeMode.System
        };
    }

    private void Fail(string message, int shutdownCode = 1)
    {
        Trace("FAIL: " + message);
        IsExiting = true;

        if (!_smoke)
        {
            MessageBox.Show(message, AppServices.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        Shutdown(shutdownCode);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace("ERRO NAO TRATADO: " + e.Exception);

        try
        {
            AppServices.Log?.Write($"ERRO NAO TRATADO: {e.Exception}", LogKind.Failed);
        }
        catch { }

        if (!_smoke)
        {
            MessageBox.Show(
                "Ocorreu um erro inesperado:\n\n" + e.Exception.Message +
                "\n\nO erro foi registrado em:\n" + (AppServices.Log?.FilePath ?? "—"),
                AppServices.AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        e.Handled = true;
    }
}
