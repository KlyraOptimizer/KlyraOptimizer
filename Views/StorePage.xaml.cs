using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KlyraOptimizer.Core;
using KlyraOptimizer.Services;

namespace KlyraOptimizer.Views;

public partial class StorePage : UserControl, IRefreshablePage
{
    private readonly Action? _onNavigationStateChanged;
    private readonly DispatcherTimer _connectivityTimer;
    private readonly List<StoreApp> _heroApps = new();
    private StoreApp? _selected;
    private InstalledAppInfo? _selectedInstalledInfo;
    private CancellationTokenSource? _detailCheckCts;
    private CancellationTokenSource? _navCts;
    private int _navSeq;
    private StoreApp? _currentHero;
    private List<StoreApp> _lastResults = new();
    private string _lastQuery = string.Empty;
    private string _lastResultsHeading = "Resultados";
    private string _currentPivotTab = "home";
    private Func<Task>? _pendingOfflineRetry;
    private bool _installingOrUninstalling;
    private bool _showcaseLoaded;
    private bool _checkingNet;
    private bool _isOnline = true;
    private string _activeViewMode = "showcase";
    private string _returnMode = "showcase"; // "showcase" ou "results"

    public StorePage() : this(null) { }

    public StorePage(Action? onNavigationStateChanged)
    {
        _onNavigationStateChanged = onNavigationStateChanged;
        InitializeComponent();

        _connectivityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _connectivityTimer.Tick += async (_, _) => await PollConnectivitySilentAsync();

        Loaded += (_, _) =>
        {
            AppServices.Winget.Progress -= OnWingetProgress;
            AppServices.Winget.Progress += OnWingetProgress;
            _connectivityTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Winget.Progress -= OnWingetProgress;
            _connectivityTimer.Stop();
        };
    }

    /// <summary>Permite que o botao Voltar da barra de titulo retorne dentro da propria Loja.</summary>
    public bool CanNavigateBack =>
        OfflineView.Visibility != Visibility.Visible &&
        (DetailView.Visibility == Visibility.Visible || ResultsView.Visibility == Visibility.Visible);

    public void NavigateBackToHome()
    {
        if (DetailView.Visibility == Visibility.Visible)
        {
            _ = NavigateBackFromDetailAsync();
            return;
        }

        TabHome.IsChecked = true;
        _currentPivotTab = "home";
        _ = OpenTabAsync("home", drillOut: true);
    }

    public async void OnActivated()
    {
        _connectivityTimer.Start();
        _ = AppServices.Winget.WarmInstalledCacheAsync();
        await Task.Run(() => AppServices.System.RefreshWinget());
        UpdateWingetState();

        if (!_showcaseLoaded)
        {
            ShowInitialLoading();
        }

        var (seq, ct) = BeginNavOperation();
        if (!await EnsureOnlineOrShowOfflineAsync(() => ResumeAfterReconnectAsync(), ct))
            return;

        if (seq != _navSeq || ct.IsCancellationRequested) return;

        if (!_showcaseLoaded)
        {
            await LoadShowcaseCoreAsync(seq, ct);
        }
        else
        {
            ShowView(_activeViewMode);
        }
    }

    private (int Seq, CancellationToken Token) BeginNavOperation()
    {
        _navCts?.Cancel();
        var cts = new CancellationTokenSource();
        _navCts = cts;
        return (++_navSeq, cts.Token);
    }

    private void ShowInitialLoading()
    {
        StoreLoadingView.Visibility = Visibility.Visible;
        OfflineView.Visibility = Visibility.Collapsed;
        ShowcaseView.Visibility = Visibility.Collapsed;
        ResultsView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Collapsed;
    }

    private void ShowOfflineScreen(Func<Task>? retryWhenOnline = null)
    {
        _isOnline = false;
        if (retryWhenOnline != null)
        {
            _pendingOfflineRetry = retryWhenOnline;
        }

        SetTopBusy(false);
        StoreLoadingView.Visibility = Visibility.Collapsed;
        ShowcaseView.Visibility = Visibility.Collapsed;
        ResultsView.Visibility = Visibility.Collapsed;
        DetailView.Visibility = Visibility.Collapsed;
        OfflineView.Visibility = Visibility.Visible;
        _onNavigationStateChanged?.Invoke();
    }

