using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App.Browser;

// 输入边界：只有账户标识与配置目录，不含 UI 控件、页面 URL、Cookie 明文、密钥或 DOM 状态。
public sealed record AccountSwitchRequest(string FromId, string ToId, string ToRoot);

// 输出边界：是否应用、是否复用了既有实例、实例总数与可安全写入日志的告警；
// 不回传 Cookie 内容、题干、答案或 WebView 控件。
public sealed record AccountSwitchResult(bool Applied, bool Reused, string ViewId, int InstanceCount, string? Warning);

public enum AccountViewLoadState { Created, Loading, Ready, Failed }

// 一个账户配置对应一个且仅一个 WebView2 实例；页面、脚本注入、Cookie 与运行时状态都随实例保留。
public sealed class AccountWebView(string id, string root, WebView2 view, BrowserHost host, bool isPrimary, bool dedicatedEnvironment)
{
    public string Id { get; } = id;
    public string Root { get; } = root;
    public WebView2 View { get; } = view;
    public BrowserHost Host { get; } = host;
    public bool IsPrimary { get; } = isPrimary;
    // 命名 profile 不可用时降级为独立 userDataFolder，仍保证会话隔离。
    public bool DedicatedEnvironment { get; } = dedicatedEnvironment;
    public StudyBrowser? Study { get; set; }
    public AccountViewLoadState State { get; set; } = AccountViewLoadState.Created;
    public DateTime LastActivated { get; set; } = DateTime.UtcNow;
    public bool Busy { get; set; } = true;
    public string? Failure { get; set; }
}

/// <summary>
/// 多账户 WebView 实例池。
/// 生命周期：随主窗口创建 → 启动阶段创建一次共享 CoreWebView2Environment 与主实例 →
/// 每个账户按需在后台隐藏创建、加载并常驻 → 切换只改 Visibility，绝不重建、导航或重新初始化 →
/// 账户删除时释放对应实例 → 窗口关闭时统一保存 Cookie 快照并释放全部实例。
/// 会话隔离：同一 environment 下每个账户使用独立命名 profile（默认账户沿用原有默认 profile），
/// Cookie / localStorage / 缓存互不串用，同时共享浏览器进程资源，实例数受 AccountViewPolicy.MaxInstances 约束。
/// </summary>
public sealed class AccountWebViewPool : IDisposable
{
    private readonly Grid stage;
    private readonly WebView2 primaryView;
    private readonly string dataRoot;
    private readonly EventLog log;
    private readonly Func<AppSettings> currentSettings;
    private readonly Action onContextChanged;
    private readonly Action<BrowserHost> attachUiEvents;
    private readonly string globalEnvPath;
    private readonly Func<string> homeUrl;
    private readonly Dictionary<string, AccountWebView> views = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim creationGate = new(1, 1);
    private CoreWebView2Environment? environment;
    private string? runtimeDirectory;
    private bool disposed;

    public AccountWebViewPool(Grid stage, WebView2 primaryView, string dataRoot, EventLog log,
        Func<AppSettings> currentSettings, Action onContextChanged, Action<BrowserHost> attachUiEvents,
        string globalEnvPath, Func<string> homeUrl)
    {
        this.stage = stage;
        this.primaryView = primaryView;
        this.dataRoot = dataRoot;
        this.log = log;
        this.currentSettings = currentSettings;
        this.onContextChanged = onContextChanged;
        this.attachUiEvents = attachUiEvents;
        this.globalEnvPath = globalEnvPath;
        this.homeUrl = homeUrl;
    }

    public string ActiveId { get; private set; } = "";
    public int Count => views.Count;
    public IReadOnlyCollection<string> InstanceIds => views.Keys;
    public AccountWebView? Find(string id) => views.GetValueOrDefault(id);
    public BrowserHost? CurrentHost => ActiveId.Length > 0 && views.TryGetValue(ActiveId, out var item) ? item.Host : null;
    public WebView2 ActiveView => Active.View;
    public BrowserHost ActiveHost => Active.Host;
    public StudyBrowser? ActiveStudy => Active.Study;
    public bool IsReady => !disposed && environment is not null && ActiveId.Length > 0 &&
        views.TryGetValue(ActiveId, out var item) && item.Host.IsReady;
    private AccountWebView Active => views.TryGetValue(ActiveId, out var item) ? item
        : throw new InvalidOperationException("当前账户的 WebView 实例不存在。");

