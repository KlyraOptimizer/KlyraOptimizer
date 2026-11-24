using System.IO;
using System.Windows;
using System.Windows.Threading;
using KlyraOptimizer.Core;

namespace KlyraOptimizer;

public partial class App : Application
{
    private string? _smokeResultFile;
    private bool _smoke;

    private static readonly string TraceFile =
        Path.Combine(Path.GetTempPath(), "klyraoptimizer-startup.txt");

    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(TraceFile, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // diagnostico nunca derruba o app
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            File.Delete(TraceFile);
        }
        catch { }

        Trace($"Iniciando - argumentos: {string.Join(' ', e.Args)}");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Trace("FATAL: " + args.ExceptionObject);

        _smoke = e.Args.Any(a => a.Equals("--smoke", StringComparison.OrdinalIgnoreCase));
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

            var ok = Core.SelfTest.Run(file);
            Shutdown(ok ? 0 : 3);
            return;
        }

        // Elevacao automatica
        var alreadyElevatedLaunch = e.Args.Any(a => a.Equals("--elevated-launch", StringComparison.OrdinalIgnoreCase));
        var noElevate = e.Args.Any(a => a.Equals("--no-elevate", StringComparison.OrdinalIgnoreCase));

        if (!Shell.IsAdmin && !_smoke && !alreadyElevatedLaunch && !noElevate)
        {
            Trace("Solicitando elevacao...");
            if (Shell.RelaunchElevated("--elevated-launch"))
            {
                Trace("Processo elevado iniciado - encerrando instancia atual.");
                Shutdown();
                return;
            }

            Trace("Elevacao recusada - seguindo em modo limitado.");
        }

        try
        {
            AppServices.Initialize();
            Trace("Servicos inicializados.");
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

        ThemeService.Initialize(ParseThemeArgument(e.Args));
        Trace("Tema aplicado.");

        MainWindow? mainWindow = null;
        try
        {
            mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
            Trace("Janela principal exibida.");
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

    private static ThemeMode ParseThemeArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return ThemeMode.System;

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