    /// <summary>
    /// Verifica silenciosamente se ha internet antes de QUALQUER acao na Loja.
    /// Caso nao haja, exibe imediatamente a tela "Voce esta sem conexao com a internet".
    /// </summary>
    private async Task<bool> EnsureOnlineOrShowOfflineAsync(Func<Task>? retryWhenOnline, CancellationToken ct = default)
    {
        var online = await AppServices.Store.CheckInternetSilentAsync(ct);
        if (ct.IsCancellationRequested) return false;

        _isOnline = online;
        if (!online)
        {
            ShowOfflineScreen(retryWhenOnline);
            return false;
        }

        if (OfflineView.Visibility == Visibility.Visible)
        {
            OfflineView.Visibility = Visibility.Collapsed;
        }

        return true;
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        await PollConnectivitySilentAsync(forceRetry: true);
    }

    /// <summary>
    /// Monitora a conexao a cada 5 segundos sem registrar nada em logs.
    /// Se cair, exibe a tela offline; quando voltar, retoma exatamente a acao pendente.
    /// </summary>
    private async Task PollConnectivitySilentAsync(bool forceRetry = false)
    {
        if (_checkingNet || _installingOrUninstalling) return;
        _checkingNet = true;

        try
        {
            var online = await AppServices.Store.CheckInternetSilentAsync();
            _isOnline = online;

            if (!online)
            {
                if (OfflineView.Visibility != Visibility.Visible)
                {
                    ShowOfflineScreen(() => ResumeAfterReconnectAsync());
                }
                return;
            }

            if (OfflineView.Visibility == Visibility.Visible || forceRetry)
            {
                OfflineView.Visibility = Visibility.Collapsed;
                var retry = _pendingOfflineRetry;
                _pendingOfflineRetry = null;

                if (retry != null)
                {
                    await retry();
                }
                else
                {
                    await ResumeAfterReconnectAsync();
                }
            }
        }
        finally
        {
            _checkingNet = false;
        }
    }

    private async Task ResumeAfterReconnectAsync()
    {
        if (!_showcaseLoaded)
        {
            var (seq, ct) = BeginNavOperation();
            ShowInitialLoading();
            await LoadShowcaseCoreAsync(seq, ct);
            if (_activeViewMode == "showcase") return;
        }

        if (_activeViewMode == "detail" && _selected != null)
        {
            await OpenProductAsync(_selected);
            return;
        }

        if (_activeViewMode == "results")
        {
            if (!string.IsNullOrWhiteSpace(_lastQuery))
            {
                await SearchAndShowAsync(_lastQuery, _lastResultsHeading);
                return;
            }

            await OpenTabAsync(_currentPivotTab);
            return;
        }

        ShowView("showcase");
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
        _activeViewMode = mode;
        if (!_isOnline)
        {
            ShowOfflineScreen();
            return;
        }

        StoreLoadingView.Visibility = Visibility.Collapsed;
        OfflineView.Visibility = Visibility.Collapsed;
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
    private async Task LoadShowcaseCoreAsync(int seq, CancellationToken ct)
    {
        SetTopBusy(true);
        try
        {
            var essentialTask = AppServices.Store.GetProductsByIdsAsync(StoreService.EssentialIds, ct);
            var prodTask = AppServices.Store.GetProductsByIdsAsync(StoreService.ProductivityAndDevIds, ct);

            await Task.WhenAll(essentialTask, prodTask);
            if (seq != _navSeq || ct.IsCancellationRequested) return;

            var essentials = essentialTask.Result;
            var productivity = prodTask.Result;

            if (essentials.Count == 0 && productivity.Count == 0)
            {
                _showcaseLoaded = false;
                ShowOfflineScreen(() => OpenTabAsync("home"));
                return;
            }

            _showcaseLoaded = true;
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

            if (_activeViewMode == "showcase")
            {
                ShowView("showcase");
            }

            _ = HydrateLogosAsync(essentials.Concat(productivity).ToList());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[LOJA] Falha ao carregar vitrine: {ex.Message}", LogKind.Warning);
            if (!await AppServices.Store.CheckInternetSilentAsync())
            {
                ShowOfflineScreen(() => OpenTabAsync("home"));
            }
        }
        finally
        {
            if (seq == _navSeq) SetTopBusy(false);
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
        if (!await EnsureOnlineOrShowOfflineAsync(() => ResumeAfterReconnectAsync())) return;

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
    // Abas Pivot e Categorias (todas verificam internet imediatamente)
    // ------------------------------------------------------------------
    private void Pivot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tab }) return;
        _currentPivotTab = tab;
        _ = OpenTabAsync(tab);
    }

