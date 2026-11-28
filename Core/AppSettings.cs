using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace KlyraOptimizer.Core;

/// <summary>
/// Preferencias persistentes do Klyra Optimizer:
/// - StartWithWindows ("Iniciar junto com o sistema", ativado por padrao);
/// - CloseToTray ("Fechar e ficar na bandeja do sistema", ativado por padrao);
/// - Theme (modo de tema persistido entre sessoes).
/// </summary>
public sealed class AppSettings
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "KlyraOptimizer";
    private const string ScheduledTaskName = "KlyraOptimizer_Startup";

    public bool StartWithWindows { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public static AppSettings Current { get; private set; } = new();

    private static string PrimaryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KlyraOptimizer", "settings.json");

    private static string UserFallbackPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KlyraOptimizer", "settings.json");

    public static void LoadAndSync(bool isAutomatedTest = false)
    {
        var loaded = TryRead(UserFallbackPath) ?? TryRead(PrimaryPath);
        if (loaded != null)
        {
            Current = loaded;
        }
        else
        {
            Current = new AppSettings
            {
                StartWithWindows = true,
                CloseToTray = true,
                Theme = ThemeMode.System
            };

            if (!isAutomatedTest)
            {
                Current.Save();
            }
        }

        if (!isAutomatedTest)
        {
            Current.SyncStartupRegistration();
        }
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        try
        {
            var dir = Path.GetDirectoryName(UserFallbackPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(UserFallbackPath, json);
        }
        catch { }

        try
        {
            var dir = Path.GetDirectoryName(PrimaryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(PrimaryPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Registra ou remove a inicializacao automatica com o Windows.
    /// Quando rodando como Administrador, cria uma Tarefa Agendada (ONLOGON / HIGHEST) para iniciar
    /// elevado sem prompt UAC no boot; caso contrario, usa HKCU\...\Run.
    /// </summary>
    public void SyncStartupRegistration()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            if (StartWithWindows)
            {
                var command = $"\"{exe}\" --autostart";

                if (Shell.IsAdmin)
                {
                    var (code, _) = Shell.RunCapture(
                        "schtasks.exe",
                        $"/Create /TN \"{ScheduledTaskName}\" /TR \"\\\"{exe}\\\" --autostart\" /SC ONLOGON /RL HIGHEST /F",
                        15000);

                    if (code == 0)
                    {
                        // Evita duplicidade caso ja houvesse entrada em HKCU\Run
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true);
                        runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                        return;
                    }
                }

                using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
                key?.SetValue(RunValueName, command, RegistryValueKind.String);
            }
            else
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true))
                {
                    key?.DeleteValue(RunValueName, throwOnMissingValue: false);
                }

                if (Shell.IsAdmin)
                {
                    Shell.RunCapture("schtasks.exe", $"/Delete /TN \"{ScheduledTaskName}\" /F", 10000);
                }
            }
        }
        catch (Exception ex)
        {
            AppServices.Log?.Write($"[CONFIG] Aviso ao sincronizar inicialização com o Windows: {ex.Message}", LogKind.Warning);
        }
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch
        {
            return null;
        }
    }
}

