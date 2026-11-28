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
    private StoreApp? _currentHero;
    private List<StoreApp> _lastResults = new();
    private string _lastQuery = string.Empty;
    private bool _busy;
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
        _connectivityTimer.Tick += async (_, _) => await CheckConnectivitySilentAsync();

        Loaded += (_, _) =>
        {
            AppServices.Winget.Progress -= OnWingetProgress;
            AppServices.Winget.Progress += OnWingetProgress;
            _connectivityTimer.Start();
            _ = CheckConnectivitySilentAsync();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Winget.Progress -= OnWingetProgress;
            _connectivityTimer.Stop();
        };
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
        _connectivityTimer.Start();
        _ = AppServices.Winget.WarmInstalledCacheAsync();
        await Task.Run(() => AppServices.System.RefreshWinget());
        UpdateWingetState();

        await CheckConnectivitySilentAsync();
    }

    private async void RetryConnection_Click(object sender, RoutedEventArgs e)
    {
        await CheckConnectivitySilentAsync();
    }

    /// <summary>
    /// Verifica silenciosamente a Internet ao abrir a aba e a cada 5 segundos (sem gravar em logs).
    /// Quando a conexao retorna, recarrega automaticamente a vitrine ou a ultima pesquisa pendente.
    /// </summary>
    private async Task CheckConnectivitySilentAsync()
    {
        if (_checkingNet) return;
        _checkingNet = true;

        try
        {
            var online = await AppServices.Store.CheckInternetSilentAsync();
            _isOnline = online;

            if (!online)
            {
                if (!_showcaseLoaded || (_activeViewMode == "results" && _lastResults.Count == 0))
                {
                    OfflineView.Visibility = Visibility.Visible;
                    ShowcaseView.Visibility = Visibility.Collapsed;
                    ResultsView.Visibility = Visibility.Collapsed;
                    DetailView.Visibility = Visibility.Collapsed;
                }
                return;
            }

            if (OfflineView.Visibility == Visibility.Visible)
            {
                OfflineView.Visibility = Visibility.Collapsed;
                RestoreCurrentViewVisibility();
            }

            if (!_showcaseLoaded && !_busy)
            {
                await LoadShowcaseAsync();
            }
            else if (_activeViewMode == "results" && _lastResults.Count == 0 && !string.IsNullOrWhiteSpace(_lastQuery) && !_busy)
            {
                await SearchAndShowAsync(_lastQuery, ResultsHeader.Text);
            }
        }
        finally
        {
            _checkingNet = false;
        }
    }

    private void RestoreCurrentViewVisibility()
    {
        ShowcaseView.Visibility = _activeViewMode == "showcase" ? Visibility.Visible : Visibility.Collapsed;
        ResultsView.Visibility = _activeViewMode == "results" ? Visibility.Visible : Visibility.Collapsed;
        DetailView.Visibility = _activeViewMode == "detail" ? Visibility.Visible : Visibility.Collapsed;
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
        if (!_isOnline && !_showcaseLoaded)
        {
            OfflineView.Visibility = Visibility.Visible;
            ShowcaseView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Collapsed;
            DetailView.Visibility = Visibility.Collapsed;
            _onNavigationStateChanged?.Invoke();
            return;
        }

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

            if (essentials.Count == 0 && productivity.Count == 0)
            {
                _showcaseLoaded = false;
                OfflineView.Visibility = Visibility.Visible;
                ShowcaseView.Visibility = Visibility.Collapsed;
                return;
            }

            _showcaseLoaded = true;
            OfflineView.Visibility = Visibility.Collapsed;
            if (_activeViewMode == "showcase") ShowcaseView.Visibility = Visibility.Visible;

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
            _lastQuery = string.Empty;
            _lastResults = items;
            ApplyResultSorting();
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
        IEnumerable<StoreApp> sorted = sortTag switch
        {
            "rating" => _lastResults.OrderByDescending(r => r.Rating).ThenByDescending(r => r.RatingCount),
            "name" => _lastResults.OrderBy(r => r.Title),
            _ => _lastResults
                .OrderByDescending(r => !string.IsNullOrEmpty(_lastQuery) && r.Title.StartsWith(_lastQuery, StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Title)
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
        if (_busy || string.IsNullOrWhiteSpace(query)) return;

        query = query.Trim();
        _lastQuery = query;
        _returnMode = "results";
        ResultsHeader.Text = heading;

        if (!await AppServices.Store.CheckInternetSilentAsync())
        {
            _isOnline = false;
            _activeViewMode = "results";
            _lastResults.Clear();
            OfflineView.Visibility = Visibility.Visible;
            ShowcaseView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Collapsed;
            DetailView.Visibility = Visibility.Collapsed;
            _onNavigationStateChanged?.Invoke();
            return;
        }

        _isOnline = true;
        ResultsStatus.Text = "Consultando a Microsoft Store...";
        EmptyResults.Visibility = Visibility.Collapsed;
        ResultsGrid.ItemsSource = null;
        ShowView("results");

        SetBusy(true);
        AppServices.Log.Write($"[LOJA] Consultando Store por '{query}'...", LogKind.Info);

        try
        {
            var results = await AppServices.Store.SearchAsync(query, CancellationToken.None);

            _lastQuery = query;
            _lastResults = results;
            ApplyResultSorting();
            var ordered = (ResultsGrid.ItemsSource as List<StoreApp>) ?? results;

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
        _detailCheckCts?.Cancel();
        var checkCts = new CancellationTokenSource();
        _detailCheckCts = checkCts;
        var ct = checkCts.Token;

        _selected = app;
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
        _ = VerifyProductInstalledAsync(app, ct);

        SetBusy(true);
        try
        {
            var details = await AppServices.Store.GetDetailsAsync(app.ProductId, ct);
            if (_selected != app || ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(app.PackageFamilyName) && !string.IsNullOrWhiteSpace(details.PackageFamilyName))
            {
                app.PackageFamilyName = details.PackageFamilyName;
                _ = VerifyProductInstalledAsync(app, ct);
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
        catch (OperationCanceledException)
        {
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

    private async Task VerifyProductInstalledAsync(StoreApp app, CancellationToken ct)
    {
        try
        {
            var info = await AppServices.Winget.CheckInstalledAsync(app.ProductId, app.Title, app.PackageFamilyName, ct);
            if (ct.IsCancellationRequested || _selected != app) return;

            _selectedInstalledInfo = info.IsInstalled ? info : null;
            if (!_busy || InstallProgressPanel.Visibility != Visibility.Visible)
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
    // Instalacao, Abertura e Desinstalacao em segundo plano
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
            SetBusy(false);
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
        if (_selected == null || _busy) return;
        var app = _selected;

        var confirmed = await AppServices.ShowDialogAsync(
            $"Desinstalar {app.Title}?",
            $"O aplicativo \"{app.Title}\" e seus pacotes associados serão removidos deste computador.",
            primaryButtonText: "Desinstalar",
            closeButtonText: "Cancelar");

        if (!confirmed || _selected != app) return;

        SetBusy(true);
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
            SetBusy(false);
            InstallProgressPanel.Visibility = Visibility.Collapsed;
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
