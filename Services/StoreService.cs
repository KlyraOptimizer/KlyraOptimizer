using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KlyraOptimizer.Core;

namespace KlyraOptimizer.Services;

public sealed class StoreApp : INotifyPropertyChanged
{
    private BitmapImage? _logo;
    private SolidColorBrush? _tileBackground;
    private double _rating;
    private int _ratingCount;
    private string _category = string.Empty;
    private string _priceLabel = "Gratuito";

    public string ProductId { get; init; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Publisher { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    public string? HeroUrl { get; set; }

    public string? TileColorHex { get; set; }

    public string? PackageFamilyName { get; set; }

    public string Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            Notify(nameof(Category));
        }
    }

    public double Rating
    {
        get => _rating;
        set
        {
            if (Math.Abs(_rating - value) < 0.01) return;
            _rating = value;
            Notify(nameof(Rating));
            Notify(nameof(RatingText));
            Notify(nameof(StarsText));
            Notify(nameof(HasRating));
        }
    }

    public int RatingCount
    {
        get => _ratingCount;
        set
        {
            if (_ratingCount == value) return;
            _ratingCount = value;
            Notify(nameof(RatingCount));
            Notify(nameof(RatingCountText));
        }
    }

    public string PriceLabel
    {
        get => _priceLabel;
        set
        {
            if (_priceLabel == value) return;
            _priceLabel = value;
            Notify(nameof(PriceLabel));
        }
    }

    public BitmapImage? Logo
    {
        get => _logo;
        set
        {
            if (_logo == value) return;
            _logo = value;
            Notify(nameof(Logo));
            Notify(nameof(HasLogo));
        }
    }

    public bool HasLogo => _logo != null;

    public SolidColorBrush? TileBackground
    {
        get => _tileBackground;
        set
        {
            if (_tileBackground == value) return;
            _tileBackground = value;
            Notify(nameof(TileBackground));
        }
    }

    public bool HasRating => _rating > 0.05;

    public string RatingText => _rating > 0.05 ? _rating.ToString("0.0", new CultureInfo("pt-BR")) : "—";

    public string StarsText
    {
        get
        {
            if (_rating <= 0.05) return "☆☆☆☆☆";
            var full = (int)Math.Round(Math.Clamp(_rating, 0, 5));
            return new string('★', full) + new string('☆', 5 - full);
        }
    }

    public string RatingCountText => _ratingCount switch
    {
        >= 1_000_000 => $"{_ratingCount / 1_000_000.0:0.0} mi",
        >= 1_000 => $"{_ratingCount / 1_000.0:0.#} mil",
        > 0 => _ratingCount.ToString("N0", new CultureInfo("pt-BR")),
        _ => string.Empty
    };

    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Notify(string name) => Ui.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}

public sealed class StoreDetails
{
    public string Description { get; set; } = string.Empty;

    public string? ShortDescription { get; set; }

    public string? Publisher { get; set; }

    public string? Category { get; set; }

    public string? LogoUrl { get; set; }

    public string? HeroUrl { get; set; }

    public string? TileColorHex { get; set; }

    public string? PackageFamilyName { get; set; }

    public string? Version { get; set; }

    public string? SizeLabel { get; set; }

    public string? ReleaseYear { get; set; }

    public double Rating { get; set; }

    public int RatingCount { get; set; }

    public List<string> Screenshots { get; } = new();
}

/// <summary>
/// Catalogo de aplicativos da Microsoft Store (estilo Windows 10 Store).
/// Combina StoreEdgeFD manifestSearch, DisplayCatalog em lote (metadados,
/// avaliacoes, icones, arte hero e capturas de tela) e instalacao via winget.
/// </summary>
public sealed class StoreService
{
    private const string ManifestSearchUrl = "https://storeedgefd.dsx.mp.microsoft.com/v9.0/manifestSearch";
    private const string DisplayCatalogUrl = "https://displaycatalog.mp.microsoft.com/v7.0/products";
    private const string DisplayCatalogSearchUrl =
        "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/Apps/products";

