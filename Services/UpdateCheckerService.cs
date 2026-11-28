using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace KlyraOptimizer.Services;

/// <summary>
/// Servico simples e leve de verificacao de atualizacoes via GitHub Releases
/// usando apenas bibliotecas nativas (.NET System.Net.Http e System.Text.Json).
/// </summary>
public static class UpdateCheckerService
{
    private const string LatestReleaseEndpoint =
        "https://api.github.com/repos/KlyraOptimizer/KlyraOptimizer/releases/latest";

    private static readonly HttpClient Http = CreateHttpClient();
    private static bool _alreadyChecked;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("KlyraOptimizer-WPF-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static async Task CheckForUpdatesAsync(Window? owner = null)
    {
        if (_alreadyChecked) return;
        _alreadyChecked = true;

        try
        {
            using var response = await Http.GetAsync(LatestReleaseEndpoint, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagProp) || tagProp.ValueKind != JsonValueKind.String)
                return;

            var rawTag = tagProp.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawTag)) return;

            var cleanTag = rawTag.TrimStart('v', 'V').Split('-', '+')[0].Trim();
            if (!Version.TryParse(cleanTag, out var parsedRemote)) return;

            var currentAssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

            var remoteNormalized = NormalizeVersion(parsedRemote);
            var localNormalized = NormalizeVersion(currentAssemblyVersion);

            if (remoteNormalized <= localNormalized) return;

            var downloadUrl = ExtractDownloadUrl(root);
            if (string.IsNullOrWhiteSpace(downloadUrl)) return;

            var localLabel = FormatShortVersion(currentAssemblyVersion);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var message =
                    $"Uma nova versão do Klyra Optimizer (v{cleanTag}) está disponível!\n\n" +
                    $"Versão atual: v{localLabel}\n" +
                    $"Nova versão: v{cleanTag}\n\n" +
                    "Deseja baixar a nova versão agora?";

                var result = owner != null && owner.IsVisible
                    ? MessageBox.Show(owner, message, "Atualização disponível — Klyra Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Information)
                    : MessageBox.Show(message, "Atualização disponível — Klyra Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = downloadUrl,
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // falha ao abrir navegador ignorada silenciosamente
                    }
                }
            });
        }
        catch
        {
            // Erros de rede, falta de internet, timeout ou rate limit da API do GitHub
            // sao ignorados silenciosamente sem interromper a experiencia do usuario.
        }
    }

    private static string? ExtractDownloadUrl(JsonElement root)
    {
        string? exeUrl = null;
        string? zipUrl = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String
                    ? u.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? url
                    : url;

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl ??= url;
                }
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl ??= url;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(exeUrl)) return exeUrl;
        if (!string.IsNullOrWhiteSpace(zipUrl)) return zipUrl;

        return root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
            ? html.GetString()
            : null;
    }

    private static Version NormalizeVersion(Version v)
    {
        return new Version(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build),
            Math.Max(0, v.Revision));
    }

    private static string FormatShortVersion(Version v)
    {
        var build = Math.Max(0, v.Build);
        return v.Revision > 0
            ? $"{v.Major}.{v.Minor}.{build}.{v.Revision}"
            : $"{v.Major}.{v.Minor}.{build}";
    }
}
