using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeLauncher.Models;
using VibeLauncher.Services;
using Microsoft.Win32;

namespace VibeLauncher;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VersionEntry> _availableVersions = new();
    private readonly ObservableCollection<InstalledVersionVm> _installedVersions = new();
    private readonly ObservableCollection<InstanceInfo> _instances = new();
    private readonly ObservableCollection<LaunchTarget> _homeTargets = new();
    private readonly ObservableCollection<ModrinthHit> _modHits = new();
    private readonly ObservableCollection<ModFileVm> _mods = new();
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly ObservableCollection<CrashFinding> _findings = new();
    private readonly ObservableCollection<CrashFinding> _homeFindings = new();
    private readonly ObservableCollection<JavaInfoVm> _javaList = new();

    private readonly DispatcherTimer _loaderTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };

    private VersionManifest? _manifest;
    private SkinProfile? _skin;
    private InstanceInfo? _selectedInstance;
    private LogEntry? _selectedLog;
    private CancellationTokenSource? _loginCts;
    private bool _busy;
    private bool _initializing = true;
    private bool _suppressCombo;

    private static readonly string[] FallbackMcVersions =
    {
        "1.21.4", "1.21.3", "1.21.1", "1.21",
        "1.20.6", "1.20.4", "1.20.1", "1.20",
        "1.19.4", "1.19.2", "1.19",
        "1.18.2", "1.17.1", "1.16.5", "1.16.1",
        "1.15.2", "1.14.4", "1.13.2", "1.12.2",
        "1.11.2", "1.10.2", "1.9.4", "1.8.9", "1.7.10"
    };

    public MainWindow()
    {
        InitializeComponent();

        InstalledVersionsList.ItemsSource = _installedVersions;
        AvailableVersionsList.ItemsSource = _availableVersions;
        InstancesList.ItemsSource = _instances;
        HomeTargetCombo.ItemsSource = _homeTargets;
        HomeTargetCombo.DisplayMemberPath = "Display";
        ModResultsList.ItemsSource = _modHits;
        ModsList.ItemsSource = _mods;
        LogsList.ItemsSource = _logs;
        HomeFindingsList.ItemsSource = _homeFindings;
        LogFindingsList.ItemsSource = _findings;
        SkinInstanceCombo.DisplayMemberPath = "Name";
        SkinInstanceCombo.ItemsSource = _instances;
        JavaCombo.DisplayMemberPath = "Display";
        SettingsJavaCombo.DisplayMemberPath = "Display";

        LoaderTypeCombo.ItemsSource = new[] { "Fabric", "Quilt", "Forge", "NeoForge" };
        LoaderTypeCombo.SelectedIndex = 0;
        NewInstanceLoader.ItemsSource = new[] { "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" };
        NewInstanceLoader.SelectedIndex = 1;
        SkinProviderCombo.ItemsSource = new[] { "Mojang", "Ely.by", "TLauncher (сайт)", "TLauncher (локально)" };
        SkinProviderCombo.SelectedIndex = 0;
        SkinModelCombo.ItemsSource = new[] { "Classic (Steve)", "Slim (Alex)" };
        SkinModelCombo.SelectedIndex = 0;

        _loaderTimer.Tick += async (_, _) =>
        {
            _loaderTimer.Stop();
            await RefreshLoaderVersionsAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // ═════════════════════════════ INITIALIZATION ═════════════════════════════

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SettingsService.Load();
        ApplySettingsToUi();
        DataPathText.Text = Paths.Root;

        RefreshAccounts();
        RefreshInstances();
        RefreshInstalledVersions();
        RefreshLogsList();
        ShowPage("home");

        PopulateMinecraftVersions();
        await LoadManifestAsync();
        PopulateMinecraftVersions();
        await DetectJavaAsync(true);

        _initializing = false;

        if (!string.IsNullOrWhiteSpace(SettingsService.Current.SkinNickname))
            SkinNicknameBox.Text = SettingsService.Current.SkinNickname;

        UpdateJavaRecommendation();
    }

    private void ApplySettingsToUi()
    {
        _initializing = true;
        var s = SettingsService.Current;

        RamSlider.Value = Math.Clamp(s.RamMb, 1024, 32768);
        RamValueText.Text = $"{s.RamMb} МБ";
        StatsRam.Text = s.RamMb >= 1024 ? $"{s.RamMb / 1024.0:0.#} ГБ" : $"{s.RamMb} МБ";
        JvmArgsBox.Text = s.JvmArgs;
        WidthBox.Text = s.WindowWidth.ToString();
        HeightBox.Text = s.WindowHeight.ToString();
        FullscreenCheck.IsChecked = s.Fullscreen;
        MsClientIdBox.Text = s.MicrosoftClientId;
        GameDirBox.Text = s.GameDirectory;
        JavaPathBox.Text = s.JavaPath;
        ShowSnapshotsCheck.IsChecked = s.ShowSnapshots;
        SnapshotsCheck.IsChecked = s.ShowSnapshots;
        SkinNicknameBox.Text = s.SkinNickname;

        var index = SkinProviderCombo.Items.IndexOf(s.SkinProvider);
        SkinProviderCombo.SelectedIndex = index >= 0 ? index : 0;
        SkinModelCombo.SelectedIndex = s.SkinModel == "slim" ? 1 : 0;

        _initializing = false;
    }

    // ═════════════════════════════ UI HELPERS ═════════════════════════════

    private void ShowPage(string page)
    {
        PageHome.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        PageVersions.Visibility = page == "versions" ? Visibility.Visible : Visibility.Collapsed;
        PageInstances.Visibility = page == "instances" ? Visibility.Visible : Visibility.Collapsed;
        PageSkins.Visibility = page == "skins" ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        HighlightNav(NavHome, page == "home");
        HighlightNav(NavVersions, page == "versions");
        HighlightNav(NavInstances, page == "instances");
        HighlightNav(NavSkins, page == "skins");
        HighlightNav(NavLogs, page == "logs");
        HighlightNav(NavSettings, page == "settings");

        if (page == "logs") RefreshLogsList();
        if (page == "instances") RefreshInstances();
        if (page == "skins") RefreshInstances();
    }

    private void HighlightNav(Button button, bool active)
    {
        button.Background = active
            ? new SolidColorBrush(Color.FromRgb(0x25, 0x1F, 0x3F))
            : Brushes.Transparent;
        button.Foreground = active
            ? (Brush)FindResource("TextBrush")
            : (Brush)FindResource("MutedBrush");
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        PlayButton.IsEnabled = !busy;
        InstancePlayButton.IsEnabled = !busy;
        InstallLoaderButton.IsEnabled = !busy;
        InstallProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VersionsProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
        {
            InstallProgress.Value = 0;
            VersionsProgress.Value = 0;
        }
    }

    private bool TryBeginBusy()
    {
        if (_busy)
        {
            StatusDetail.Text = "Дождитесь завершения текущей операции";
            return false;
        }
        SetBusy(true);
        return true;
    }

    private void UpdateProgress(ProgressInfo info)
    {
        StatusText.Text = string.IsNullOrWhiteSpace(info.Stage) ? "Работа…" : info.Stage;
        StatusDetail.Text = info.Detail;
        VersionsStatus.Text = info.Stage + (string.IsNullOrWhiteSpace(info.Detail) ? "" : " · " + info.Detail);

        var value = Math.Clamp(info.Fraction * 100, 0, 100);
        if (info.Fraction > 0)
        {
            InstallProgress.Value = value;
            VersionsProgress.Value = value;
        }
    }

    private void Fail(string title, Exception ex)
    {
        StatusText.Text = "Ошибка";
        StatusDetail.Text = ex.Message;
        VersionsStatus.Text = ex.Message;
        LogService.WriteCrash(title, ex.ToString());
        MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch { }
    }

    private static BitmapImage? LoadBitmap(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadHead(string? path)
    {
        var bitmap = LoadBitmap(path);
        if (bitmap == null) return null;
        try
        {
            if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16) return bitmap;
            var crop = new CroppedBitmap(bitmap, new Int32Rect(8, 8, 8, 8));
            crop.Freeze();
            return crop;
        }
        catch
        {
            return bitmap;
        }
    }

    // ═════════════════════════════ NAVIGATION ═════════════════════════════

    private void NavHome_Click(object sender, RoutedEventArgs e) => ShowPage("home");
    private void NavVersions_Click(object sender, RoutedEventArgs e) => ShowPage("versions");
    private void NavInstances_Click(object sender, RoutedEventArgs e) => ShowPage("instances");
    private void NavSkins_Click(object sender, RoutedEventArgs e) => ShowPage("skins");
    private void NavLogs_Click(object sender, RoutedEventArgs e) => ShowPage("logs");
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    private void GoLogs_Click(object sender, RoutedEventArgs e) => ShowPage("logs");

    // ═════════════════════════════ ACCOUNTS ═════════════════════════════

    private void RefreshAccounts()
    {
        var account = SettingsService.SelectedAccount;
        AccountName.Text = account?.Username ?? "Не выполнен вход";
        AccountType.Text = account == null
            ? "Войдите, чтобы играть онлайн"
            : account.IsMicrosoft ? "Microsoft · лицензия" : "Офлайн · локальный аккаунт";
        AccountInitial.Text = account?.Initial ?? "?";
        SideAccountName.Text = account?.Username ?? "Не выполнен вход";
        SideAccountType.Text = account?.TypeLabel.ToLowerInvariant() ?? "гость";
        SideInitial.Text = account?.Initial ?? "?";
        LogoutButton.IsEnabled = account != null;
        MsLoginButton.Content = account?.IsMicrosoft == true ? "Аккаунт" : "Microsoft";
    }

    private async void MsLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            var clientId = SettingsService.Current.MicrosoftClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                var entered = await Dialogs.PromptAsync(this, "Вход Microsoft",
                    "Вставьте Application (client) ID из Azure Portal.\nВ приложении должен быть включён «Allow public client flows».",
                    "");
                if (string.IsNullOrWhiteSpace(entered)) return;
                clientId = entered.Trim();
                SettingsService.Current.MicrosoftClientId = clientId;
                SettingsService.Save();
                MsClientIdBox.Text = clientId;
            }

            _loginCts = new CancellationTokenSource();
            ShowOverlay("Вход Microsoft", "Получаем код устройства…", "", null);

            var account = await AuthService.LoginMicrosoftAsync(clientId, info =>
            {
                Dispatcher.Invoke(() =>
                {
                    OverlayMessage.Text = "Откройте страницу microsoft.com/devicelogin и введите код:";
                    OverlayCode.Text = info.UserCode;
                    OverlayLinkButton.Visibility = Visibility.Visible;
                    OverlayLinkButton.Tag = info.VerificationUri;
                    try
                    {
                        Process.Start(new ProcessStartInfo(info.VerificationUri) { UseShellExecute = true });
                    }
                    catch { }
                });
                return Task.CompletedTask;
            }, _loginCts.Token);

            SettingsService.UpsertAccount(account);
            RefreshAccounts();
            HideOverlay();
            StatusText.Text = $"Вход выполнен: {account.Username}";
            StatusDetail.Text = "Аккаунт Microsoft готов к запуску";
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            StatusText.Text = "Вход отменён";
            StatusDetail.Text = "";
        }
        catch (Exception ex)
        {
            HideOverlay();
            Fail("Ошибка входа Microsoft", ex);
        }
        finally
        {
            _loginCts?.Dispose();
            _loginCts = null;
        }
    }

    private async void OfflineLogin_Click(object sender, RoutedEventArgs e)
    {
        var current = SettingsService.SelectedAccount;
        var name = await Dialogs.PromptAsync(this, "Офлайн-аккаунт",
            "Введите никнейм (3–16 символов). Он будет использоваться в игре и на серверах без online-mode.",
            current?.Type == "offline" ? current.Username : "Player");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var account = AuthService.CreateOffline(name);
            SettingsService.UpsertAccount(account);
            RefreshAccounts();
            StatusText.Text = $"Офлайн-аккаунт: {account.Username}";
            StatusDetail.Text = "UUID сгенерирован по нику, как в офлайн-режиме";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Офлайн-аккаунт", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var account = SettingsService.SelectedAccount;
        if (account == null) return;
        if (MessageBox.Show($"Выйти из аккаунта {account.Username}?", "Vibe Launcher",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SettingsService.RemoveAccount(account);
        RefreshAccounts();
        StatusText.Text = "Вы вышли из аккаунта";
        StatusDetail.Text = "";
    }

    // ═════════════════════════════ HOME / LAUNCH ═════════════════════════════

    private void RebuildHomeTargets()
    {
        var previousInstance = _selectedInstance?.Name ?? SettingsService.Current.SelectedInstance;
        var previousVersion = SettingsService.Current.SelectedVersion;

        _suppressCombo = true;
        _homeTargets.Clear();

        foreach (var instance in _instances)
        {
            _homeTargets.Add(new LaunchTarget
            {
                Display = $"Сборка · {instance.Name}   ({instance.Subtitle})",
                VersionId = string.IsNullOrWhiteSpace(instance.VersionId) ? instance.McVersion : instance.VersionId,
                GameDir = instance.Directory,
                IsInstance = true,
                Key = instance.Name
            });
        }

        foreach (var version in _installedVersions)
        {
            _homeTargets.Add(new LaunchTarget
            {
                Display = $"Версия · {version.Id}" + (string.IsNullOrEmpty(version.Kind) ? "" : $"   ({version.Kind})"),
                VersionId = version.Id,
                GameDir = DefaultGameDir(),
                IsInstance = false,
                Key = version.Id
            });
        }

        LaunchTarget? selected = null;
        if (!string.IsNullOrWhiteSpace(previousInstance))
            selected = _homeTargets.FirstOrDefault(t => t.IsInstance && t.Key == previousInstance);
        if (selected == null && !string.IsNullOrWhiteSpace(previousVersion))
            selected = _homeTargets.FirstOrDefault(t => !t.IsInstance && t.Key == previousVersion);
        HomeTargetCombo.SelectedItem = selected ?? _homeTargets.FirstOrDefault();

        _suppressCombo = false;

        if (_homeTargets.Count == 0)
        {
            StatusText.Text = "Нет установленных версий";
            StatusDetail.Text = "Установите версию на вкладке «Версии»";
        }
    }

    private string DefaultGameDir()
    {
        var configured = SettingsService.Current.GameDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try { return Environment.ExpandEnvironmentVariables(configured); } catch { }
        }
        return Path.Combine(Paths.Root, "game");
    }

    private void HomeTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo) return;
        if (HomeTargetCombo.SelectedItem is not LaunchTarget target) return;

        if (target.IsInstance)
        {
            SettingsService.Current.SelectedInstance = target.Key;
            SettingsService.Current.SelectedVersion = "";
        }
        else
        {
            SettingsService.Current.SelectedVersion = target.Key;
            SettingsService.Current.SelectedInstance = "";
        }
        SettingsService.Save();
        UpdateJavaRecommendation();
    }

    private void UpdateJavaRecommendation()
    {
        var mc = GetSelectedMcVersion() ?? "1.21.4";
        var major = JavaService.RecommendedMajor(mc);
        var found = _javaList.FirstOrDefault(j => j.Major == major);
        JavaRecommendText.Text = $"Minecraft {mc}: рекомендуется Java {major}" +
                                 (found != null ? " · найдена" : " · не найдена");
    }

    private string? GetSelectedMcVersion()
    {
        if (HomeTargetCombo.SelectedItem is not LaunchTarget target) return null;
        if (target.IsInstance)
        {
            var instance = _instances.FirstOrDefault(i => i.Name == target.Key);
            return instance?.McVersion;
        }
        var match = Regex.Match(target.Key, @"1\.\d+(?:\.\d+)?");
        return match.Success ? match.Value : target.Key;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (HomeTargetCombo.SelectedItem is not LaunchTarget target)
        {
            MessageBox.Show("Установите версию или создайте сборку.", "Vibe Launcher",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var account = SettingsService.SelectedAccount;
        if (account == null)
        {
            MessageBox.Show("Сначала войдите в аккаунт Microsoft или создайте офлайн-аккаунт.",
                "Нужен аккаунт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var instance = target.IsInstance ? _instances.FirstOrDefault(i => i.Name == target.Key) : null;
        if (instance != null && (string.IsNullOrWhiteSpace(instance.VersionId) ||
                                 !File.Exists(VersionService.VersionJsonPath(instance.VersionId))))
        {
            if (!TryBeginBusy()) return;
            var progress = new Progress<ProgressInfo>(UpdateProgress);
            try
            {
                InstanceStatus.Text = "Установка загрузчика…";
                await InstanceService.InstallLoaderAsync(instance, progress);
                RefreshInstalledVersions();
                RefreshInstances();
                target = _homeTargets.FirstOrDefault(t => t.IsInstance && t.Key == instance.Name) ?? target;
            }
            catch (Exception ex)
            {
                Fail("Не удалось установить сборку", ex);
                return;
            }
            finally
            {
                SetBusy(false);
            }
        }

        await LaunchAsync(target.VersionId, target.GameDir, target.Display);
    }

    private async Task LaunchAsync(string versionId, string gameDir, string label)
    {
        if (_busy) return;
        var account = SettingsService.SelectedAccount;
        if (account == null) return;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            MessageBox.Show("У этой сборки не установлена версия игры.", "Vibe Launcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        StatusText.Text = "Подготовка к запуску…";
        StatusDetail.Text = label;
        var progress = new Progress<ProgressInfo>(UpdateProgress);

        try
        {
            await AuthService.EnsureValidAsync(account);
            Directory.CreateDirectory(gameDir);
            var result = await VersionService.LaunchAsync(versionId, gameDir, account, progress);

            StatusText.Text = result.Cancelled
                ? "Запуск отменён"
                : $"Игра завершилась · код {result.ExitCode}";
            StatusDetail.Text = result.Findings.FirstOrDefault()?.Title ?? result.LogPath;

            ShowFindingsOnHome(result.Findings,
                $"Анализ запуска · {result.VersionId} · {result.EndedAt:HH:mm}");

            RefreshLogsList();
            var log = _logs.FirstOrDefault(l => l.FullPath == result.LogPath);
            if (log != null)
            {
                LogsList.SelectedItem = log;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Запуск отменён";
        }
        catch (Exception ex)
        {
            Fail("Не удалось запустить Minecraft", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowFindingsOnHome(List<CrashFinding> findings, string title)
    {
        _homeFindings.Clear();
        foreach (var finding in findings) _homeFindings.Add(finding);
        HomeAnalysisTitle.Text = title;
        HomeAnalysisPanel.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ═════════════════════════════ VERSIONS ═════════════════════════════

    private async Task LoadManifestAsync(bool refresh = false)
    {
        try
        {
            AvailableStatus.Visibility = Visibility.Visible;
            AvailableStatus.Text = "Загрузка списка версий Mojang…";
            _manifest = await VersionService.GetManifestAsync(refresh);
            ApplyVersionFilter();
        }
        catch (Exception ex)
        {
            AvailableStatus.Visibility = Visibility.Visible;
            AvailableStatus.Text = "Не удалось загрузить список: " + ex.Message;
            LogService.WriteCrash("Manifest", ex.ToString());
        }
    }

    private void ApplyVersionFilter()
    {
        if (_manifest == null) return;

        var filter = VersionFilterBox.Text.Trim();
        var showSnapshots = SnapshotsCheck.IsChecked == true;
        var items = _manifest.Versions
            .Where(v => showSnapshots || v.Type == "release")
            .Where(v => string.IsNullOrWhiteSpace(filter) ||
                        v.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _availableVersions.Clear();
        foreach (var item in items) _availableVersions.Add(item);

        AvailableStatus.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailableStatus.Text = "Ничего не найдено";
    }

    private void RefreshInstalledVersions()
    {
        var current = SettingsService.Current.SelectedVersion;
        _installedVersions.Clear();
        foreach (var version in VersionService.GetInstalledVersions())
            _installedVersions.Add(version);

        NoInstalledText.Visibility = _installedVersions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsVersions.Text = _installedVersions.Count.ToString();
        RebuildHomeTargets();
    }

    private void VersionFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyVersionFilter();

    private void Snapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var value = sender is CheckBox checkBox
            ? checkBox.IsChecked == true
            : SnapshotsCheck.IsChecked == true;
        _initializing = true;
        SnapshotsCheck.IsChecked = value;
        ShowSnapshotsCheck.IsChecked = value;
        _initializing = false;

        SettingsService.Current.ShowSnapshots = value;
        SettingsService.Save();
        ApplyVersionFilter();
    }

    private async void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        await LoadManifestAsync(true);
    }

    private async void InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (!TryBeginBusy()) return;

        var progress = new Progress<ProgressInfo>(UpdateProgress);
        VersionsStatus.Text = "Установка " + id;
        try
        {
            await VersionService.InstallAsync(id, progress);
            RefreshInstalledVersions();
            VersionsStatus.Text = $"{id} установлена";
            StatusText.Text = "Установка завершена";
            StatusDetail.Text = id;
        }
        catch (Exception ex)
        {
            VersionsStatus.Text = "Ошибка установки: " + ex.Message;
            Fail("Ошибка установки версии", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LaunchInstalledVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _ = LaunchAsync(id, DefaultGameDir(), "Версия · " + id);
    }

    private void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (MessageBox.Show($"Удалить версию {id} и её файлы?", "Удаление версии",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            var dir = VersionService.VersionDir(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            RefreshInstalledVersions();
            VersionsStatus.Text = $"{id} удалена";
        }
        catch (Exception ex)
        {
            Fail("Не удалось удалить версию", ex);
        }
    }

    private string SelectedLoader()
    {
        var value = LoaderTypeCombo.SelectedItem as string ?? "Fabric";
        return value.ToLowerInvariant();
    }

    private void LoaderType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            UpdateLoaderJavaHint();
            return;
        }
        _loaderTimer.Stop();
        _loaderTimer.Start();
        UpdateLoaderJavaHint();
    }

    private void LoaderMc_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLoaderJavaHint();
        if (_initializing) return;
        _loaderTimer.Stop();
        _loaderTimer.Start();
    }

    private string SelectedMcVersion => LoaderMcCombo.SelectedItem as string ?? "";

    private void PopulateMinecraftVersions()
    {
        var versions = _manifest?.Versions
            .Where(v => v.Type == "release")
            .Select(v => v.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (versions.Count == 0) versions = FallbackMcVersions.ToList();

        var previousLoader = LoaderMcCombo.SelectedItem as string;
        var previousNew = NewInstanceMcCombo.SelectedItem as string;

        LoaderMcCombo.ItemsSource = versions;
        NewInstanceMcCombo.ItemsSource = versions;

        LoaderMcCombo.SelectedItem = versions.Contains(previousLoader ?? "")
            ? previousLoader
            : versions.FirstOrDefault(v => v.Equals("1.21.4", StringComparison.OrdinalIgnoreCase)) ?? versions.FirstOrDefault();

        NewInstanceMcCombo.SelectedItem = versions.Contains(previousNew ?? "")
            ? previousNew
            : versions.FirstOrDefault(v => v.Equals("1.21.4", StringComparison.OrdinalIgnoreCase)) ?? versions.FirstOrDefault();
    }

    private void UpdateLoaderJavaHint()
    {
        var mc = SelectedMcVersion;
        if (string.IsNullOrWhiteSpace(mc)) return;
        var major = JavaService.RecommendedMajor(mc);
        JavaRecommendText.Text = $"Minecraft {mc}: требуется Java {major}";
    }

    private async void RefreshLoaderVersions_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLoaderVersionsAsync();
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        var mc = SelectedMcVersion;
        if (string.IsNullOrWhiteSpace(mc)) return;
        var loader = SelectedLoader();

        try
        {
            LoaderStatus.Text = $"{LoaderService.LoaderLabel(loader)} · получение версий…";
            var versions = loader switch
            {
                "fabric" => await LoaderService.GetFabricLoaderVersionsAsync(),
                "quilt" => await LoaderService.GetQuiltLoaderVersionsAsync(),
                "forge" => await LoaderService.GetForgeVersionsAsync(mc),
                "neoforge" => await LoaderService.GetNeoForgeVersionsAsync(mc),
                _ => new List<string>()
            };

            LoaderVersionCombo.ItemsSource = versions;
            if (versions.Count > 0 && string.IsNullOrWhiteSpace(LoaderVersionCombo.Text))
                LoaderVersionCombo.SelectedIndex = 0;

            LoaderStatus.Text = versions.Count > 0
                ? $"{LoaderService.LoaderLabel(loader)} · {versions.Count} версий для {mc}"
                : $"Для {mc} версии {LoaderService.LoaderLabel(loader)} не найдены";

            UpdateLoaderJavaHint();
        }
        catch (Exception ex)
        {
            LoaderStatus.Text = "Ошибка: " + ex.Message;
        }
    }

    private async void InstallLoader_Click(object sender, RoutedEventArgs e)
    {
        var mc = SelectedMcVersion;
        if (string.IsNullOrWhiteSpace(mc))
        {
            MessageBox.Show("Выберите версию Minecraft.", "Загрузчик", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var loader = SelectedLoader();
        var loaderVersion = LoaderVersionCombo.SelectedItem as string ?? LoaderVersionCombo.Text.Trim();
        if (!TryBeginBusy()) return;

        var progress = new Progress<ProgressInfo>(UpdateProgress);
        InstallLoaderButton.IsEnabled = false;
        LoaderStatus.Text = "Установка…";
        try
        {
            var id = loader switch
            {
                "fabric" => await LoaderService.InstallFabricAsync(mc, loaderVersion, progress),
                "quilt" => await LoaderService.InstallQuiltAsync(mc, loaderVersion, progress),
                "forge" => await LoaderService.InstallForgeAsync(mc, loaderVersion, progress),
                "neoforge" => await LoaderService.InstallNeoForgeAsync(mc, loaderVersion, progress),
                _ => throw new InvalidOperationException("Неизвестный загрузчик")
            };

            RefreshInstalledVersions();
            RefreshInstances();
            LoaderStatus.Text = $"Установлено: {id}";
            StatusText.Text = "Загрузчик установлен";
            StatusDetail.Text = id;
        }
        catch (Exception ex)
        {
            LoaderStatus.Text = "Ошибка: " + ex.Message;
            Fail("Ошибка установки загрузчика", ex);
        }
        finally
        {
            InstallLoaderButton.IsEnabled = true;
            SetBusy(false);
        }
    }

    // ═════════════════════════════ JAVA ═════════════════════════════

    private async Task DetectJavaAsync(bool updateStatus)
    {
        if (updateStatus) JavaStatus.Text = "Поиск Java…";
        try
        {
            var javas = await JavaService.DetectAsync();
            _javaList.Clear();
            foreach (var java in javas) _javaList.Add(java);

            JavaCombo.ItemsSource = _javaList;
            SettingsJavaCombo.ItemsSource = _javaList;

            var preferred = SettingsService.Current.JavaPath;
            var selected = _javaList.FirstOrDefault(j => j.Path.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                           ?? _javaList.FirstOrDefault();
            JavaCombo.SelectedItem = selected;
            SettingsJavaCombo.SelectedItem = selected;

            if (selected != null)
            {
                StatsJava.Text = "Java " + selected.Major;
                SettingsJavaStatus.Text = $"Найдено установок: {_javaList.Count}";
            }
            else
            {
                StatsJava.Text = "нет";
                SettingsJavaStatus.Text = "Java не найдена — скачайте её кнопками ниже";
            }

            if (updateStatus && selected != null)
                JavaStatus.Text = $"Выбрана Java {selected.Major} ({selected.Version}); для версии игры подбирается рекомендованная";

            UpdateJavaRecommendation();
        }
        catch (Exception ex)
        {
            JavaStatus.Text = "Ошибка поиска Java: " + ex.Message;
        }
    }

    private void JavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (JavaCombo.SelectedItem is JavaInfoVm java)
        {
            SettingsService.Current.JavaPath = java.Path;
            SettingsService.Save();
            JavaPathBox.Text = java.Path;
            JavaStatus.Text = $"Выбрана Java {java.Major}: {java.Path}";
        }
    }

    private void SettingsJavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (SettingsJavaCombo.SelectedItem is JavaInfoVm java)
        {
            SettingsService.Current.JavaPath = java.Path;
            SettingsService.Save();
            JavaPathBox.Text = java.Path;
        }
    }

    private async void DetectJava_Click(object sender, RoutedEventArgs e) => await DetectJavaAsync(true);

    private async void DetectJavaSettings_Click(object sender, RoutedEventArgs e)
    {
        await DetectJavaAsync(false);
        SettingsJavaStatus.Text = $"Найдено установок: {_javaList.Count}";
    }

    private async void DownloadJava_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string majorText } || !int.TryParse(majorText, out var major)) return;
        if (!TryBeginBusy()) return;

        var progress = new Progress<ProgressInfo>(UpdateProgress);
        try
        {
            var path = await JavaService.DownloadRuntimeAsync(major, progress);
            SettingsService.Current.JavaPath = path;
            SettingsService.Save();
            JavaPathBox.Text = path;
            JavaStatus.Text = $"Java {major} скачана: {path}";
            await DetectJavaAsync(false);
            UpdateJavaRecommendation();
        }
        catch (Exception ex)
        {
            JavaStatus.Text = "Ошибка загрузки Java: " + ex.Message;
            Fail("Не удалось скачать Java", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DownloadJavaSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string majorText } || !int.TryParse(majorText, out var major)) return;
        if (!TryBeginBusy()) return;

        var progress = new Progress<ProgressInfo>(UpdateProgress);
        try
        {
            var path = await JavaService.DownloadRuntimeAsync(major, progress);
            SettingsService.Current.JavaPath = path;
            SettingsService.Save();
            JavaPathBox.Text = path;
            SettingsJavaStatus.Text = $"Java {major} скачана: {path}";
            await DetectJavaAsync(false);
        }
        catch (Exception ex)
        {
            SettingsJavaStatus.Text = "Ошибка: " + ex.Message;
            Fail("Не удалось скачать Java", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ═════════════════════════════ INSTANCES ═════════════════════════════

    private void RefreshInstances()
    {
        var selectedName = _selectedInstance?.Name ?? SettingsService.Current.SelectedInstance;
        _instances.Clear();
        foreach (var instance in InstanceService.GetAll().OrderByDescending(i => i.CreatedAt))
            _instances.Add(instance);

        StatsInstances.Text = _instances.Count.ToString();

        if (!string.IsNullOrWhiteSpace(selectedName))
            InstancesList.SelectedItem = _instances.FirstOrDefault(i => i.Name == selectedName);

        if (SkinInstanceCombo.SelectedItem == null && _instances.Count > 0)
            SkinInstanceCombo.SelectedIndex = 0;

        RebuildHomeTargets();
    }

    private void InstancesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstancesList.SelectedItem is not InstanceInfo instance) return;
        _selectedInstance = instance;
        ShowInstance(instance);
    }

    private void ShowInstance(InstanceInfo instance)
    {
        NoInstancePanel.Visibility = Visibility.Collapsed;
        InstancePanel.Visibility = Visibility.Visible;
        InstanceTitle.Text = instance.Name;
        InstanceSubtitle.Text = instance.Subtitle;
        InstanceStatus.Text = string.IsNullOrWhiteSpace(instance.VersionId)
            ? "Загрузчик ещё не установлен"
            : "Версия: " + instance.VersionId;
        SkinInstanceCombo.SelectedItem = instance;
        LoadMods();
        _modHits.Clear();
        ModSearchStatus.Text = "Введите запрос и нажмите «Найти»";
        if (instance.Loader == "vanilla")
            ModSearchStatus.Text = "Установите загрузчик модов, чтобы работать с Modrinth";
    }

    private void LoadMods()
    {
        _mods.Clear();
        if (_selectedInstance == null) return;
        foreach (var mod in InstanceService.GetMods(_selectedInstance))
            _mods.Add(mod);
        ModsCount.Text = _mods.Count == 0 ? "пусто" : $"{_mods.Count} шт.";
    }

    private void NewInstanceLoader_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NewInstanceLoaderVersion == null) return;
        var loader = NewInstanceLoader.SelectedItem as string;
        NewInstanceLoaderVersion.IsEnabled = loader != "Vanilla";
        NewInstanceLoaderVersion.ToolTip = loader == "Vanilla"
            ? "Для Vanilla версия загрузчика не нужна"
            : "Оставьте пустым для автоматического выбора";
    }

    private async void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        var name = NewInstanceName.Text.Trim();
        var mc = NewInstanceMcCombo.SelectedItem as string ?? "";
        if (string.IsNullOrWhiteSpace(mc))
        {
            CreateStatus.Text = "Выберите версию Minecraft";
            return;
        }

        var loaderLabel = NewInstanceLoader.SelectedItem as string ?? "Vanilla";
        var loader = loaderLabel.ToLowerInvariant();
        var loaderVersion = NewInstanceLoaderVersion.Text.Trim();
        if (!TryBeginBusy()) return;

        CreateStatus.Text = "Создание сборки…";
        var progress = new Progress<ProgressInfo>(UpdateProgress);
        try
        {
            var instance = await InstanceService.CreateAsync(name, mc, loader, loaderVersion, progress);
            RefreshInstances();
            RefreshInstalledVersions();
            InstancesList.SelectedItem = _instances.FirstOrDefault(i => i.Name == instance.Name);
            CreateStatus.Text = "Сборка создана: " + instance.Name;
            SettingsService.Current.SelectedInstance = instance.Name;
            SettingsService.Save();
            RebuildHomeTargets();
        }
        catch (Exception ex)
        {
            CreateStatus.Text = "Ошибка: " + ex.Message;
            Fail("Не удалось создать сборку", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ImportMrpack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт сборки Modrinth",
            Filter = "Modrinth modpack (*.mrpack)|*.mrpack|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!TryBeginBusy()) return;

        var progress = new Progress<ProgressInfo>(UpdateProgress);
        try
        {
            StatusText.Text = "Импорт сборки…";
            var instance = await ModrinthService.ImportMrpackAsync(dialog.FileName, progress);
            RefreshInstances();
            RefreshInstalledVersions();
            InstancesList.SelectedItem = _instances.FirstOrDefault(i => i.Name == instance.Name);
            StatusText.Text = "Сборка импортирована";
            StatusDetail.Text = instance.Name;
        }
        catch (Exception ex)
        {
            Fail("Не удалось импортировать сборку", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void InstancePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance == null) return;
        if (string.IsNullOrWhiteSpace(_selectedInstance.VersionId))
        {
            if (!TryBeginBusy()) return;
            try
            {
                await InstanceService.InstallLoaderAsync(_selectedInstance, new Progress<ProgressInfo>(UpdateProgress));
                RefreshInstalledVersions();
                RefreshInstances();
            }
            catch (Exception ex)
            {
                Fail("Не удалось установить сборку", ex);
                return;
            }
            finally
            {
                SetBusy(false);
            }
        }

        await LaunchAsync(_selectedInstance.VersionId, _selectedInstance.Directory, "Сборка · " + _selectedInstance.Name);
    }

    private void InstanceOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance != null) OpenFolder(_selectedInstance.Directory);
    }

    private void InstanceDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance == null) return;
        if (MessageBox.Show($"Удалить сборку «{_selectedInstance.Name}» со всеми модами и мирами?",
                "Удаление сборки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            InstanceService.Delete(_selectedInstance);
            _selectedInstance = null;
            NoInstancePanel.Visibility = Visibility.Visible;
            InstancePanel.Visibility = Visibility.Collapsed;
            RefreshInstances();
            RefreshInstalledVersions();
        }
        catch (Exception ex)
        {
            Fail("Не удалось удалить сборку", ex);
        }
    }

    private async void ModSearch_Click(object sender, RoutedEventArgs e) => await SearchModsAsync();

    private async void ModSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchModsAsync();
    }

    private async Task SearchModsAsync()
    {
        if (_selectedInstance == null) return;
        if (_selectedInstance.Loader == "vanilla")
        {
            ModSearchStatus.Text = "Для Vanilla моды недоступны — установите загрузчик";
            return;
        }

        ModSearchStatus.Text = "Поиск на Modrinth…";
        try
        {
            var result = await ModrinthService.SearchAsync(ModSearchBox.Text.Trim(),
                _selectedInstance.McVersion, _selectedInstance.Loader);
            _modHits.Clear();
            foreach (var hit in result.Hits) _modHits.Add(hit);
            ModSearchStatus.Text = result.Hits.Count == 0
                ? "Ничего не найдено"
                : $"Найдено: {result.TotalHits} · показано {result.Hits.Count}";
        }
        catch (Exception ex)
        {
            ModSearchStatus.Text = "Ошибка поиска: " + ex.Message;
        }
    }

    private async void InstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string projectId }) return;
        if (_selectedInstance == null) return;
        if (!TryBeginBusy()) return;

        var progress = new Progress<string>(text =>
        {
            InstanceStatus.Text = text;
            StatusDetail.Text = text;
        });
        try
        {
            var file = await ModrinthService.InstallModAsync(_selectedInstance, projectId, progress);
            InstanceStatus.Text = "Установлен мод: " + file;
            LoadMods();
        }
        catch (Exception ex)
        {
            InstanceStatus.Text = "Ошибка установки мода: " + ex.Message;
            Fail("Не удалось установить мод", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModFileVm mod }) return;
        if (MessageBox.Show($"Удалить мод {mod.FileName}?", "Удаление мода",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            InstanceService.RemoveMod(mod);
            LoadMods();
        }
        catch (Exception ex)
        {
            Fail("Не удалось удалить мод", ex);
        }
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance != null)
            OpenFolder(Path.Combine(_selectedInstance.Directory, "mods"));
    }

    private async void InstallCsl_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedInstance == null) return;
        if (!TryBeginBusy()) return;

        try
        {
            var progress = new Progress<string>(text => InstanceStatus.Text = text);
            var file = await SkinService.InstallCustomSkinLoaderAsync(_selectedInstance, progress);
            InstanceStatus.Text = "CustomSkinLoader установлен: " + file;
            LoadMods();
        }
        catch (Exception ex)
        {
            Fail("Не удалось установить CustomSkinLoader", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ═════════════════════════════ SKINS ═════════════════════════════

    private void SkinProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkinStatus == null) return;
        var index = SkinProviderCombo.SelectedIndex;
        SkinStatus.Text = index switch
        {
            0 => "Mojang: загрузка по никнейму через официальный API.",
            1 => "Ely.by: скины и плащи по никнейму.",
            2 => "TLauncher: скин скачивается с сайта по никнейму. Вход в аккаунт — на сайте TLauncher.",
            3 => "TLauncher (локально): поиск skin.png и cape.png в папках TLauncher.",
            _ => ""
        };
    }

    private async void LoadSkin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginBusy()) return;

        try
        {
            var provider = SkinProviderCombo.SelectedIndex;
            if (provider == 3)
            {
                var tlauncher = SkinService.ImportFromTLauncher();
                if (tlauncher == null)
                {
                    SkinStatus.Text = "Файлы TLauncher не найдены — выберите PNG вручную";
                    PickAndLoadSkinFile();
                    return;
                }
                DisplaySkin(tlauncher);
                SkinStatus.Text = "Скин загружен из локальной папки TLauncher";
                return;
            }

            var nickname = SkinNicknameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nickname))
            {
                SkinStatus.Text = "Введите никнейм";
                return;
            }

            SkinStatus.Text = provider == 2 ? "Загрузка скина с сайта TLauncher…" : "Загрузка скина…";
            var profile = provider switch
            {
                1 => await SkinService.FetchElyAsync(nickname),
                2 => await SkinService.FetchTLauncherAsync(nickname),
                _ => await SkinService.FetchMojangAsync(nickname)
            };

            // Сайт TLauncher не отдаёт модель скина, поэтому сохраняем выбор пользователя.
            if (provider == 2)
                profile.Model = SkinModelCombo.SelectedIndex == 1 ? "slim" : "classic";

            SettingsService.Current.SkinNickname = profile.Nickname;
            SettingsService.Current.SkinProvider = SkinProviderCombo.SelectedItem as string ?? "Mojang";
            SettingsService.Save();

            DisplaySkin(profile);
            SkinNicknameBox.Text = profile.Nickname;

            SkinStatus.Text = profile.IsDefault
                ? $"У ника «{profile.Nickname}» нет скина на TLauncher — сайт вернул стандартный. Загрузите скин на сайте TLauncher и повторите."
                : $"Загружен скин {profile.Provider}: {profile.Nickname}";
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SkinModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = SkinModelCombo.SelectedIndex == 1 ? "slim" : "classic";
        if (_skin != null) _skin.Model = model;
        UpdateSkinModelText();
        SettingsService.Current.SkinModel = model;
        SettingsService.Save();
    }

    private void UpdateSkinModelText()
    {
        if (SkinModelText == null || _skin == null) return;
        SkinModelText.Text = _skin.Model == "slim"
            ? "Модель: slim (Alex) · плащ: " + (_skin.LocalCapePath != null ? "есть" : "нет")
            : "Модель: classic (Steve) · плащ: " + (_skin.LocalCapePath != null ? "есть" : "нет");
    }

    private void OpenTLauncherSkins_Click(object sender, RoutedEventArgs e)
    {
        var nickname = SkinNicknameBox.Text.Trim();
        var url = string.IsNullOrWhiteSpace(nickname)
            ? SkinService.TLauncherSkinsUrl
            : SkinService.TLauncherSkinsUrl + "?nick=" + Uri.EscapeDataString(nickname);
        OpenExternal(url);
    }

    private void OpenTLauncherLogin_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(SkinService.TLauncherLoginUrl);

    private void OpenTLauncherProfile_Click(object sender, RoutedEventArgs e) =>
        OpenExternal(SkinService.TLauncherProfileUrl);

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Не удалось открыть браузер: " + ex.Message;
        }
    }

    private async void SkinNicknameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        LoadSkin_Click(sender, e);
        await Task.CompletedTask;
    }

    private void DisplaySkin(SkinProfile profile)
    {
        _skin = profile;

        var skinBitmap = LoadBitmap(profile.LocalSkinPath);
        SkinImage.Source = skinBitmap;
        HeadImage.Source = LoadHead(profile.LocalSkinPath);
        CapeImage.Source = LoadBitmap(profile.LocalCapePath);

        SkinNameText.Text = profile.Nickname is "local" ? "Локальный скин" : profile.Nickname;
        SkinProviderText.Text = profile.Provider;
        SkinModelCombo.SelectedIndex = profile.Model == "slim" ? 1 : 0;
        UpdateSkinModelText();

        if (skinBitmap == null && profile.LocalCapePath == null)
            SkinStatus.Text = "Файлы скина не найдены";
    }

    private void TlauncherImport_Click(object sender, RoutedEventArgs e)
    {
        var profile = SkinService.ImportFromTLauncher();
        if (profile == null)
        {
            SkinStatus.Text = "TLauncher-файлы не найдены. Выберите PNG вручную.";
            PickAndLoadSkinFile();
            return;
        }
        DisplaySkin(profile);
        SkinStatus.Text = "Импортировано из локальных файлов TLauncher";
    }

    private void PickSkinFile_Click(object sender, RoutedEventArgs e) => PickAndLoadSkinFile();

    private void PickAndLoadSkinFile()
    {
        var dialog = new OpenFileDialog { Title = "Выберите скин", Filter = "PNG скин (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var profile = SkinService.ImportLocal(dialog.FileName, "skin");
            if (_skin != null) profile.LocalCapePath = _skin.LocalCapePath;
            profile.Model = _skin?.Model ?? SettingsService.Current.SkinModel;
            DisplaySkin(profile);
            SkinStatus.Text = "Локальный скин загружен";
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Ошибка: " + ex.Message;
        }
    }

    private void PickCapeFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Выберите плащ", Filter = "PNG плащ (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var prof = SkinService.ImportLocal(dialog.FileName, "cape");
            _skin ??= new SkinProfile { Provider = "Локальный", Nickname = "local" };
            _skin.LocalCapePath = prof.LocalCapePath;
            DisplaySkin(_skin);
            SkinStatus.Text = "Локальный плащ загружен";
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Ошибка: " + ex.Message;
        }
    }

    private async void ApplySkin_Click(object sender, RoutedEventArgs e)
    {
        if (_skin == null)
        {
            SkinStatus.Text = "Сначала загрузите скин";
            return;
        }
        if (SkinInstanceCombo.SelectedItem is not InstanceInfo instance)
        {
            SkinStatus.Text = "Создайте сборку и выберите её";
            return;
        }
        var account = SettingsService.SelectedAccount;
        if (account == null)
        {
            SkinStatus.Text = "Сначала войдите в аккаунт";
            return;
        }

        try
        {
            await SkinService.ApplyToInstanceAsync(instance, account, _skin);
            SkinStatus.Text = $"Скин записан в «{instance.Name}». Установите CustomSkinLoader, если ещё не установлен.";
            SettingsService.Current.SkinModel = _skin.Model;
            SettingsService.Save();
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Ошибка: " + ex.Message;
        }
    }

    private void SaveSkinAccount_Click(object sender, RoutedEventArgs e)
    {
        var account = SettingsService.SelectedAccount;
        if (account == null || _skin == null)
        {
            SkinStatus.Text = "Нужен аккаунт и загруженный скин";
            return;
        }
        account.SkinModel = _skin.Model;
        SettingsService.Current.SkinModel = _skin.Model;
        SettingsService.Save();
        SkinStatus.Text = "Модель скина сохранена в аккаунте";
    }

    private async void SkinCsl_Click(object sender, RoutedEventArgs e)
    {
        if (SkinInstanceCombo.SelectedItem is not InstanceInfo instance)
        {
            SkinStatus.Text = "Выберите сборку";
            return;
        }
        if (!TryBeginBusy()) return;

        try
        {
            var progress = new Progress<string>(text => SkinStatus.Text = text);
            var file = await SkinService.InstallCustomSkinLoaderAsync(instance, progress);
            SkinStatus.Text = "CustomSkinLoader установлен: " + file;
        }
        catch (Exception ex)
        {
            SkinStatus.Text = "Ошибка: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ═════════════════════════════ LOGS ═════════════════════════════

    private void RefreshLogsList()
    {
        var selectedPath = _selectedLog?.FullPath;
        _logs.Clear();
        foreach (var log in LogService.GetLogs()) _logs.Add(log);
        if (selectedPath != null)
            LogsList.SelectedItem = _logs.FirstOrDefault(l => l.FullPath == selectedPath);
    }

    private void LogsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogsList.SelectedItem is not LogEntry entry) return;
        _selectedLog = entry;
        LogTitle.Text = entry.Name;
        LogStatusText.Text = entry.Subtitle;
        LogTextBox.Text = LogService.ReadLog(entry.FullPath);
        AnalyzeLogText(LogTextBox.Text);
    }

    private void AnalyzeLogText(string text)
    {
        var code = Regex.IsMatch(text, @"Exception|ERROR|FATAL|Caused by|crash", RegexOptions.IgnoreCase) ? 1 : 0;
        var findings = CrashAnalyzer.Analyze(text, code);

        _findings.Clear();
        foreach (var finding in findings) _findings.Add(finding);

        LogAnalysisPanel.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LogStatusText.Text = findings.Count > 0
            ? $"Найдено проблем: {findings.Count} · нажмите на лог для повторного анализа"
            : "Известных проблем не найдено";
    }

    private void AnalyzeLog_Click(object sender, RoutedEventArgs e) => AnalyzeLogText(LogTextBox.Text);

    private void RefreshLogs_Click(object sender, RoutedEventArgs e) => RefreshLogsList();

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Paths.Logs);

    private void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLog == null) return;
        if (MessageBox.Show($"Удалить лог {_selectedLog.Name}?", "Удаление лога",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(_selectedLog.FullPath);
            _selectedLog = null;
            LogTextBox.Text = "";
            LogTitle.Text = "Выберите лог";
            LogStatusText.Text = "";
            _findings.Clear();
            LogAnalysisPanel.Visibility = Visibility.Collapsed;
            RefreshLogsList();
        }
        catch (Exception ex)
        {
            Fail("Не удалось удалить лог", ex);
        }
    }

    // ═════════════════════════════ SETTINGS ═════════════════════════════

    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RamValueText == null) return;
        var ram = (int)Math.Round(e.NewValue);
        RamValueText.Text = $"{ram} МБ";
        if (StatsRam != null) StatsRam.Text = $"{ram / 1024.0:0.#} ГБ";
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsService.Current;
        s.RamMb = (int)Math.Round(RamSlider.Value);
        s.JvmArgs = JvmArgsBox.Text.Trim();
        s.MicrosoftClientId = MsClientIdBox.Text.Trim();
        s.GameDirectory = GameDirBox.Text.Trim();
        s.JavaPath = JavaPathBox.Text.Trim();
        s.Fullscreen = FullscreenCheck.IsChecked == true;
        s.ShowSnapshots = ShowSnapshotsCheck.IsChecked == true;

        s.WindowWidth = int.TryParse(WidthBox.Text.Trim(), out var w) && w >= 320 ? w : 1280;
        s.WindowHeight = int.TryParse(HeightBox.Text.Trim(), out var h) && h >= 240 ? h : 720;
        WidthBox.Text = s.WindowWidth.ToString();
        HeightBox.Text = s.WindowHeight.ToString();

        SettingsService.Save();
        StatsRam.Text = $"{s.RamMb / 1024.0:0.#} ГБ";
        SettingsSavedText.Text = "Сохранено в " + DateTime.Now.ToString("HH:mm:ss");
        UpdateJavaRecommendation();
        RebuildHomeTargets();
    }

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите java.exe",
            Filter = "Java (*.exe)|java.exe|Исполняемые файлы (*.exe)|*.exe"
        };
        if (dialog.ShowDialog(this) == true)
        {
            JavaPathBox.Text = dialog.FileName;
            SettingsService.Current.JavaPath = dialog.FileName;
        }
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Каталог игры" };
        if (dialog.ShowDialog(this) == true)
        {
            GameDirBox.Text = dialog.FolderName;
            SettingsService.Current.GameDirectory = dialog.FolderName;
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Paths.Root);

    // ═════════════════════════════ OVERLAY ═════════════════════════════

    private void ShowOverlay(string title, string message, string code, string? url)
    {
        OverlayTitle.Text = title;
        OverlayMessage.Text = message;
        OverlayCode.Text = code;
        OverlayCode.Visibility = string.IsNullOrWhiteSpace(code) ? Visibility.Collapsed : Visibility.Visible;
        OverlayLinkButton.Visibility = url != null ? Visibility.Visible : Visibility.Collapsed;
        OverlayLinkButton.Tag = url;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        OverlayCode.Text = "";
        OverlayLinkButton.Visibility = Visibility.Collapsed;
    }

    private void OverlayCancel_Click(object sender, RoutedEventArgs e)
    {
        _loginCts?.Cancel();
        HideOverlay();
    }

    private void OverlayLink_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayLinkButton.Tag is string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SettingsService.Save();
    }
}
