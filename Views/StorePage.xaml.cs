using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KlyraOptimizer.Core;
using KlyraOptimizer.Services;

namespace KlyraOptimizer.Views;

public partial class StorePage : UserControl, IRefreshablePage
{
    private readonly Action? _onNavigationStateChanged;
    private readonly List<StoreApp> _heroApps = new();
    private StoreApp? _selected;
    private StoreApp? _currentHero;
    private bool _busy;
    private bool _showcaseLoaded;
    private string _returnMode = "showcase"; // "showcase" ou "results"

    public StorePage() : this(null) { }

    public StorePage(Action? onNavigationStateChanged)
    {
        _onNavigationStateChanged = onNavigationStateChanged;
        InitializeComponent();

        Loaded += (_, _) =>
        {
            AppServices.Winget.Progress -= OnWingetProgress;
            AppServices.Winget.Progress += OnWingetProgress;
        };
        Unloaded += (_, _) => AppServices.Winget.Progress -= OnWingetProgress;
    }

    /// <summary>Permite que o botao Voltar da barra de titulo retorne dentro da propria Loja.</summary>
    public bool CanNavigateBack => DetailView.Visibility == Visibility.Visible || ResultsView.Visibility == Visibility.Visible;

    public void NavigateBackToHome()
    {
        if (DetailView.Visibility == Visibility.Visible)
        {
            ShowView(_returnMode, drillOut: true);
            return;
        }

        TabHome.IsChecked = true;
        ShowView("showcase", drillOut: true);
    }

    public async void OnActivated()
    {
        await Task.Run(() => AppServices.System.RefreshWinget());
        UpdateWingetState();

        if (!_showcaseLoaded)
        {
            _showcaseLoaded = true;
            await LoadShowcaseAsync();
        }
    }

    private void OnWingetProgress(string message)
    {
        WingetWarnText.Text = message;
        InstallStatus.Text = message;
    }

    private void UpdateWingetState()
    {
        var installed = AppServices.System.WingetInstalled;
        Motion.SetVisible(WingetWarn, !installed);

        if (!installed)
        {
            WingetWarnText.Text =
                "O App Installer (winget) é usado para baixar e instalar os aplicativos em segundo plano. " +
                "Clique ao lado para instalar automaticamente.";
        }
    }

    private void ShowView(string mode, bool drillIn = false, bool drillOut = false)
    {
        ShowcaseView.Visibility = mode == "showcase" ? Visibility.Visible : Visibility.Collapsed;
        ResultsView.Visibility = mode == "results" ? Visibility.Visible : Visibility.Collapsed;
        DetailView.Visibility = mode == "detail" ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement active = mode switch
        {
            "detail" => DetailView,
            "results" => ResultsView,
            _ => ShowcaseView
        };

        if (drillIn) Motion.DrillIn(active);
        else if (drillOut) Motion.DrillOut(active);
        else Motion.FadeIn(active, 14, 200);

        _onNavigationStateChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Vitrine inicial (estilo Windows 10 Store)
    // ------------------------------------------------------------------
    private async Task LoadShowcaseAsync()
    {
        SetBusy(true);
        try
        {
            var essentialTask = AppServices.Store.GetProductsByIdsAsync(StoreService.EssentialIds, CancellationToken.None);
            var prodTask = AppServices.Store.GetProductsByIdsAsync(StoreService.ProductivityAndDevIds, CancellationToken.None);

            await Task.WhenAll(essentialTask, prodTask);

            var essentials = essentialTask.Result;
            var productivity = prodTask.Result;

            EssentialRow.ItemsSource = essentials;
            ProductivityRow.ItemsSource = productivity;

            _heroApps.Clear();
            foreach (var candidate in essentials.Concat(productivity).Where(a => !string.IsNullOrWhiteSpace(a.HeroUrl)).Take(4))
            {
                _heroApps.Add(candidate);
            }
            if (_heroApps.Count == 0 && essentials.Count > 0)
            {
                _heroApps.AddRange(essentials.Take(4));
            }

            if (_heroApps.Count > 0)
            {
                await SetHeroAsync(_heroApps[0]);
            }

            _ = HydrateLogosAsync(essentials.Concat(productivity).ToList());
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[LOJA] Falha ao carregar vitrine: {ex.Message}", LogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SetHeroAsync(StoreApp app)
    {
        _currentHero = app;
        HeroBadge.Text = $"EM DESTAQUE • {app.Category.ToUpperInvariant()}";
        HeroTitle.Text = app.Title;
        HeroSubtitle.Text = !string.IsNullOrWhiteSpace(app.Summary)
            ? app.Summary
            : $"{app.Publisher}  •  {app.StarsText} {app.RatingText}  •  Instalação direta via winget sem conta Microsoft.";

        Motion.FadeIn(HeroTitle, 8, 180);

        if (!string.IsNullOrWhiteSpace(app.HeroUrl))
        {
            var bmp = await AppServices.Store.LoadImageAsync(app.HeroUrl);
            if (_currentHero == app && bmp != null)
            {
                HeroBannerImage.Source = bmp;
            }
        }
        else
        {
            HeroBannerImage.Source = null;
        }
    }

    private async void HeroSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string s } || !int.TryParse(s, out var idx)) return;
        if (idx >= 0 && idx < _heroApps.Count)
        {
            await SetHeroAsync(_heroApps[idx]);
        }
    }