    private async Task OpenTabAsync(string tab, bool drillOut = false)
    {
        _currentPivotTab = tab;

        switch (tab)
        {
            case "home":
            {
                var (seq, ct) = BeginNavOperation();
                if (!_showcaseLoaded) ShowInitialLoading();
                if (!await EnsureOnlineOrShowOfflineAsync(() => OpenTabAsync("home"), ct)) return;
                if (seq != _navSeq || ct.IsCancellationRequested) return;

                if (!_showcaseLoaded)
                {
                    await LoadShowcaseCoreAsync(seq, ct);
                }
                else
                {
                    ShowView("showcase", drillOut: drillOut);
                }
                break;
            }

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
            _currentPivotTab = "productivity";
            await ShowCuratedCollectionAsync("Essenciais para Windows e produtividade", StoreService.ProductivityAndDevIds);
        }
        else
        {
            TabApps.IsChecked = true;
            _currentPivotTab = "apps";
            await ShowCuratedCollectionAsync("Principais aplicativos gratuitos", StoreService.EssentialIds);
        }
    }

    private async Task ShowCuratedCollectionAsync(string title, IReadOnlyList<string> ids)
    {
        var (seq, ct) = BeginNavOperation();
        _returnMode = "results";
        _lastQuery = string.Empty;
        _lastResultsHeading = title;

        if (!await EnsureOnlineOrShowOfflineAsync(() => ShowCuratedCollectionAsync(title, ids), ct))
            return;

        if (seq != _navSeq || ct.IsCancellationRequested) return;

        ResultsHeader.Text = title;
        ResultsStatus.Text = "Carregando catálogo...";
        EmptyResults.Visibility = Visibility.Collapsed;
        ResultsGrid.ItemsSource = null;
        ShowView("results");

        SetTopBusy(true);
        try
        {
            var items = await AppServices.Store.GetProductsByIdsAsync(ids, ct);
            if (seq != _navSeq || ct.IsCancellationRequested) return;

            if (items.Count == 0 && !await AppServices.Store.CheckInternetSilentAsync(ct))
            {
                ShowOfflineScreen(() => ShowCuratedCollectionAsync(title, ids));
                return;
            }

            _lastResults = items;
            ApplyResultSorting();
            ResultsStatus.Text = $"{items.Count} aplicativos disponíveis gratuitamente";
            _ = HydrateLogosAsync(items);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (seq == _navSeq) SetTopBusy(false);
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
        _currentPivotTab = "home";
        _ = OpenTabAsync("home", drillOut: true);
    }

    // ------------------------------------------------------------------
    // Pesquisa (API + winget complementar sem duplicatas)
    // ------------------------------------------------------------------
    public void RunExternalSearch(string query)
    {
        SearchBox.Text = query;
        _ = SearchAndShowAsync(query, $"Resultados para \"{query.Trim()}\"");
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid == null || _lastResults.Count == 0) return;
        ApplyResultSorting();
    }

