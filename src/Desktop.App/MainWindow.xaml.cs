using System.IO;
using System.Windows;
using System.Windows.Controls;

using Desktop.App.Browser;
using Desktop.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

public partial class MainWindow : Window
{
    private readonly string dataRoot;
    private readonly string? smokePhase;
    private readonly EventLog log = new();
    private readonly ProfileRegistry profiles;
    private string activeRoot;
    private SettingsStore settings;
    private TaskStateStore taskStore;
    // 默认配置沿用原有的 .env 解析规则；其他配置优先使用各自目录下的 .env，
    // 尚未保存过时回退到全局配置，保证新建配置立即沿用全部现有功能。
    private string GlobalEnvPath => smokePhase is null
        ? AiConfiguration.ResolvePath(AppContext.BaseDirectory) : Path.Combine(dataRoot, ".env");
    private string envPath
    {
        get
        {
            if (activeRoot == dataRoot) return GlobalEnvPath;
            var local = Path.Combine(activeRoot, ".env");
            return File.Exists(local) ? local : GlobalEnvPath;
        }
    }
    private string envSavePath => activeRoot == dataRoot ? envPath : Path.Combine(activeRoot, ".env");
    // 当前激活账户的宿主与学习代理。切换时仅把引用指向已存在的实例，不重新创建或重新初始化。
    private BrowserHost browser = null!;
    private readonly AccountWebViewPool pool;
    private readonly CancellationTokenSource lifetime = new();
    private AppSettings appSettings = new();
    private bool switchingProfile, applyingSettings;

    public MainWindow(string root, string? phase = null)
    {
        dataRoot = root;
        smokePhase = phase;
        var registry = ProfileRegistry.Load(root);
        profiles = registry.Registry;
        activeRoot = profiles.CurrentDirectory;
        Directory.CreateDirectory(activeRoot);
        settings = new SettingsStore(activeRoot);
        taskStore = new TaskStateStore(activeRoot);
        InitializeComponent();
        pool = new AccountWebViewPool(BrowserStage, CourseBrowser, root, log,
            () => appSettings, InvalidateAnswers, AttachBrowserEvents, GlobalEnvPath,
            // 离线回归使用本地 fixture 作为后台实例的首页，避免预热发起外网请求。
            () => smokePhase is null ? BrowserPolicy.HomeUrl : BrowserPolicy.LocalStartUrl);
        LogList.ItemsSource = log.Entries;
        ProviderBox.ItemsSource = ProviderCatalog.All;
        PlaybackRateBox.ItemsSource = new[] { 1.0, 1.25, 1.5, 2.0, 3.0 };
        ApplySettingsToControls();
        if (registry.Warning is not null) log.Write("WARN", registry.Warning);
        DataPathText.Text = activeRoot;
        LoadModelSettings();
        RefreshProfileTabs();
    }

    private void ApplySettingsToControls()
    {
        var loaded = settings.Load();
        appSettings = loaded.Settings;
        applyingSettings = true;
        try
        {
            ProviderBox.SelectedValue = loaded.Settings.SelectedProvider;
            AdvancePercentSlider.Value = appSettings.AdvanceAtPercent;
            AdvancePercentLabel.Text = $"{appSettings.AdvanceAtPercent}%";
            PlaybackRateBox.SelectedItem = appSettings.PlaybackRate;
            AutoAdvanceBox.IsChecked = appSettings.AutoAdvance;
        }
        finally { applyingSettings = false; }
        if (loaded.Warning is not null) log.Write("WARN", loaded.Warning);
    }

    private void RefreshProfileTabs() => ProfileTabs.ItemsSource = profiles.Profiles
        .Select(entry => new ProfileTab(entry.Id, entry.Name, entry.Id == profiles.CurrentId)).ToArray();

    // 所有原先直接操作 CourseBrowser 的入口都落到当前激活账户的 WebView 实例。
    private WebView2 ActiveView => pool.CurrentHost is null ? CourseBrowser : pool.ActiveView;