    private static readonly Regex SearchTableRow = new(
        @"^(?<name>.+?)\s{2,}(?<id>[A-Za-z0-9]{9,20})\s{2,}(?<version>\S+)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FieldLine = new(
        @"^(?<key>[A-Za-zÀ-ÿ][A-Za-zÀ-ÿ ]{1,40}):\s?(?<value>.*)$",
        RegexOptions.Compiled);

    private readonly AppLog _log;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, StoreDetails> _detailsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vitrine curada da pagina inicial da Loja (estilo Windows 10 Store).</summary>
    public static readonly IReadOnlyList<string> EssentialIds = new[]
    {
        "9NCBCSZSJRSB", // Spotify
        "9NKSQGP7F2NH", // WhatsApp
        "9NZTWSQNTD0S", // Telegram Desktop
        "9WZDNCRFJ3TJ", // Netflix
        "9NBLGGH4VVNH", // VLC
        "9NZVDKPMR9RD", // Firefox
        "9NBLGGH5L9XT", // Instagram
        "9P6RC76MSMMJ", // Prime Video
        "9NXQXXLFST89", // Disney+
        "9NH2GPH4JZS4", // TikTok
        "9WZDNCRF0083", // Messenger
        "9PB2MZ1ZMB1S"  // iTunes
    };

    public static readonly IReadOnlyList<string> ProductivityAndDevIds = new[]
    {
        "9N0DX20HK701", // Windows Terminal
        "9NBHCS1LX4R0", // Paint.NET
        "9NBLGGH5R558", // Microsoft To Do
        "9WZDNCRFHVJL", // OneNote for Windows 10
        "9NBLGGH4QGHW", // Sticky Notes
        "9MSPC6MP8FM4", // Whiteboard
        "9WZDNCRFJ3PS", // Remote Desktop
        "9PDXGNCFSCZV", // Ubuntu
        "9NCVDN91XZQP", // Python 3.12
        "9NBLGGH33N0N", // WiFi Analyzer
        "9MV0B5HZVK9Z", // Xbox
        "9NBLGGGZM6WM"  // Roblox
    };

    public StoreService(AppLog log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    }