    private void ApplyResultSorting()
    {
        var sortTag = (SortCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance";
        var q = _lastQuery.Trim();

        IEnumerable<StoreApp> sorted = sortTag switch
        {
            "rating" => _lastResults.OrderByDescending(r => r.Rating).ThenByDescending(r => r.RatingCount),
            "name" => _lastResults.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => _lastResults
                .Select((app, idx) => (app, idx))
                .OrderByDescending(x => !string.IsNullOrEmpty(q) && x.app.Title.Equals(q, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => !string.IsNullOrEmpty(q) && x.app.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.app.FromWingetOnly ? 1 : 0)
                .ThenBy(x => x.idx)
                .Select(x => x.app)
        };
        ResultsGrid.ItemsSource = sorted.ToList();
    }

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
        if (string.IsNullOrWhiteSpace(query)) return;

        query = query.Trim();
        var (seq, ct) = BeginNavOperation();

        _lastQuery = query;
        _lastResultsHeading = heading;
        _returnMode = "results";
        ResultsHeader.Text = heading;

        if (!await EnsureOnlineOrShowOfflineAsync(() => SearchAndShowAsync(query, heading), ct))
            return;

        if (seq != _navSeq || ct.IsCancellationRequested) return;

        ResultsStatus.Text = "Consultando a Microsoft Store e o winget...";
        EmptyResults.Visibility = Visibility.Collapsed;
        ResultsGrid.ItemsSource = null;
        ShowView("results");

        SetTopBusy(true);
        AppServices.Log.Write($"[LOJA] Consultando Store + winget por '{query}'...", LogKind.Info);

        try
        {
            var results = await AppServices.Store.SearchAsync(query, ct);
            if (seq != _navSeq || ct.IsCancellationRequested) return;

            if (results.Count == 0 && !await AppServices.Store.CheckInternetSilentAsync(ct))
            {
                ShowOfflineScreen(() => SearchAndShowAsync(query, heading));
                return;
            }

            _lastQuery = query;
            _lastResults = results;
            ApplyResultSorting();
            var ordered = (ResultsGrid.ItemsSource as List<StoreApp>) ?? results;

            EmptyResults.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ResultsStatus.Text = ordered.Count == 0
                ? "Nenhum resultado encontrado"
                : $"{ordered.Count} aplicativo(s) encontrado(s)";

            Motion.FadeIn(ResultsGrid, 12, 200);
            AppServices.Log.Write($"[LOJA] {ordered.Count} resultado(s) consolidado(s) para '{query}'.", LogKind.Info);

            // Enriquece icones, notas e cores em segundo plano (incluindo os itens vindos do winget!)
            _ = Task.Run(async () =>
            {
                await AppServices.Store.EnrichAppsAsync(ordered, CancellationToken.None);
                await HydrateLogosAsync(ordered);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (seq == _navSeq)
            {
                if (!await AppServices.Store.CheckInternetSilentAsync())
                {
                    ShowOfflineScreen(() => SearchAndShowAsync(query, heading));
                    return;
                }

                ResultsStatus.Text = "Falha ao consultar a Microsoft Store.";
                AppServices.Log.Write($"[LOJA] Erro na busca: {ex.Message}", LogKind.Failed);
            }
        }
        finally
        {
            if (seq == _navSeq) SetTopBusy(false);
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
        _ = NavigateBackFromDetailAsync();
    }

    private async Task NavigateBackFromDetailAsync()
    {
        var (seq, ct) = BeginNavOperation();
        if (!await EnsureOnlineOrShowOfflineAsync(() => NavigateBackFromDetailAsync(), ct))
            return;

        if (seq != _navSeq || ct.IsCancellationRequested) return;
        ShowView(_returnMode, drillOut: true);
    }

    private async Task OpenProductAsync(StoreApp app)
    {
        var (seq, ct) = BeginNavOperation();
        _selected = app;

        if (!await EnsureOnlineOrShowOfflineAsync(() => OpenProductAsync(app), ct))
            return;

        if (seq != _navSeq || ct.IsCancellationRequested) return;

        _detailCheckCts?.Cancel();
        var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _detailCheckCts = checkCts;
        var detailCt = checkCts.Token;

        _selectedInstalledInfo = null;
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

        ApplyInstalledUiState(isInstalled: false);
        InstallProgressPanel.Visibility = Visibility.Collapsed;

        // Dispara verificacao silenciosa imediata em segundo plano
        _ = VerifyProductInstalledAsync(app, detailCt);

        SetTopBusy(true);
        try
        {
            var details = await AppServices.Store.GetDetailsAsync(app.ProductId, detailCt);
            if (_selected != app || seq != _navSeq || detailCt.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(app.PackageFamilyName) && !string.IsNullOrWhiteSpace(details.PackageFamilyName))
            {
                app.PackageFamilyName = details.PackageFamilyName;
                _ = VerifyProductInstalledAsync(app, detailCt);
            }

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
                var logoBmp = await AppServices.Store.LoadImageAsync(details.LogoUrl, detailCt);
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
                var heroBmp = await AppServices.Store.LoadImageAsync(details.HeroUrl, detailCt);
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
                    var shotBmp = await AppServices.Store.LoadImageAsync(url, detailCt);
                    if (_selected != app || detailCt.IsCancellationRequested) return;
                    if (shotBmp != null) shots.Add(shotBmp);
                }

                if (shots.Count == 0) ScreenshotsSection.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[LOJA] Detalhes de {app.ProductId} falharam: {ex.Message}", LogKind.Warning);
            if (!await AppServices.Store.CheckInternetSilentAsync())
            {
                ShowOfflineScreen(() => OpenProductAsync(app));
            }
        }
        finally
        {
            if (seq == _navSeq) SetTopBusy(false);
        }
    }

    private async Task VerifyProductInstalledAsync(StoreApp app, CancellationToken ct)
    {
        try
        {
            var info = await AppServices.Winget.CheckInstalledAsync(app.ProductId, app.Title, app.PackageFamilyName, ct);
            if (ct.IsCancellationRequested || _selected != app) return;

            _selectedInstalledInfo = info.IsInstalled ? info : null;
            if (!_installingOrUninstalling || InstallProgressPanel.Visibility != Visibility.Visible)
            {
                ApplyInstalledUiState(info.IsInstalled);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppServices.Log.Write($"[LOJA] Falha ao verificar instalacao de {app.ProductId}: {ex.Message}", LogKind.Warning);
        }
    }

    private void ApplyInstalledUiState(bool isInstalled, string? customStatus = null)
    {
        if (isInstalled)
        {
            InstallButton.Visibility = Visibility.Collapsed;
            InstalledActionsPanel.Visibility = Visibility.Visible;
            OpenAppButton.IsEnabled = true;
            UninstallAppButton.IsEnabled = true;
            InstallStatus.Text = customStatus ?? "Este aplicativo já está instalado neste computador.";
        }
        else
        {
            InstalledActionsPanel.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Visible;
            InstallButton.Content = "Obter";
            InstallButton.IsEnabled = true;
            InstallStatus.Text = customStatus ?? "Instalação silenciosa via winget (sem conta Microsoft).";
        }
    }

    // ------------------------------------------------------------------
    // Instalacao (com fallback de ID/nome do winget), Abertura e Desinstalacao
    // ------------------------------------------------------------------
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _installingOrUninstalling) return;

        var app = _selected;
        if (!await EnsureOnlineOrShowOfflineAsync(() => OpenProductAsync(app))) return;

        if (!AppServices.System.WingetInstalled)
        {
            UpdateWingetState();
            InstallStatus.Text = "Instale o App Installer (winget) no aviso acima para continuar.";
            return;
        }

        _installingOrUninstalling = true;
        SetTopBusy(true);
        InstallButton.IsEnabled = false;
        InstallButton.Content = "Instalando...";
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallStatus.Text = $"Baixando e instalando {app.Title} em segundo plano...";

        try
        {
            var success = await AppServices.Winget.InstallStoreAppAsync(app, CancellationToken.None);

            if (success)
            {
                AppServices.Log.Write($"[LOJA] {app.Title} ({app.ProductId}) instalado.", LogKind.Applied);
                var info = await AppServices.Winget.CheckInstalledAsync(app.ProductId, app.Title, app.PackageFamilyName);
                if (_selected == app)
                {
                    _selectedInstalledInfo = info;
                    ApplyInstalledUiState(isInstalled: true, $"✓ {app.Title} instalado com sucesso.");
                }
            }
            else if (_selected == app)
            {
                if (!await AppServices.Store.CheckInternetSilentAsync())
                {
                    ShowOfflineScreen(() => OpenProductAsync(app));
                    return;
                }

                ApplyInstalledUiState(
                    isInstalled: false,
                    $"Não foi possível concluir a instalação de {app.Title} (detalhes registrados nos logs).");
                InstallButton.Content = "Tentar novamente";
            }
        }
        catch (Exception ex)
        {
            if (_selected == app)
            {
                ApplyInstalledUiState(isInstalled: false, "Erro durante a instalação: " + ex.Message);
            }
            AppServices.Log.Write($"[LOJA] Erro instalando {app.ProductId}: {ex.Message}", LogKind.Failed);
        }
        finally
        {
            _installingOrUninstalling = false;
            SetTopBusy(false);
            InstallProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
        }
    }

    private async void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var app = _selected;

        InstallStatus.Text = $"Abrindo {app.Title}...";
        var opened = await AppServices.Winget.LaunchInstalledAsync(
            app.ProductId,
            app.Title,
            app.PackageFamilyName,
            _selectedInstalledInfo);

        if (_selected == app)
        {
            InstallStatus.Text = opened
                ? $"{app.Title} iniciado."
                : $"Não foi possível localizar o atalho de abertura automática de {app.Title}. Abra pelo Menu Iniciar.";
        }
    }

    private async void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _installingOrUninstalling) return;
        var app = _selected;

