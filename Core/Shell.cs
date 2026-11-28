using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace KlyraOptimizer.Core;

/// <summary>
/// Execucao de processos externos (winget, powershell, bcdedit, sc...) com
/// captura de saida em tempo real e sem janelas pretas piscando.
/// </summary>
public static class Shell
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool IsAdmin { get; } = CheckAdmin();

    private static bool CheckAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static ProcessStartInfo CreateHidden(string fileName, string arguments)
    {
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = Environment.SystemDirectory
        };
    }

    /// <summary>Executa e devolve a saida completa (stdout + stderr).</summary>
    public static string Run(string fileName, string arguments, int timeoutMs = 120000)
        => RunCapture(fileName, arguments, timeoutMs).Output;

    /// <summary>Executa e devolve codigo de saida + texto.</summary>
    public static (int Code, string Output) RunCapture(string fileName, string arguments, int timeoutMs = 120000, bool mergeStderr = true)
    {
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var code = -1;
        try
        {
            using var process = new Process { StartInfo = CreateHidden(fileName, arguments) };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (errors) errors.AppendLine(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
            }
            else
            {
                code = process.ExitCode;
            }
        }
        catch (Exception ex)
        {
            lock (errors) errors.AppendLine(ex.Message);
        }

        if (mergeStderr && errors.Length > 0)
        {
            lock (output) output.Append(errors);
        }

        return (code, output.ToString());
    }

    /// <summary>Executa sem bloquear a interface, repassando cada linha.</summary>
    public static async Task<int> RunAsync(string fileName, string arguments, Action<string>? onLine = null, CancellationToken ct = default)
    {
        try
        {
            using var process = new Process { StartInfo = CreateHidden(fileName, arguments) };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            onLine?.Invoke(ex.Message);
            return -1;
        }
    }

    /// <summary>Executa um script PowerShell sem problemas de aspas (EncodedCommand).</summary>
    public static Task<int> RunPowerShellAsync(string script, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var wrapped = "$ProgressPreference='SilentlyContinue';" +
                      "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{};" +
                      script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", onLine, ct);
    }

    /// <summary>Executa um script PowerShell e devolve toda a saida (somente stdout).</summary>
    public static string RunPowerShellCapture(string script, int timeoutMs = 60000)
    {
        var wrapped = "$ProgressPreference='SilentlyContinue';" +
                      "try{[Console]::OutputEncoding=[System.Text.Encoding]::UTF8}catch{};" +
                      script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunCapture(
            "powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            timeoutMs,
            mergeStderr: false).Output;
    }

    public static void Open(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
        }
        catch { }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Reinicia o aplicativo pedindo elevacao (UAC).</summary>
    public static bool RelaunchElevated(string? arguments = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Shutdown()
    {
        try { App.ExitCompletely(); } catch { }
    }

    /// <summary>
    /// Verifica privilegios e, se necessario, oferece reiniciar elevado.
    /// Devolve false quando a acao deve ser interrompida.
    /// </summary>
    public static async Task<bool> RequireAdminAsync()
    {
        if (IsAdmin) return true;

        var elevate = await AppServices.ShowDialogAsync(
            "Privilégios de administrador necessários",
            "Esta operação modifica chaves de registro, serviços ou configurações de boot do Windows e exige execução como Administrador.\n\n" +
            "Deseja reiniciar o Klyra Optimizer com privilégios elevados agora?",
            primaryButtonText: "Reiniciar como Administrador",
            closeButtonText: "Cancelar");

        if (elevate && RelaunchElevated("--elevated-launch"))
        {
            Shutdown();
        }

        return false;
    }

    public static bool RequireAdmin()
    {
        if (IsAdmin) return true;

        var answer = MessageBox.Show(
            "Esta ação altera o registro, serviços e configurações do Windows e exige privilégios de administrador.\n\n" +
            "Deseja reiniciar o Klyra Optimizer como administrador agora?",
            "Klyra Optimizer",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Yes && RelaunchElevated("--elevated-launch"))
        {
            Shutdown();
        }

        return false;
    }
}