    // 后台实例的加载进度不覆盖当前窗口的状态栏；启动阶段实例池尚未激活时照常显示进度。
    private void AttachBrowserEvents(BrowserHost host)
    {
        bool Foreground() => pool.CurrentHost is null || ReferenceEquals(host, pool.CurrentHost);
        host.AddressChanged += _ =>
        {
            if (Foreground() && pool.CurrentHost is not null) BackButton.IsEnabled = pool.ActiveView.CanGoBack;
        };
        host.StatusChanged += message =>
        {
            if (!Foreground()) return;
            StatusText.Text = message;
            if (LoadingPanel.Visibility == Visibility.Visible) LoadingText.Text = message;
        };
        host.TitleChanged += title =>
        {
            if (Foreground()) PageTitleText.Text = string.IsNullOrWhiteSpace(title) ? "等待页面加载…" : title;
        };
    }

    private Task SyncInstancesAsync() =>
        pool.SyncAsync(profiles.Profiles.Select(entry => entry.Id), lifetime.Token);

    private async void ProfileTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) await SwitchProfileAsync(id);
    }

    private async void NewProfileClick(object sender, RoutedEventArgs e)
    {
        var name = ProfileDialogs.PromptName(this, "新建配置");
        if (!string.IsNullOrEmpty(name)) await CreateProfileAsync(name);
    }

    private async void ManageProfileClick(object sender, RoutedEventArgs e)
    {
        ProfileDialogs.Manage(this, profiles,
            SwitchProfileAsync, CreateProfileAsync, DeleteProfileAsync,
            (id, name) =>
            {
                try { profiles.Rename(id, name); RefreshProfileTabs(); return true; }
                catch (InvalidOperationException error)
                { MessageBox.Show(this, error.Message, "重命名配置", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            });
        // 管理窗口内可新建、切换、重命名与删除，关闭后统一校验实例与清单的一致性。
        // 映射以不可变的配置 id 为键，重命名不会重建实例。
        try { await SyncInstancesAsync(); }
        catch (Exception error) { log.Write("WARN", $"实例同步未完成：{error.GetType().Name}"); }
    }

    private async void DeleteProfileClick(object sender, RoutedEventArgs e)
    {
        var current = profiles.Current;
        if (current.IsDefault)
        {
            MessageBox.Show(this, "默认配置不能删除；可在「管理」中删除其他配置。", "删除配置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除配置「{current.Name}」及其独立资料？该操作不可撤销。", "删除配置",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await DeleteProfileAsync(current.Id);
    }

    private async Task CreateProfileAsync(string name)
    {
        try
        {
            var entry = profiles.Create(name);
            log.Write("INFO", $"已新建配置「{entry.Name}」，资料目录独立。");
            RefreshProfileTabs();
            // 新账户的 WebView 在后台隐藏启动并加载主页；随后切换直接复用该实例。
            try { await pool.PreloadAsync(entry.Id, profiles.DirectoryOf(entry.Id), lifetime.Token); }
            catch (Exception preload)
            { log.Write("WARN", $"新配置后台预热未完成，切换时将按需创建：{preload.GetType().Name}"); }
            await SwitchProfileAsync(entry.Id);
        }
        catch (InvalidOperationException error)
        { MessageBox.Show(this, error.Message, "新建配置", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task DeleteProfileAsync(string id)
    {
        if (id == profiles.CurrentId) await SwitchProfileAsync(ProfileRegistry.DefaultId);
        // 切换未成功时不能删除正在使用的配置目录。
        if (id == profiles.CurrentId)
        {
            MessageBox.Show(this, "当前配置切换失败，未执行删除。", "删除配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var name = profiles.Profiles.First(entry => entry.Id == id).Name;
            // 先释放该账户的 WebView（保存 Cookie 并解除资料目录占用），再删除目录与清单项。
            await pool.ReleaseAsync(id, lifetime.Token);
            profiles.Delete(id);
            await SyncInstancesAsync();
            RefreshProfileTabs();
            log.Write("INFO", $"已删除配置「{name}」及其 WebView 实例。");
        }
        catch (InvalidOperationException error)
        { MessageBox.Show(this, error.Message, "删除配置", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // 切换配置：实例池在后台读取配置、确保目标 WebView 已隐藏加载，最后隐藏当前实例、显示目标实例。
    // 目标实例始终复用，不导航、不重载、不重新初始化；触发入口、配置数据、切换状态、
    // 结果回传、异常反馈与边界场景沿用原有流程。
    // 供同程序集的离线回归调用；触发入口与业务流程保持一致。
    internal Task SwitchProfileForTestAsync(string id) => SwitchProfileAsync(id);
    internal ProfileRegistry Profiles => profiles;
    internal StudyBrowser? StudyForTest => study;
    internal BrowserHost BrowserForTest => browser;
    internal int AccountViewCount => pool.Count;
    internal string ActiveAccountId => pool.ActiveId;
    internal WebView2? AccountViewFor(string id) => pool.Find(id)?.View;
    internal bool IsAccountViewActive(string id) => pool.IsActive(id);
    internal string? AccountViewState(string id) => pool.Find(id)?.State.ToString();
    internal Task ReleaseAccountViewAsync(string id) => pool.ReleaseAsync(id, lifetime.Token);
    internal Task SyncInstancesForTestAsync() => SyncInstancesAsync();

    private async Task SwitchProfileAsync(string id)
    {
        if (switchingProfile || id == profiles.CurrentId || !profiles.Profiles.Any(entry => entry.Id == id)) return;
        if (!pool.IsReady)
        {
            MessageBox.Show(this, "浏览器尚在初始化，请稍后再切换配置。", "切换配置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        switchingProfile = true;
        // 记录切出前的播放开关：自动流程属于会话状态，切换后需要延续而不是静默停掉。
        var wasPlaying = playing;
        // 课程流程绑定当前账户的页面；切换账户前停止，避免用新账户页面判断旧课程的完成情况。
        courseWorkflow.Stop();
        CourseFlowStatusText.Text = "切换账户后课程流程已停止；如需继续请重新执行";
        var previousRoot = activeRoot;
        var previousId = profiles.CurrentId;
        var stateSwitched = false;
        try
        {
            var target = profiles.DirectoryOf(id);
            // ① 实例池：后台读取目标配置与快照；已存在的 WebView 直接复用，否则后台隐藏创建并加载。
            var result = await pool.SwitchAsync(new AccountSwitchRequest(previousId, id, target), lifetime.Token);
            // ② 后台更新配置清单与数据归属；不刷新设置控件、不重新注入网页配置。
            await Task.Run(() => profiles.Select(id), lifetime.Token);
            activeRoot = target;
            // ③ 按账户隔离自动答题缓存与进度，避免 A 账户的答案被 B 账户复用。
            CaptureStudyState(previousId);
            RestoreStudyState(id);
            stateSwitched = true;
            browser = pool.ActiveHost;
            study = pool.ActiveStudy;
            settings = new SettingsStore(activeRoot);
            taskStore = new TaskStateStore(activeRoot);
            // ④ 结果回传：只更新标签高亮与日志；顺带读取目标实例已有的标题/后退状态，不触发任何加载。
            RefreshProfileTabs();
            var activeTitle = pool.ActiveView.CoreWebView2?.DocumentTitle;
            PageTitleText.Text = string.IsNullOrWhiteSpace(activeTitle) ? "等待页面加载…" : activeTitle;
            BackButton.IsEnabled = pool.ActiveView.CanGoBack;
            if (result.Warning is not null) log.Write("WARN", result.Warning);
            log.Write("INFO", $"已切换到配置「{profiles.Current.Name}」的 WebView（{(result.Reused ? "复用实例" : "后台新建实例")}，共 {result.InstanceCount} 个实例）。");
            // 后台实例是隐藏创建的；激活时显式取得前台焦点，唤醒 Chromium 渲染与媒体，
            // 只改控件焦点，不导航、不重载、不重建实例。
            if (smokePhase is null)
                try { pool.ActiveView.Focus(); } catch (Exception) { }
            if (wasPlaying)
            {
                // 会话状态延续：只恢复播放开关，不导航、不重载、不重新初始化实例。
                playing = true;
                CaptureStudyState(id);
                StudyStatusText.Text = $"已切换到配置「{profiles.Current.Name}」，自动播放已接续";
                log.Write("INFO", "切换配置后自动播放已接续，目标账户页面未重载。");
            }
            else
            {
                StudyStatusText.Text = $"已切换到配置「{profiles.Current.Name}」；当前配置未启动自动播放，点「开始自动播放」启动";
            }
        }
        catch (Exception error)
        {
            // 与原有流程一致：回滚显隐、数据目录与清单、记录异常并提示；不重载页面或重新初始化。
            try { pool.Activate(previousId); }
            catch (Exception rollbackView) { log.Write("WARN", $"切换回滚时恢复显示未完成：{rollbackView.GetType().Name}"); }
            browser = pool.CurrentHost ?? browser;
            study = pool.ActiveStudy;
            if (stateSwitched) RestoreStudyState(previousId);
            activeRoot = previousRoot;
            settings = new SettingsStore(previousRoot);
            taskStore = new TaskStateStore(previousRoot);
            try { await Task.Run(() => profiles.Select(previousId), CancellationToken.None); }
            catch (Exception rollback) { log.Write("WARN", $"切换回滚未完成：{rollback.GetType().Name}"); }
            RefreshProfileTabs();
            log.Write("ERROR", $"切换配置失败：{error.GetType().Name}");
            MessageBox.Show(this, "切换配置失败，已保留当前配置。详情请查看日志。", "切换配置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { switchingProfile = false; }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 实例池创建共享 environment 与当前账户的主 WebView（复用 CourseBrowser），其余账户按需后台预热。
            await pool.InitializeAsync(profiles.CurrentId, activeRoot, lifetime.Token);
            browser = pool.ActiveHost;
            await InitializeStudyAsync();
            if (lifetime.IsCancellationRequested) return;
            if (DumpTarget is not null)
            {
                browser.Navigate(DumpUrl ?? BrowserPolicy.HomeUrl);
                await Task.Delay(12000);
                await DumpPageAsync(DumpTarget);
                Close();
                return;
            }
            LoadingPanel.Visibility = Visibility.Collapsed;
            foreach (var button in new[] { ReloadButton, HomeButton, MuteButton, StopButton, ClearLoginButton })
                button.IsEnabled = true;
            if (smokePhase is not null)
            {
                await SmokeTest.RunAsync(this, browser, CourseBrowser, dataRoot, smokePhase);
                Close();
                return;
            }
            browser.Navigate(BrowserPolicy.HomeUrl);
            RestoreCourseButton.Visibility = taskStore.Load() is { } saved && BrowserPolicy.IsAllowed(saved.CourseUrl) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception error)
        {
            if (lifetime.IsCancellationRequested) return;
            LoadingText.Text = error is TimeoutException
                ? "浏览器初始化超时。请关闭程序后重试；如仍失败，请查看本机数据目录的 browser-startup.log 和 startup-error.log。Cookie 保留。"
                : "浏览器初始化失败。请重新解压完整部署包，检查本机缓存目录权限；详情见本机数据目录的启动日志。Cookie 保留。";
            StatusText.Text = "浏览器暂不可用，设置仍可使用";
            log.Write("ERROR", $"初始化失败：{error.GetType().Name}（0x{error.HResult:X8}）");
            var detail = $"{DateTimeOffset.Now:O}{Environment.NewLine}{error}";
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), detail); } catch { }
            try { File.WriteAllText(Path.Combine(dataRoot, "startup-error.log"), detail); } catch { }
            if (smokePhase is not null)
            {
                SmokeTest.WriteFailure(dataRoot, smokePhase, error);
                Close();
            }
        }
    }

    private void BackClick(object sender, RoutedEventArgs e) { if (ActiveView.CanGoBack) ActiveView.GoBack(); }
    private void ReloadClick(object sender, RoutedEventArgs e)
    {
        if (!browser.IsReady) return;
        // 后台实例加载失败时，用户显式刷新会重新加载该账户主页；切换流程本身绝不自动导航。
        if (pool.Find(pool.ActiveId)?.State == AccountViewLoadState.Failed) pool.Retry(pool.ActiveId);
        else ActiveView.Reload();
    }
    private void HomeClick(object sender, RoutedEventArgs e) => browser.Navigate(BrowserPolicy.HomeUrl);
    private async void StopClick(object sender, RoutedEventArgs e)
    {
        // 停止按钮同时结束课程级编排，避免停止后仍继续轮询并弹出完成提示。
        courseWorkflow.Stop();
        await StopStudyAsync();
        browser.StopLoading();
    }

    private void MuteClick(object sender, RoutedEventArgs e)
    {
        if (!browser.IsReady) return;
        var core = ActiveView.CoreWebView2;
        core.IsMuted = !core.IsMuted;
        MuteButton.Content = core.IsMuted ? "取消静音" : "静音";
    }
    private void RestoreCourseClick(object sender, RoutedEventArgs e)
    {
        if (taskStore.Load() is { } saved) browser.Navigate(saved.CourseUrl);
    }
    private string SelectedProvider => ProviderBox.SelectedValue as string ?? "zhipu";

    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        ApiKeyBox.Clear();
        ApplyProviderDefaults(SelectedProvider);
        UpdateKeyStatus();
    }

    // 原脚本切换 Provider 时会同时切换模型与接口。这里只在字段为空或仍是某个 Provider 的默认值时带出，
    // 用户手填的地址和模型保持不变；实际生效值仍以 .env 为准，保存规则不变。
    private void ApplyProviderDefaults(string provider)
    {
        var definition = ProviderCatalog.Find(provider);
        if (definition is null || EndpointBox is null || ModelBox is null) return;
        if (EndpointBox.Text.Length == 0 || ProviderCatalog.All.Any(item => item.Endpoint == EndpointBox.Text))
            EndpointBox.Text = definition.Endpoint;
        if (ModelBox.Text.Length == 0 || ProviderCatalog.All.Any(item => item.Model == ModelBox.Text))
            ModelBox.Text = definition.Model;
    }

    private async void AdvancePercentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var percent = (int)Math.Round(AdvancePercentSlider.Value);
        if (percent is < 1 or > 100) return;
        AdvancePercentLabel.Text = $"{percent}%";
        if (applyingSettings) return;
        appSettings = appSettings with { AdvanceAtPercent = percent };
        SavePlaybackSettings();
        if (study is not null) await study.ConfigureAsync(appSettings);
    }

    private async void PlaybackRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackRateBox.SelectedItem is not double rate || rate is < 0.25 or > 16 || applyingSettings) return;
        appSettings = appSettings with { PlaybackRate = rate };
        SavePlaybackSettings();
        if (study is not null) await study.ConfigureAsync(appSettings);
    }

    private void SavePlaybackSettings()
    {
        // 构造窗口时控件会触发 Changed，此时保留原设置，只保存就绪后的用户调整。
        if (study is null || applyingSettings) return;
        try { settings.Save(appSettings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { log.Write("WARN", "播放设置未能保存，请检查本机数据目录权限。"); }
    }

    private void AutoAdvanceChanged(object sender, RoutedEventArgs e)
    {
        if (applyingSettings) return;
        appSettings = appSettings with { AutoAdvance = AutoAdvanceBox.IsChecked == true };
        SavePlaybackSettings();
    }

    private void SidebarChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, SidebarTabs) && SettingsTab.IsSelected) LoadModelSettings();
    }

    private void LoadModelSettings()
    {
        try
        {
            var config = AiConfiguration.LoadFile(envPath);
            ProviderBox.ItemsSource = config.Provider.Length > 0 && !ProviderCatalog.Contains(config.Provider)
                ? ProviderCatalog.All.Append(new ProviderDefinition(config.Provider, config.Provider)).ToArray()
                : ProviderCatalog.All;
            ProviderBox.SelectedValue = config.Provider.Length > 0 ? config.Provider : appSettings.SelectedProvider;
            EndpointBox.Text = config.Endpoint;
            ModelBox.Text = config.Model;
            // .env 只写了密钥、未写接口或模型时，按所选 Provider 带上原脚本默认值（不写回文件）。
            if (config.Endpoint.Length == 0 || config.Model.Length == 0)
                ApplyProviderDefaults(ProviderBox.SelectedValue as string ?? appSettings.SelectedProvider);
            ApiKeyBox.Clear();
            log.RegisterSecret(config.ApiKey);
            UpdateKeyStatus();
            RefreshEnvStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { EnvStatusText.Text = "无法读取 .env，请检查文件权限。"; }
    }

    private void UpdateKeyStatus()
    {
        if (KeyStatusText is null || envPath is null) return;
        try
        {
            var config = AiConfiguration.LoadFile(envPath);
            var status = config.Provider == SelectedProvider && config.ApiKey.Length > 0
                ? ".env 已有密钥 · 不回显；留空可保留" : "请填写此提供商的新密钥";
            // 原脚本每个 Provider 都附带取 Key 引导；保持在状态行，不新增面板。
            var hint = ProviderCatalog.Find(SelectedProvider)?.KeyHint;
            KeyStatusText.Text = hint is null ? status : $"{status} · {hint}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { KeyStatusText.Text = "无法读取 .env"; }
    }

    private void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var previous = AiConfiguration.LoadFile(envPath);
            var key = ApiKeyBox.Password.Trim();
            if (key.Length == 0 && previous.Provider == SelectedProvider) key = previous.ApiKey;
            var config = new AiConfiguration { Provider = SelectedProvider, ApiKey = key,
                Endpoint = EndpointBox.Text.Trim(), Model = ModelBox.Text.Trim() };
            log.RegisterSecret(key);
            // 非默认配置保存到自己的目录，形成独立配置文件；默认配置仍写入原有的 .env。
            config.Save(envSavePath);
            ApiKeyBox.Clear();
            appSettings = appSettings with { SelectedProvider = ProviderCatalog.Contains(SelectedProvider) ? SelectedProvider : appSettings.SelectedProvider };
            settings.Save(appSettings);
            UpdateKeyStatus();
            RefreshEnvStatus();
            log.Write("INFO", "模型配置已写入 .env，后续请求使用新配置。");
            StatusText.Text = "设置已保存到 .env";
        }
        catch (InvalidOperationException error) { StatusText.Text = error.Message; }
        catch (Exception error)
        {
            log.Write("ERROR", $"保存失败：{error.GetType().Name}；请检查 .env 目录权限。");
            StatusText.Text = "保存失败，请查看日志";
        }
    }

    private void DeleteKeyClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "删除当前提供商在 .env 及旧密钥库中保存的 API Key？", "删除密钥", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var config = AiConfiguration.LoadFile(envPath);
            if (config.Provider == SelectedProvider)
                new AiConfiguration { Provider = config.Provider, Model = config.Model, Endpoint = config.Endpoint }.Save(envSavePath, allowEmptyKey: true);
            new SecretStore(dataRoot).Delete(SelectedProvider);
            ApiKeyBox.Clear();
            UpdateKeyStatus();
            RefreshEnvStatus();
            log.Write("INFO", "当前提供商密钥已删除。");
        }
        catch (Exception error) { log.Write("ERROR", $"删除失败：{error.GetType().Name}"); }
    }
    private async void ClearLoginClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将清除此程序的 Cookie、缓存和网站存储，并退出网页登录。模型密钥不会删除。是否继续？",
            "清除浏览器资料", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ClearLoginButton.IsEnabled = false;
        try
        {
            await browser.ClearLoginAsync();
        }
        catch (Exception error) { log.Write("ERROR", $"清除浏览器资料失败：{error.GetType().Name}"); }
        finally { if (!lifetime.IsCancellationRequested) ClearLoginButton.IsEnabled = browser.IsReady; }
    }

    private void ClearLogsClick(object sender, RoutedEventArgs e) => log.Clear();

    private bool closingInProgress, closingPrepared;
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closingPrepared || browser is null || !browser.IsReady) return;
        e.Cancel = true;
        if (closingInProgress) return;
        closingInProgress = true;
        progressTimer.Stop();
        try
        {
            await StopStudyAsync();
            // 所有账户实例的 Cookie 快照统一保存，随后由 OnClosed 释放全部 WebView。
            await pool.SaveAllAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception error) { log.Write("WARN", $"退出前保存会话未完成：{error.GetType().Name}"); }
        finally { closingPrepared = true; Close(); }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        lifetime.Cancel();
        DisposeStudy();
        // 实例池统一释放所有账户的脚本代理与 WebView 控件，避免后台实例随窗口泄漏。
        pool.Dispose();
        lifetime.Dispose();
    }
}