        var confirmed = await AppServices.ShowDialogAsync(
            $"Desinstalar {app.Title}?",
            $"O aplicativo \"{app.Title}\" e seus pacotes associados serão removidos deste computador.",
            primaryButtonText: "Desinstalar",
            closeButtonText: "Cancelar");

        if (!confirmed || _selected != app) return;

        _installingOrUninstalling = true;
        SetTopBusy(true);
        OpenAppButton.IsEnabled = false;
        UninstallAppButton.IsEnabled = false;
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallStatus.Text = $"Desinstalando {app.Title} em segundo plano...";

        try
        {
            var removed = await AppServices.Winget.UninstallAsync(
                app.ProductId,
                app.Title,
                _selectedInstalledInfo,
                CancellationToken.None);

            var recheck = await AppServices.Winget.CheckInstalledAsync(app.ProductId, app.Title, app.PackageFamilyName);
            var stillInstalled = !removed && recheck.IsInstalled;

            if (_selected == app)
            {
                _selectedInstalledInfo = stillInstalled ? recheck : null;
                ApplyInstalledUiState(
                    isInstalled: stillInstalled,
                    stillInstalled
                        ? $"Não foi possível concluir a desinstalação automática de {app.Title}."
                        : $"✓ {app.Title} foi desinstalado deste computador.");
            }
        }
        catch (Exception ex)
        {
            if (_selected == app)
            {
                OpenAppButton.IsEnabled = true;
                UninstallAppButton.IsEnabled = true;
                InstallStatus.Text = "Erro durante a desinstalação: " + ex.Message;
            }
            AppServices.Log.Write($"[LOJA] Erro desinstalando {app.ProductId}: {ex.Message}", LogKind.Failed);
        }
        finally
        {
            _installingOrUninstalling = false;
            SetTopBusy(false);
            InstallProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void InstallWinget_Click(object sender, RoutedEventArgs e)
    {
        if (_installingOrUninstalling) return;
        if (!await EnsureOnlineOrShowOfflineAsync(() => ResumeAfterReconnectAsync())) return;

        _installingOrUninstalling = true;
        SetTopBusy(true);
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
            _installingOrUninstalling = false;
            SetTopBusy(false);
            InstallWingetButton.IsEnabled = true;
        }
    }

    private void SetTopBusy(bool busy)
    {
        Motion.SetVisible(TopProgress, busy);
        if (ResultsLoadingPanel != null)
        {
            ResultsLoadingPanel.Visibility = busy && ResultsView.Visibility == Visibility.Visible && ResultsGrid.ItemsSource == null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