    // 启动阶段调用一次：准备运行时、创建共享 environment、建立当前账户的主实例。
    // 主实例沿用 XAML 中已有的 CourseBrowser，不新建控件，因此原有启动流程与离线回归不变。
    public async Task InitializeAsync(string activeId, string activeRoot, CancellationToken cancellation = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AccountWebViewPool));
        Directory.CreateDirectory(dataRoot);
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChaoxingStudyDesktop", "WebView2Runtime");
        runtimeDirectory = await Task.Run(() => BrowserRuntime.Prepare(AppContext.BaseDirectory, cache), cancellation);
        environment = await CoreWebView2Environment.CreateAsync(runtimeDirectory, Path.Combine(dataRoot, "BrowserProfile"))
            .WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        await CreateAsync(activeId, activeRoot, isPrimary: true, cancellation);
        Activate(activeId);
        log.Write("INFO", $"账户「{activeId}」的 WebView 实例已就绪（共 {views.Count} 个实例）。");
    }

    // 切换：后台读取目标配置 → 确保目标实例存在（已有则复用）→ 隐藏当前、显示目标。
    // 显隐切换放在最后一步，任何前置异常都不会改变当前可见页面。
    public async Task<AccountSwitchResult> SwitchAsync(AccountSwitchRequest request, CancellationToken cancellation = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AccountWebViewPool));
        if (request.FromId == request.ToId) return new(true, true, request.ToId, views.Count, null);
        cancellation.ThrowIfCancellationRequested();
        var snapshot = await Task.Run(() => ProfileSnapshot.Read(request.ToRoot, globalEnvPath), cancellation).ConfigureAwait(true);
        var reused = views.ContainsKey(request.ToId);
        var target = await EnsureAsync(request.ToId, request.ToRoot, cancellation);
        string? warning = snapshot.SettingsWarning;
        if (target.State == AccountViewLoadState.Failed)
            warning = Append(warning, "目标账户页面上次加载失败，切换后可在当前页面点「刷新」重试。");
        if (target.DedicatedEnvironment)
            warning = Append(warning, "目标账户已使用独立浏览器资料目录（命名 profile 不可用）。");
        Activate(request.ToId);
        return new(true, reused, request.ToId, views.Count, warning);
    }

    private static string Append(string? warning, string extra)
        => warning is null ? extra : warning + "；" + extra;

    // 账户新增后的后台预热：隐藏启动并加载主页，不激活、不打断当前页面。
    public Task PreloadAsync(string id, string root, CancellationToken cancellation = default)
        => EnsureAsync(id, root, cancellation);

    // 一一映射：同一个 id 永远返回同一个实例，不重复创建、不丢失已加载页面状态。
    public async Task<AccountWebView> EnsureAsync(string id, string root, CancellationToken cancellation = default)
    {
        if (disposed) throw new ObjectDisposedException(nameof(AccountWebViewPool));
        if (views.TryGetValue(id, out var existing)) return existing;
        await creationGate.WaitAsync(cancellation);
        try
        {
            if (views.TryGetValue(id, out existing)) return existing;
            if (AccountViewPolicy.NeedsTrim(views.Count))
            {
                var candidate = AccountViewPolicy.EvictionCandidate(
                    views.Values.Select(item => new AccountViewUsage(item.Id, item.LastActivated, item.Busy)), ActiveId);
                if (candidate is null)
                    throw new InvalidOperationException($"WebView 实例已达上限（{AccountViewPolicy.MaxInstances}），且没有可释放的后台实例。");
                await ReleaseAsync(candidate, cancellation);
                log.Write("INFO", $"实例数达到上限，已释放最久未使用的后台 WebView「{candidate}」。");
            }
            AccountWebView created;
            try { created = await BuildAsync(id, root, isPrimary: false, useNamedProfile: true, cancellation); }
            catch (Exception error) when (IsRecoverable(error) && !cancellation.IsCancellationRequested)
            {
                // 运行时不支持命名 profile 时，用独立 userDataFolder 重建；隔离性不变。
                log.Write("WARN", $"账户「{id}」的命名 profile 不可用（{error.GetType().Name}），改用独立浏览器资料目录。");
                created = await BuildAsync(id, root, isPrimary: false, useNamedProfile: false, cancellation);
            }
            views[id] = created;
            return created;
        }
        finally { creationGate.Release(); }
    }

    private static bool IsRecoverable(Exception error) => error is InvalidOperationException or COMException
        or TimeoutException or IOException or UnauthorizedAccessException;

    private async Task<AccountWebView> CreateAsync(string id, string root, bool isPrimary, CancellationToken cancellation)
    {
        await creationGate.WaitAsync(cancellation);
        try
        {
            var created = await BuildAsync(id, root, isPrimary, useNamedProfile: true, cancellation);
            views[id] = created;
            return created;
        }
        finally { creationGate.Release(); }
    }

    private async Task<AccountWebView> BuildAsync(string id, string root, bool isPrimary, bool useNamedProfile, CancellationToken cancellation)
    {
        Directory.CreateDirectory(root);
        var view = isPrimary ? primaryView : NewHiddenView();
        if (!isPrimary) stage.Children.Add(view);
        CoreWebView2Environment env = environment!;
        CoreWebView2ControllerOptions? options = null;
        if (useNamedProfile)
        {
            var profileName = AccountViewPolicy.ProfileNameOf(id);
            if (profileName is not null)
            {
                options = env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = profileName;
            }
        }
        else
        {
            // 降级：该账户使用独立 userDataFolder，隔离性不变，但不再共享浏览器进程资源。
            env = await CoreWebView2Environment.CreateAsync(runtimeDirectory, Path.Combine(root, "BrowserProfile"))
                .WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        }

        var host = new BrowserHost(view, dataRoot, log, root, env, options);
        var account = new AccountWebView(id, root, view, host, isPrimary, dedicatedEnvironment: !useNamedProfile);
        attachUiEvents(host);
        host.LoadFailed += reason =>
        {
            account.State = AccountViewLoadState.Failed;
            account.Failure = reason;
            log.Write("WARN", $"账户「{id}」的页面加载失败（{reason}），实例已保留，可刷新重试。");
        };
        host.PageLoaded += () =>
        {
            // 失败后用户刷新成功时也要清除失败标记，避免实例一直停留在 Failed。
            account.State = AccountViewLoadState.Ready;
            account.Failure = null;
        };
        try
        {
            await host.InitializeAsync(cancellation);
            var study = new StudyBrowser(view.CoreWebView2, host);
            await study.InstallAsync();
            await study.ConfigureAsync(currentSettings());
            study.ContextChanged += () => onContextChanged();
            account.Study = study;
            account.State = AccountViewLoadState.Loading;
            account.Busy = false;
            // 只有后台实例在此加载主页；主实例的首次导航沿用 MainWindow 原有流程。
            if (!isPrimary) host.Navigate(homeUrl());
            return account;
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            DisposeAccount(account);
            RemoveView(view, isPrimary);
            throw;
        }
    }

    private WebView2 NewHiddenView() => new()
    {
        // Hidden 保留布局尺寸（WebView2 需要有效 HWND 才能初始化与后台加载），
        // 又不渲染、不接收输入；airspace 问题由显隐切换规避。
        Visibility = Visibility.Hidden,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Margin = new Thickness(0)
    };

    // 隐藏当前实例、显示目标实例。已有实例的 Source、DOM、脚本与播放状态完全保留。
    public void Activate(string id)
    {
        if (!views.TryGetValue(id, out var target)) throw new InvalidOperationException("目标账户的 WebView 实例不存在。");
        foreach (var item in views.Values)
        {
            var visible = ReferenceEquals(item, target);
            item.View.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            Panel.SetZIndex(item.View, visible ? 10 : 0);
        }
        target.LastActivated = DateTime.UtcNow;
        ActiveId = id;
    }

    public bool IsActive(string id) => ActiveId == id && views.TryGetValue(id, out var item) &&
        item.View.Visibility == Visibility.Visible;

    // 账户移除：先由调用方切离，再保存 Cookie、释放脚本与控件；不改变其他实例。
    public async Task ReleaseAsync(string id, CancellationToken cancellation = default)
    {
        if (disposed || !views.TryGetValue(id, out var account)) return;
        if (id == ActiveId) throw new InvalidOperationException("不能释放当前正在显示的 WebView 实例。");
        account.Busy = true;
        try { await account.Host.SaveSessionCookiesAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellation); }
        catch (Exception error) { log.Write("WARN", $"释放实例前保存会话未完成：{error.GetType().Name}"); }
        views.Remove(id);
        DisposeAccount(account);
        RemoveView(account.View, account.IsPrimary);
        log.Write("INFO", $"已释放账户「{id}」的 WebView 实例（剩余 {views.Count} 个）。");
    }

    // 配置变更同步：移除清单中已不存在的实例；新增账户在 CreateProfileAsync 中单独预热。
    public async Task SyncAsync(IEnumerable<string> profileIds, CancellationToken cancellation = default)
    {
        var ids = profileIds.ToArray();
        foreach (var stale in AccountViewPolicy.Stale(views.Keys, ids))
            if (stale != ActiveId) await ReleaseAsync(stale, cancellation);
    }

    // 加载失败后在用户显式刷新时重试；切换流程绝不自动导航或重载。
    public void Retry(string id)
    {
        if (!views.TryGetValue(id, out var account)) return;
        account.State = AccountViewLoadState.Loading;
        account.Failure = null;
        account.Host.Navigate(homeUrl());
    }

    public async Task SaveAllAsync(CancellationToken cancellation = default)
    {
        foreach (var account in views.Values.ToArray())
        {
            try { await account.Host.SaveSessionCookiesAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellation); }
            catch (Exception error) { log.Write("WARN", $"账户「{account.Id}」会话保存未完成：{error.GetType().Name}"); }
        }
    }

    private static void DisposeAccount(AccountWebView account)
    {
        try { account.Study?.Dispose(); } catch (Exception) { }
        account.Study = null;
        try { account.Host.Dispose(); } catch (Exception) { }
    }

    private void RemoveView(WebView2 view, bool isPrimary)
    {
        if (isPrimary) return;
        try { stage.Children.Remove(view); } catch (Exception) { }
        try { view.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var account in views.Values.ToArray())
        {
            DisposeAccount(account);
            RemoveView(account.View, account.IsPrimary);
        }
        views.Clear();
        creationGate.Dispose();
    }
}