    /// <summary>
    /// Verifica silenciosamente se ha conexao ativa com a Internet (zero escrita em logs).
    /// </summary>
    public async Task<bool> CheckInternetSilentAsync(CancellationToken ct = default)
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            using var req = new HttpRequestMessage(HttpMethod.Get, "http://www.msftconnecttest.com/connecttest.txt");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 200 and < 500) return true;
        }
        catch
        {
            // tenta fallback direto no endpoint HTTPS da Store antes de declarar offline
        }

        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            using var req = new HttpRequestMessage(HttpMethod.Head, "https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds=9NCBCSZSJRSB&market=BR&languages=pt-br&fieldsTemplate=browse");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return (int)resp.StatusCode is >= 200 and < 500;
        }
        catch
        {
            return false;
        }
    }

    public async Task<BitmapImage?> LoadImageAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_imageCache.TryGetValue(url, out var cached)) return cached;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();

            _imageCache[url] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Carrega uma colecao de aplicativos ricos por ID em uma unica chamada DisplayCatalog.</summary>
    public async Task<List<StoreApp>> GetProductsByIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var apps = new List<StoreApp>();
        if (list.Count == 0) return apps;

        try
        {
            var url = $"{DisplayCatalogUrl}?bigIds={Uri.EscapeDataString(string.Join(',', list))}&market=BR&languages=pt-br,en-us&fieldsTemplate=browse";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return apps;
            }

            var byId = new Dictionary<string, StoreApp>(StringComparer.OrdinalIgnoreCase);

            foreach (var product in products.EnumerateArray())
            {
                var id = GetString(product, "ProductId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var details = ParseCatalogProduct(product, id!);
                _detailsCache[id!] = details;

                var app = new StoreApp
                {
                    ProductId = id!,
                    Title = ExtractTitle(product) ?? id!,
                    Publisher = details.Publisher ?? "Microsoft Corporation",
                    Category = TranslateCategory(details.Category),
                    LogoUrl = details.LogoUrl,
                    HeroUrl = details.HeroUrl,
                    TileColorHex = details.TileColorHex,
                    PackageFamilyName = details.PackageFamilyName,
                    Rating = details.Rating,
                    RatingCount = details.RatingCount,
                    Summary = details.ShortDescription ?? string.Empty
                };

                ApplyTileColor(app, details.TileColorHex);
                byId[id!] = app;
            }

            foreach (var id in list)
            {
                if (byId.TryGetValue(id, out var found))
                {
                    apps.Add(found);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NetworkInterface.GetIsNetworkAvailable() && ex is not HttpRequestException)
            {
                _log.Write($"   [AVISO] Vitrine DisplayCatalog: {ex.Message}", LogKind.Warning);
            }
        }

        return apps;
    }

    /// <summary>Enriquece resultados de busca com logos, avaliacoes e categorias em lote.</summary>
    public async Task EnrichAppsAsync(IReadOnlyList<StoreApp> apps, CancellationToken ct)
    {
        var storeIds = apps
            .Where(a => a.ProductId.Length is >= 10 and <= 16 && !a.ProductId.Contains('.'))
            .Take(24)
            .Select(a => a.ProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (storeIds.Count == 0) return;

        try
        {
            var url = $"{DisplayCatalogUrl}?bigIds={Uri.EscapeDataString(string.Join(',', storeIds))}&market=BR&languages=pt-br,en-us&fieldsTemplate=browse";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var lookup = apps.ToDictionary(a => a.ProductId, StringComparer.OrdinalIgnoreCase);

            foreach (var product in products.EnumerateArray())
            {
                var id = GetString(product, "ProductId");
                if (string.IsNullOrWhiteSpace(id) || !lookup.TryGetValue(id!, out var app)) continue;

                var details = ParseCatalogProduct(product, id!);
                _detailsCache[id!] = details;

                if (!string.IsNullOrWhiteSpace(details.Publisher)) app.Publisher = details.Publisher!;
                app.Category = TranslateCategory(details.Category);
                app.LogoUrl = details.LogoUrl;
                app.HeroUrl = details.HeroUrl;
                app.TileColorHex = details.TileColorHex;
                app.PackageFamilyName ??= details.PackageFamilyName;
                app.Rating = details.Rating;
                app.RatingCount = details.RatingCount;
                ApplyTileColor(app, details.TileColorHex);
                app.Notify(nameof(StoreApp.Publisher));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write($"   [AVISO] Enrich DisplayCatalog: {ex.Message}", LogKind.Warning);
        }
    }

    private static void ApplyTileColor(StoreApp app, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var normalized = hex.StartsWith('#') ? hex : "#" + hex;
            var color = (Color)ColorConverter.ConvertFromString(normalized);
            if (color.A < 40) return;
            var brush = new SolidColorBrush(Color.FromArgb(0xFF, color.R, color.G, color.B));
            brush.Freeze();
            app.TileBackground = brush;
        }
        catch { }
    }

    /// <summary>Busca em cascata: manifestSearch -> winget (msstore) -> DisplayCatalog.</summary>
    public async Task<List<StoreApp>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<StoreApp>();
        query = query.Trim();

        var results = await SearchManifestAsync(query, ct).ConfigureAwait(false);
        if (results.Count > 0) return results;

        results = await SearchWingetAsync(query, ct).ConfigureAwait(false);
        if (results.Count > 0) return results;

        return await SearchDisplayCatalogAsync(query, ct).ConfigureAwait(false);
    }

    private async Task<List<StoreApp>> SearchManifestAsync(string query, CancellationToken ct)
    {
        var results = new List<StoreApp>();

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                MaximumResults = 36,
                Query = new { KeyWord = query, MatchType = "Substring" }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ManifestSearchUrl, content, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in data.EnumerateArray())
            {
                var id = GetString(item, "PackageIdentifier");
                if (string.IsNullOrWhiteSpace(id)) continue;

                results.Add(new StoreApp
                {
                    ProductId = id!,
                    Title = GetString(item, "PackageName") ?? id!,
                    Publisher = GetString(item, "Publisher") ?? "Microsoft Store",
                    Category = "Aplicativo",
                    Summary = $"Product ID: {id}"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write($"   [AVISO] manifestSearch: {ex.Message}", LogKind.Warning);
        }

        return results;
    }

    private async Task<List<StoreApp>> SearchWingetAsync(string query, CancellationToken ct)
    {
        var results = new List<StoreApp>();
        var winget = SystemInfo.FindWinget();
        if (winget == null) return results;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Shell.RunAsync(
            winget,
            $"search --source msstore \"{query}\" --accept-source-agreements --disable-interactivity",
            line =>
            {
                var text = line.TrimEnd('\r', ' ');
                if (text.Length == 0) return;

                var match = SearchTableRow.Match(text);
                if (!match.Success) return;

                var id = match.Groups["id"].Value.Trim();
                var name = match.Groups["name"].Value.Trim();

                if (id.Length < 9 || !seen.Add(id)) return;

                results.Add(new StoreApp
                {
                    ProductId = id,
                    Title = name,
                    Publisher = "Microsoft Store",
                    Category = "Aplicativo",
                    Summary = $"Product ID: {id}"
                });
            },
            ct).ConfigureAwait(false);

        if (results.Count > 0)
        {
            _log.Write($"   [LOJA] winget (msstore) retornou {results.Count} aplicativo(s).", LogKind.Info);
        }

        return results;
    }

    private async Task<List<StoreApp>> SearchDisplayCatalogAsync(string query, CancellationToken ct)
    {
        var results = new List<StoreApp>();

        try
        {
            var url = $"{DisplayCatalogSearchUrl}?query={Uri.EscapeDataString(query)}&market=BR&languages=pt-br&fieldsTemplate=Details";
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Products", out var products) ||
                products.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var product in products.EnumerateArray())
            {
                var id = GetString(product, "ProductId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var details = ParseCatalogProduct(product, id!);
                _detailsCache[id!] = details;

                var app = new StoreApp
                {
                    ProductId = id!,
                    Title = ExtractTitle(product) ?? id!,
                    Publisher = details.Publisher ?? "Microsoft Store",
                    Category = TranslateCategory(details.Category),
                    LogoUrl = details.LogoUrl,
                    HeroUrl = details.HeroUrl,
                    TileColorHex = details.TileColorHex,
                    PackageFamilyName = details.PackageFamilyName,
                    Rating = details.Rating,
                    RatingCount = details.RatingCount,
                    Summary = $"Product ID: {id}"
                };

                ApplyTileColor(app, details.TileColorHex);
                results.Add(app);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write($"   [AVISO] DisplayCatalog: {ex.Message}", LogKind.Warning);
        }

        return results;
    }

    public async Task<StoreDetails> GetDetailsAsync(string productId, CancellationToken ct)
    {
        if (_detailsCache.TryGetValue(productId, out var cached) &&
            !string.IsNullOrWhiteSpace(cached.Description))
        {
            return cached;
        }

        var details = cached ?? new StoreDetails();

        await FillFromCatalogAsync(productId, details, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(details.Description) ||
            string.IsNullOrWhiteSpace(details.Publisher))
        {
            await FillFromWingetAsync(productId, details, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(details.Description))
        {
            details.Description =
                "Aplicativo distribuído oficialmente pela Microsoft Store.\n\n" +
                $"Product ID: {productId}";
        }

        _detailsCache[productId] = details;
        return details;
    }

    private async Task FillFromCatalogAsync(string productId, StoreDetails details, CancellationToken ct)
    {
        foreach (var culture in new[] { ("BR", "pt-br"), ("US", "en-us") })
        {
            try
            {
                var url = $"{DisplayCatalogUrl}?bigIds={Uri.EscapeDataString(productId)}" +
                          $"&market={culture.Item1}&languages={culture.Item2}&fieldsTemplate=Details";

                var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);

                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Products", out var products) ||
                    products.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var product in products.EnumerateArray())
                {
                    var parsed = ParseCatalogProduct(product, productId);
                    if (string.IsNullOrWhiteSpace(details.Description) && !string.IsNullOrWhiteSpace(parsed.Description))
                        details.Description = parsed.Description;
                    if (string.IsNullOrWhiteSpace(details.ShortDescription) && !string.IsNullOrWhiteSpace(parsed.ShortDescription))
                        details.ShortDescription = parsed.ShortDescription;
                    if (string.IsNullOrWhiteSpace(details.Publisher) && !string.IsNullOrWhiteSpace(parsed.Publisher))
                        details.Publisher = parsed.Publisher;
                    if (string.IsNullOrWhiteSpace(details.Category) && !string.IsNullOrWhiteSpace(parsed.Category))
                        details.Category = parsed.Category;
                    details.LogoUrl ??= parsed.LogoUrl;
                    details.HeroUrl ??= parsed.HeroUrl;
                    details.TileColorHex ??= parsed.TileColorHex;
                    details.PackageFamilyName ??= parsed.PackageFamilyName;
                    details.SizeLabel ??= parsed.SizeLabel;
                    details.ReleaseYear ??= parsed.ReleaseYear;
                    details.Version ??= parsed.Version;
                    if (details.Rating <= 0 && parsed.Rating > 0)
                    {
                        details.Rating = parsed.Rating;
                        details.RatingCount = parsed.RatingCount;
                    }
                    if (details.Screenshots.Count == 0 && parsed.Screenshots.Count > 0)
                    {
                        details.Screenshots.AddRange(parsed.Screenshots);
                    }
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Write($"   [AVISO] DisplayCatalog ({culture.Item1}): {ex.Message}", LogKind.Warning);
            }

            if (!string.IsNullOrWhiteSpace(details.Description) && details.LogoUrl != null)
            {
                break;
            }
        }
    }

    private static string? ExtractTitle(JsonElement product)
    {
        if (product.TryGetProperty("LocalizedProperties", out var localized) &&
            localized.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in localized.EnumerateArray())
            {
                var t = GetString(item, "ProductTitle");
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        return null;
    }

    private static StoreDetails ParseCatalogProduct(JsonElement product, string productId)
    {
        var details = new StoreDetails();

        if (product.TryGetProperty("Properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            details.Category = GetString(props, "Category");
            details.PackageFamilyName = GetString(props, "PackageFamilyName");
        }

        if (product.TryGetProperty("MarketProperties", out var marketProps) &&
            marketProps.ValueKind == JsonValueKind.Array)
        {
            foreach (var mp in marketProps.EnumerateArray())
            {
                var rel = GetString(mp, "OriginalReleaseDate");
                if (!string.IsNullOrWhiteSpace(rel) && DateTime.TryParse(rel, out var dt) && dt.Year > 1995)
                {
                    details.ReleaseYear = dt.Year.ToString();
                }

                if (mp.TryGetProperty("UsageData", out var usage) && usage.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in usage.EnumerateArray())
                    {
                        var span = GetString(u, "AggregateTimeSpan") ?? string.Empty;
                        var avg = u.TryGetProperty("AverageRating", out var ar) && ar.TryGetDouble(out var arv) ? arv : 0;
                        var cnt = u.TryGetProperty("RatingCount", out var rc) && rc.TryGetInt32(out var rcv) ? rcv : 0;

                        if (span.Equals("AllTime", StringComparison.OrdinalIgnoreCase) && cnt > 0)
                        {
                            details.Rating = avg;
                            details.RatingCount = cnt;
                            break;
                        }
                        if (cnt > details.RatingCount)
                        {
                            details.Rating = avg;
                            details.RatingCount = cnt;
                        }
                    }
                }
                break;
            }
        }

        if (product.TryGetProperty("DisplaySkuAvailabilities", out var skus) &&
            skus.ValueKind == JsonValueKind.Array)
        {
            foreach (var skuAvail in skus.EnumerateArray())
            {
                if (skuAvail.TryGetProperty("Sku", out var sku) &&
                    sku.TryGetProperty("Properties", out var skuProps) &&
                    skuProps.TryGetProperty("Packages", out var packages) &&
                    packages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pkg in packages.EnumerateArray())
                    {
                        details.PackageFamilyName ??= GetString(pkg, "PackageFamilyName");

                        if (pkg.TryGetProperty("MaxDownloadSizeInBytes", out var sz) &&
                            sz.TryGetInt64(out var bytes) && bytes > 0)
                        {
                            details.SizeLabel = FormatBytes(bytes);
                        }

                        var ver = GetString(pkg, "Version");
                        if (!string.IsNullOrWhiteSpace(ver) && ver != "0.0.0.0")
                        {
                            details.Version = ver;
                        }

                        if (details.SizeLabel != null) break;
                    }
                }
                break;
            }
        }

        if (product.TryGetProperty("LocalizedProperties", out var localized) &&
            localized.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in localized.EnumerateArray())
            {
                var description = GetString(item, "ProductDescription");
                var shortDesc = GetString(item, "ShortDescription");

                if (!string.IsNullOrWhiteSpace(description)) details.Description = description!.Trim();
                else if (!string.IsNullOrWhiteSpace(shortDesc)) details.Description = shortDesc!.Trim();

                if (!string.IsNullOrWhiteSpace(shortDesc)) details.ShortDescription = shortDesc!.Trim();
                details.Publisher = GetString(item, "PublisherName");

                if (item.TryGetProperty("Images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    var (logo, bg) = PickLogo(images);
                    details.LogoUrl = logo;
                    details.TileColorHex = bg;
                    details.HeroUrl = PickHero(images);
                    PickScreenshots(images, details.Screenshots);
                }

                break;
            }
        }

        return details;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024.0 * 1024):0.1} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    public static string TranslateCategory(string? category) => (category ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "music" => "Música",
        "social" => "Social",
        "entertainment" => "Entretenimento",
        "photo & video" => "Foto e vídeo",
        "productivity" => "Produtividade",
        "developer tools" => "Desenvolvedor",
        "utilities & tools" => "Utilitários",
        "security" => "Segurança",
        "personalization" => "Personalização",
        "action & adventure" or "card & board" or "games" => "Jogos",
        "shopping" => "Compras",
        "education" => "Educação",
        "books & reference" => "Livros e referência",
        "news & weather" => "Notícias e clima",
        "" => "Aplicativo",
        _ => category!
    };

    private static (string? Url, string? Background) PickLogo(JsonElement images)
    {
        string? best = null;
        string? bestBg = null;
        var bestScore = -1;

        foreach (var image in images.EnumerateArray())
        {
            var uri = GetString(image, "Uri");
            if (string.IsNullOrWhiteSpace(uri)) continue;

            var purpose = (GetString(image, "ImagePurpose") ?? string.Empty).ToLowerInvariant();
            if (purpose is not ("logo" or "tile" or "boxart")) continue;

            var width = image.TryGetProperty("Width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
            var height = image.TryGetProperty("Height", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
            if (width > 0 && height > 0 && Math.Abs(width - height) > 16) continue; // ignora tiles largos

            var score = purpose switch
            {
                "logo" => 3000,
                "tile" => 2200,
                _ => 1000
            } + Math.Min(height, 300);

            if (score > bestScore)
            {
                bestScore = score;
                best = uri;
                bestBg = GetString(image, "BackgroundColor");
            }
        }

        if (best == null) return (null, null);
        return (FormatImageUrl(best, 128, 128), bestBg);
    }

    private static string? PickHero(JsonElement images)
    {
        string? best = null;
        var bestScore = -1;

        foreach (var image in images.EnumerateArray())
        {
            var uri = GetString(image, "Uri");
            if (string.IsNullOrWhiteSpace(uri)) continue;

            var purpose = (GetString(image, "ImagePurpose") ?? string.Empty).ToLowerInvariant();
            var score = purpose switch
            {
                "superheroart" => 4000,
                "hero" => 3500,
                "titledheroart" => 3000,
                "screenshot" => 1500,
                _ => -1
            };
            if (score < 0) continue;

            var width = image.TryGetProperty("Width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
            if (width > 0 && width < 600) continue;

            if (score + width > bestScore)
            {
                bestScore = score + width;
                best = uri;
            }
        }

        return best == null ? null : FormatImageUrl(best, 960, 540);
    }

    private static void PickScreenshots(JsonElement images, List<string> target)
    {
        foreach (var image in images.EnumerateArray())
        {
            if (target.Count >= 5) break;
            var purpose = (GetString(image, "ImagePurpose") ?? string.Empty).ToLowerInvariant();
            if (purpose != "screenshot") continue;

            var uri = GetString(image, "Uri");
            if (string.IsNullOrWhiteSpace(uri)) continue;

            var formatted = FormatImageUrl(uri!, 560, 315);
            if (!target.Contains(formatted)) target.Add(formatted);
        }
    }

    private static string FormatImageUrl(string uri, int w, int h)
    {
        if (uri.StartsWith("//", StringComparison.Ordinal)) uri = "https:" + uri;
        return uri.Contains('?') ? $"{uri}&w={w}&h={h}&format=png" : $"{uri}?w={w}&h={h}&format=png";
    }

    private async Task FillFromWingetAsync(string productId, StoreDetails details, CancellationToken ct)
    {
        var winget = SystemInfo.FindWinget();
        if (winget == null) return;

        var description = new StringBuilder();
        var inDescription = false;
        var descriptionDone = false;

        await Shell.RunAsync(
            winget,
            $"show --id {productId} --source msstore --accept-source-agreements --disable-interactivity",
            line =>
            {
                var raw = line.TrimEnd('\r');
                var text = raw.Trim();

                if (text.Length == 0)
                {
                    if (inDescription && description.Length > 0) description.AppendLine();
                    return;
                }

                var match = FieldLine.Match(text);
                if (match.Success && !inDescription)
                {
                    var key = match.Groups["key"].Value.Trim().ToLowerInvariant();
                    var value = match.Groups["value"].Value.Trim();

                    switch (key)
                    {
                        case "publisher":
                        case "editora":
                            if (string.IsNullOrWhiteSpace(details.Publisher)) details.Publisher = value;
                            break;
                        case "version":
                        case "versão":
                            if (!string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase) &&
                                string.IsNullOrWhiteSpace(details.Version))
                            {
                                details.Version = value;
                            }
                            break;
                        case "description":
                        case "descrição":
                            inDescription = true;
                            if (value.Length > 0) description.AppendLine(value);
                            break;
                    }

                    return;
                }

                if (inDescription && !descriptionDone)
                {
                    if (FieldLine.IsMatch(text) && !text.StartsWith("  ", StringComparison.Ordinal))
                    {
                        descriptionDone = true;
                        inDescription = false;
                        return;
                    }

                    description.AppendLine(text);
                }
            },
            ct).ConfigureAwait(false);

        if (description.Length > 0 && string.IsNullOrWhiteSpace(details.Description))
        {
            details.Description = description.ToString().Trim();
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}
