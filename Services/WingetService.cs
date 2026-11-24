using System.IO;
using System.Net.Http;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Services;

/// <summary>
/// Instalacao via winget (fonte msstore) usando o Product ID obtido nas APIs
/// da Store, com verificacao e instalacao automatica da dependencia winget.
/// </summary>
public sealed class WingetService
{
    private readonly AppLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly (string Name, string Url)[] Prerequisites =
    {
        ("Microsoft.VCLibs.140.00.UWPDesktop", "https://aka.ms/Microsoft.VCLibs.x64.14.00.Desktop.appx"),
        ("Microsoft.UI.Xaml.2.8", "https://aka.ms/Microsoft.UI.Xaml.2.8.appx"),
        ("App Installer (winget)", "https://aka.ms/getwinget")
    };

    public WingetService(AppLog log) => _log = log;

    public static bool IsInstalled => SystemInfo.FindWinget() != null;

    public event Action<string>? Progress;

    /// <summary>Publica o andamento na thread da interface (instalacao roda em segundo plano).</summary>
    private void RaiseProgress(string message)
    {
        var handler = Progress;
        if (handler == null) return;

        Core.Ui.Post(() => handler(message));
    }

    public async Task<bool> InstallFromStoreAsync(string productId, CancellationToken ct)
        => await InstallAsync(productId, "msstore", ct).ConfigureAwait(false);

    public async Task<bool> InstallAsync(string id, string? source, CancellationToken ct)
    {
        var winget = SystemInfo.FindWinget();
        if (winget == null)
        {
            _log.Write("   [ERRO] winget não esta instalado. Use o botao 'Instalar winget'.", LogKind.Failed);
            return false;
        }

        var arguments = $"install --id \"{id}\" --accept-package-agreements --accept-source-agreements";
        if (!string.IsNullOrWhiteSpace(source))
        {
            arguments += $" --source {source}";
        }

        _log.Write($"   [COMANDO] winget {arguments}", LogKind.Command);

        var alreadyInstalled = false;
        var code = await Shell.RunAsync(
            winget,
            arguments,
            line =>
            {
                var text = line.Trim();
                if (text.Length == 0) return;
                if (text.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ja instalado", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("já instalado", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyInstalled = true;
                }

                _log.Write($"   {text}", LogKind.Info);
            },
            ct).ConfigureAwait(false);

        var success = code == 0 || alreadyInstalled;

        if (success)
        {
            _log.Write($"   [OK] {id} instalado com sucesso via winget ({source ?? "winget"}).", LogKind.Applied);
        }
        else
        {
            _log.Write($"   [ERRO] winget retornou o código {code} para {id}.", LogKind.Failed);
        }

        return success;
    }

    /// <summary>Garante o winget: baixa e instala o App Installer + dependencias.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct)
    {
        if (IsInstalled)
        {
            RaiseProgress("winget ja esta instalado.");
            return true;
        }

        var folder = Path.Combine(Path.GetTempPath(), "KlyraOptimizer-prereq");
        Directory.CreateDirectory(folder);

        foreach (var (name, url) in Prerequisites)
        {
            ct.ThrowIfCancellationRequested();

            var extension = url.EndsWith(".appx", StringComparison.OrdinalIgnoreCase) ? ".appx" : ".msixbundle";
            var file = Path.Combine(folder, name.Replace(' ', '_').Replace("(", "").Replace(")", "") + extension);

            try
            {
                RaiseProgress($"Baixando {name}...");
                await DownloadAsync(url, file, ct).ConfigureAwait(false);

                RaiseProgress($"Instalando {name}...");
                await Shell.RunPowerShellAsync(
                    $"Add-AppxPackage -Path '{file}' -ForceApplicationShutdown -ErrorAction Stop; 'instalado'",
                    line => _log.Write($"   {line}", LogKind.Info),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Write($"   [AVISO] {name}: {ex.Message}", LogKind.Warning);
            }
        }

        if (IsInstalled)
        {
            _log.Write("   [OK] winget (App Installer) instalado com sucesso.", LogKind.Applied);
            RaiseProgress("winget instalado com sucesso.");
            return true;
        }

        _log.Write(
            "   [ERRO] Não foi possível instalar o winget automaticamente. Instale o 'App Installer' pela Microsoft Store.",
            LogKind.Failed);
        RaiseProgress("Falha ao instalar o winget.");
        return false;
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var local = File.Create(destination);
        await remote.CopyToAsync(local, ct).ConfigureAwait(false);
    }
}