    internal void HeroOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_currentHero != null)
        {
            _returnMode = "showcase";
            _ = OpenProductAsync(_currentHero);
        }
    }

    private async Task HydrateLogosAsync(IReadOnlyList<StoreApp> apps)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = apps.Select(async app =>
        {
            if (app.Logo != null || string.IsNullOrWhiteSpace(app.LogoUrl)) return;
            await gate.WaitAsync();
            try
            {
                var bmp = await AppServices.Store.LoadImageAsync(app.LogoUrl);
                if (bmp != null) app.Logo = bmp;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    // ------------------------------------------------------------------
    // Abas Pivot e Categorias
    // ------------------------------------------------------------------
    private async void Pivot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tab }) return;

        switch (tab)
        {
            case "home":
                ShowView("showcase");
                break;

            case "apps":
                await ShowCuratedCollectionAsync("Aplicativos em alta", StoreService.EssentialIds);
                break;

            case "games":
                await SearchAndShowAsync("Xbox Game", "Jogos na Microsoft Store");
                break;

            case "productivity":
                await ShowCuratedCollectionAsync("Produtividade e utilitários", StoreService.ProductivityAndDevIds);
                break;

            case "dev":
                await SearchAndShowAsync("Python Terminal Git", "Ferramentas de desenvolvedor");
                break;
        }
    }

    private async void ShowAllSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string section }) return;
        if (section == "productivity")
        {
            TabProd.IsChecked = true;
            await ShowCuratedCollectionAsync("Essenciais para Windows e produtividade", StoreService.ProductivityAndDevIds);
        }
        else
        {
            TabApps.IsChecked = true;
            await ShowCuratedCollectionAsync("Principais aplicativos gratuitos", StoreService.EssentialIds);
        }
    }

    private async Task ShowCuratedCollectionAsync(string title, IReadOnlyList<string> ids)
    {
        _returnMode = "results";
        ResultsHeader.Text = title;
        ResultsStatus.Text = "Carregando catálogo...";
        EmptyResults.Visibility = Visibility.Collapsed;
        ShowView("results");

        SetBusy(true);
        try
        {
            var items = await AppServices.Store.GetProductsByIdsAsync(ids, CancellationToken.None);
            ResultsGrid.ItemsSource = items;
            ResultsStatus.Text = $"{items.Count} aplicativos disponíveis gratuitamente";
            _ = HydrateLogosAsync(items);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void QuickCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string query }) return;
        SearchBox.Text = query;
        _ = SearchAndShowAsync(query, $"Resultados para \"{query}\"");
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e)
    {
        TabHome.IsChecked = true;
        ShowView("showcase", drillOut: true);
    }

    // ------------------------------------------------------------------
    // Pesquisa
    // ------------------------------------------------------------------
    private void Search_Click(object sender, RoutedEventArgs e)
        => _ = SearchAndShowAsync(SearchBox.Text, $"Resultados para \"{SearchBox.Text.Trim()}\"");

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = SearchAndShowAsync(SearchBox.Text, $"Resultados para \"{SearchBox.Text.Trim()}\"");
        }
    }

    private async Task SearchAndShowAsync(string query, string heading)
    {
        if (_busy || string.IsNullOrWhiteSpace(query)) return;

        query = query.Trim();
        _returnMode = "results";
        ResultsHeader.Text = heading;
        ResultsStatus.Text = "Consultando a Microsoft Store...";
        EmptyResults.Visibility = Visibility.Collapsed;
        ResultsGrid.ItemsSource = null;
        ShowView("results");

        SetBusy(true);
        AppServices.Log.Write($"[LOJA] Consultando Store por '{query}'...", LogKind.Info);

        try
        {
            var results = await AppServices.Store.SearchAsync(query, CancellationToken.None);

            var ordered = results
                .OrderByDescending(r => r.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Title)
                .ToList();

            ResultsGrid.ItemsSource = ordered;
            EmptyResults.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultsStatus.Text = ordered.Count == 0
                ? "Nenhum resultado encontrado"
                : $"{ordered.Count} aplicativo(s) encontrado(s) na Microsoft Store";

            Motion.FadeIn(ResultsGrid, 12, 200);
            AppServices.Log.Write($"[LOJA] {ordered.Count} resultado(s) para '{query}'.", LogKind.Info);

            // Enriquece icones, notas e cores em segundo plano sem travar a listagem
            _ = Task.Run(async () =>
            {
                await AppServices.Store.EnrichAppsAsync(ordered, CancellationToken.None);
                await HydrateLogosAsync(ordered);
            });
        }
        catch (Exception ex)
        {
            ResultsStatus.Text = "Falha ao consultar a Microsoft Store.";
            AppServices.Log.Write($"[LOJA] Erro na busca: {ex.Message}", LogKind.Failed);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------
    // Pagina de Produto (PDP estilo Windows 10 Store)
    // ------------------------------------------------------------------
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: StoreApp app }) return;
        _returnMode = ResultsView.Visibility == Visibility.Visible ? "results" : "showcase";
        _ = OpenProductAsync(app);
    }

    private void BackFromDetail_Click(object sender, RoutedEventArgs e)
    {
        ShowView(_returnMode, drillOut: true);
    }

    private async Task OpenProductAsync(StoreApp app)
    {
        _selected = app;
        DetailView.ScrollToHome();
        ShowView("detail", drillIn: true);

        DetailTitle.Text = app.Title;
        DetailPublisher.Text = app.Publisher;
        DetailCategory.Text = string.IsNullOrWhiteSpace(app.Category) ? "Aplicativo" : app.Category;
        DetailInitial.Text = app.Initial;
        DetailStars.Text = app.StarsText;
        DetailRatingText.Text = app.RatingText;
        DetailRatingCount.Text = string.IsNullOrWhiteSpace(app.RatingCountText) ? string.Empty : $"({app.RatingCountText} avaliações)";

        DetailTileBox.Background = app.TileBackground ?? (Brush)FindResource("App.Card");

        if (app.Logo != null)
        {
            DetailLogo.Source = app.Logo;
            DetailLogo.Visibility = Visibility.Visible;
            DetailInitial.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailLogo.Visibility = Visibility.Collapsed;
            DetailInitial.Visibility = Visibility.Visible;
        }

        DetailHeroImage.Source = null;
        ScreenshotsSection.Visibility = Visibility.Collapsed;
        ScreenshotsList.ItemsSource = null;

        DetailDescription.Text = "Carregando informações oficiais da Microsoft Store...";
        MetaPublisher.Text = app.Publisher;
        MetaSize.Text = "Calculando...";
        MetaVersion.Text = "—";
        MetaProductId.Text = app.ProductId;

        InstallButton.Content = "Obter";
        InstallButton.IsEnabled = true;
        InstallProgressPanel.Visibility = Visibility.Collapsed;
        InstallStatus.Text = "Instalação silenciosa via winget (sem conta Microsoft).";

        SetBusy(true);
        try
        {
            var details = await AppServices.Store.GetDetailsAsync(app.ProductId, CancellationToken.None);
            if (_selected != app) return;

            DetailDescription.Text = details.Description;

            if (!string.IsNullOrWhiteSpace(details.Publisher))
            {
                DetailPublisher.Text = details.Publisher!;
                MetaPublisher.Text = details.Publisher!;
            }

            if (!string.IsNullOrWhiteSpace(details.Category))
            {
                DetailCategory.Text = StoreService.TranslateCategory(details.Category);
            }

            if (details.Rating > 0)
            {
                app.Rating = details.Rating;
                app.RatingCount = details.RatingCount;
                DetailStars.Text = app.StarsText;
                DetailRatingText.Text = app.RatingText;
                DetailRatingCount.Text = string.IsNullOrWhiteSpace(app.RatingCountText) ? string.Empty : $"({app.RatingCountText} avaliações)";
            }

            MetaSize.Text = details.SizeLabel ?? "Variável por arquitetura";
            MetaVersion.Text = !string.IsNullOrWhiteSpace(details.Version)
                ? details.Version!
                : (!string.IsNullOrWhiteSpace(details.ReleaseYear) ? $"Lançado em {details.ReleaseYear}" : "Última versão da Store");

            if (app.Logo == null && !string.IsNullOrWhiteSpace(details.LogoUrl))
            {
                var logoBmp = await AppServices.Store.LoadImageAsync(details.LogoUrl);
                if (_selected == app && logoBmp != null)
                {
                    app.Logo = logoBmp;
                    DetailLogo.Source = logoBmp;
                    DetailLogo.Visibility = Visibility.Visible;
                    DetailInitial.Visibility = Visibility.Collapsed;
                }
            }

            if (!string.IsNullOrWhiteSpace(details.HeroUrl))
            {
                var heroBmp = await AppServices.Store.LoadImageAsync(details.HeroUrl);
                if (_selected == app && heroBmp != null)
                {
                    DetailHeroImage.Source = heroBmp;
                }
            }

            if (details.Screenshots.Count > 0)
            {
                var shots = new ObservableCollection<BitmapImage>();
                ScreenshotsList.ItemsSource = shots;
                ScreenshotsSection.Visibility = Visibility.Visible;

                foreach (var url in details.Screenshots.Take(4))
                {
                    var shotBmp = await AppServices.Store.LoadImageAsync(url);
                    if (_selected != app) return;
                    if (shotBmp != null) shots.Add(shotBmp);
                }

                if (shots.Count == 0) ScreenshotsSection.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[LOJA] Detalhes de {app.ProductId} falharam: {ex.Message}", LogKind.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------
    // Instalacao em segundo plano via winget
    // ------------------------------------------------------------------
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _busy) return;

        if (!AppServices.System.WingetInstalled)
        {
            UpdateWingetState();
            InstallStatus.Text = "Instale o App Installer (winget) no aviso acima para continuar.";
            return;
        }

        var app = _selected;

        SetBusy(true);
        InstallButton.IsEnabled = false;
        InstallButton.Content = "Instalando...";
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallStatus.Text = $"Baixando e instalando {app.Title} em segundo plano...";

        try
        {
            var success = await AppServices.Winget.InstallFromStoreAsync(app.ProductId, CancellationToken.None);

            if (_selected == app)
            {
                InstallButton.Content = success ? "Instalado" : "Tentar novamente";
                InstallStatus.Text = success
                    ? $"✓ {app.Title} instalado com sucesso neste computador."
                    : $"Não foi possível concluir a instalação de {app.Title} (detalhes registrados nos logs).";
            }

            if (success)
            {
                AppServices.Log.Write($"[LOJA] {app.Title} ({app.ProductId}) instalado.", LogKind.Applied);
            }
        }
        catch (Exception ex)
        {
            InstallButton.Content = "Obter";
            InstallStatus.Text = "Erro durante a instalação: " + ex.Message;
            AppServices.Log.Write($"[LOJA] Erro instalando {app.ProductId}: {ex.Message}", LogKind.Failed);
        }
        finally
        {
            SetBusy(false);
            InstallProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
        }
    }

    private async void InstallWinget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        InstallWingetButton.IsEnabled = false;
        WingetWarn.Visibility = Visibility.Visible;
        WingetWarnText.Text = "Baixando o App Installer e as dependências...";

        try
        {
            var ok = await AppServices.Winget.EnsureAsync(CancellationToken.None);

            WingetWarnText.Text = ok
                ? "winget instalado com sucesso."
                : "Não foi possível instalar o winget automaticamente. Instale o 'App Installer' pela Microsoft Store.";

            UpdateWingetState();
        }
        finally
        {
            SetBusy(false);
            InstallWingetButton.IsEnabled = true;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Motion.SetVisible(TopProgress, busy);
        if (ResultsLoadingPanel != null)
        {
            ResultsLoadingPanel.Visibility = busy && ResultsView.Visibility == Visibility.Visible && ResultsGrid.ItemsSource == null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
