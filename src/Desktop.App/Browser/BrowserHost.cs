using System.IO;
using System.Text.Json;
using Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App.Browser;

// environment / controllerOptions 由账户实例池注入：池内所有实例共享同一 environment，
// 但每个账户使用自己的命名 profile，Cookie、localStorage 与缓存互不串用。
// 两者为空时保持原有行为（自行创建 environment、使用默认 profile），兼容单实例启动。
public sealed class BrowserHost(WebView2 view, string dataRoot, EventLog log, string? profileRoot = null,
    CoreWebView2Environment? environment = null, CoreWebView2ControllerOptions? controllerOptions = null) : IDisposable
{
    private bool disposed;
    // activeRoot 只决定当前实例读写哪一组配置 / Cookie 快照，与浏览器会话隔离无关。
    private string activeRoot = profileRoot ?? dataRoot;
    public bool IsReady { get; private set; }
    public string ActiveRoot => activeRoot;
    public CoreWebView2Environment? SharedEnvironment { get; private set; }
    public CoreWebView2CookieManager? CookieManager => IsReady && !disposed ? view.CoreWebView2.CookieManager : null;
    public void SetActiveRoot(string root) => activeRoot = Path.GetFullPath(root);
    // 页面加载失败由实例池读取，用于标记该账户的 WebView 需要重试；不阻断切换。
    public bool LastLoadFailed { get; private set; }
    internal string? RuntimeDirectory { get; private set; }
    internal string? AutomaticSubmissionUri { get; set; }
    internal bool AutomaticSubmissionBlocked { get; set; }
    public event Action<string>? AddressChanged;
    public event Action<string>? StatusChanged;
    public event Action<string>? TitleChanged;
    public event Action? PageLoaded;
    public event Action<string>? LoadFailed;

    public async Task InitializeAsync(CancellationToken cancellation = default)
    {
        void Report(string stage)
        {
            StatusChanged?.Invoke(stage);
            log.Write("INFO", stage);
            // 每个账户写自己的启动日志，避免多个实例并发追加同一文件。
            try { File.AppendAllText(Path.Combine(activeRoot, "browser-startup.log"), $"{DateTimeOffset.Now:O} {stage}{Environment.NewLine}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        if (environment is null)
        {
            Report("正在准备内置 WebView2 运行时，首次启动需要复制并校验文件…");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChaoxingStudyDesktop", "WebView2Runtime");
            RuntimeDirectory = await Task.Run(() => BrowserRuntime.Prepare(AppContext.BaseDirectory, cache), cancellation);
            cancellation.ThrowIfCancellationRequested();
            Report("正在创建 WebView2 浏览器环境…");
            SharedEnvironment = await CoreWebView2Environment.CreateAsync(RuntimeDirectory, Path.Combine(dataRoot, "BrowserProfile"))
                .WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        }
        else SharedEnvironment = environment;
        var created = SharedEnvironment!;
        if (disposed) return;
        Report("正在创建课程浏览窗口…");
        // 命名 profile 由实例池注入；默认账户不传 options，继续使用原有默认 profile 的登录资料。
        var init = controllerOptions is null
            ? view.EnsureCoreWebView2Async(created)
            : view.EnsureCoreWebView2Async(created, controllerOptions);
        await init.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        if (disposed) return;
        var core = view.CoreWebView2;
        Report("正在恢复学习通登录会话…");
        await RestoreSessionCookiesAsync().WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.IsMuted = true;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.ScriptDialogOpening += OnScriptDialog;
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        core.SetVirtualHostNameToFolderMapping(BrowserPolicy.LocalHost, assets, CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.DocumentTitleChanged += OnTitleChanged;
        core.NewWindowRequested += OnNewWindow;
        core.ProcessFailed += OnProcessFailed;
        IsReady = true;
        Report("课程浏览器初始化完成");
        log.Write("INFO", "浏览器已就绪，登录资料使用独立的本地目录。");
        StatusChanged?.Invoke("浏览器已就绪");
    }

    public void Navigate(string url)
    {
        if (!IsReady || disposed) return;
        if (!BrowserPolicy.IsAllowed(url))
        {
            StatusChanged?.Invoke("请输入有效的 HTTP/HTTPS 网址");
            log.Write("WARN", "导航已取消：地址不是有效的网页地址。");
            return;
        }
        view.CoreWebView2.Navigate(url);
    }

    private void OnScriptDialog(object? sender, CoreWebView2ScriptDialogOpeningEventArgs args)
    {
        var sameOrigin = Uri.TryCreate(AutomaticSubmissionUri, UriKind.Absolute, out var expected) &&
            Uri.TryCreate(args.Uri, UriKind.Absolute, out var actual) && expected.GetLeftPart(UriPartial.Authority) == actual.GetLeftPart(UriPartial.Authority);
        if (sameOrigin && System.Text.RegularExpressions.Regex.IsMatch(args.Message, "未作答|未答|漏答|未完成|未填写"))
        { AutomaticSubmissionBlocked = true; log.Write("WARN", "网页提示仍有未完成题目，已停止提交确认。"); return; }
        if (sameOrigin && (args.Kind == CoreWebView2ScriptDialogKind.Alert ||
            args.Kind == CoreWebView2ScriptDialogKind.Confirm && System.Text.RegularExpressions.Regex.IsMatch(args.Message, "提交|交卷")))
        { args.Accept(); return; }
        // 其余弹窗仍交给用户；异步调度避免在 WebView2 回调内启动嵌套消息循环。
        var deferral = args.GetDeferral();
        view.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (args.Kind == CoreWebView2ScriptDialogKind.Alert)
                { System.Windows.MessageBox.Show(args.Message, "网页提示"); args.Accept(); }
                else if (args.Kind is CoreWebView2ScriptDialogKind.Confirm or CoreWebView2ScriptDialogKind.Beforeunload)
                {
                    if (System.Windows.MessageBox.Show(args.Message, "网页确认", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes) args.Accept();
                }
                else if (args.Kind == CoreWebView2ScriptDialogKind.Prompt)
                {
                    var input = new System.Windows.Controls.TextBox { Text = args.DefaultText };
                    var ok = new System.Windows.Controls.Button { Content = "确定", IsDefault = true, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
                    var cancel = new System.Windows.Controls.Button { Content = "取消", IsCancel = true };
                    var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
                    panel.Children.Add(new System.Windows.Controls.TextBlock { Text = args.Message, TextWrapping = System.Windows.TextWrapping.Wrap });
                    panel.Children.Add(input); panel.Children.Add(ok); panel.Children.Add(cancel);
                    var dialog = new System.Windows.Window { Title = "网页输入", Content = panel, Width = 420,
                        SizeToContent = System.Windows.SizeToContent.Height, Owner = System.Windows.Window.GetWindow(view),
                        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner };
                    ok.Click += (_, _) => dialog.DialogResult = true;
                    if (dialog.ShowDialog() == true) { args.ResultText = input.Text; args.Accept(); }
                }
            }
            finally { deferral.Complete(); }
        }));
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        TitleChanged?.Invoke("正在加载…");
        StatusChanged?.Invoke("正在加载页面…");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (disposed) return;
        if (!e.IsSuccess)
        {
            // 页面自身重定向/中断会产生 OperationCanceled、ConnectionAborted，属正常现象，仅记录 INFO。
            if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.ConnectionAborted)
            {
                log.Write("INFO", $"页面导航已中断：{e.WebErrorStatus}");
                return;
            }
            LastLoadFailed = true;
            StatusChanged?.Invoke("页面加载失败，可刷新重试");
            log.Write("WARN", $"页面加载失败：{e.WebErrorStatus}");
            LoadFailed?.Invoke(e.WebErrorStatus.ToString());
            return;
        }
        LastLoadFailed = false;
        StatusChanged?.Invoke("页面已加载");
        OnTitleChanged(sender, EventArgs.Empty);
        PageLoaded?.Invoke();
    }

    private void OnTitleChanged(object? sender, object e)
        => TitleChanged?.Invoke(view.CoreWebView2.DocumentTitle);

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        => AddressChanged?.Invoke(view.CoreWebView2.Source);

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated) Navigate(e.Uri);
        else log.Write("WARN", "已忽略非用户发起的新窗口请求。");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        StatusChanged?.Invoke("浏览器进程异常，请重启程序");
        log.Write("ERROR", $"浏览器进程异常：{e.ProcessFailedKind}");
    }

    public void StopLoading()
    {
        if (IsReady && !disposed) view.CoreWebView2.Stop();
        StatusChanged?.Invoke("已停止加载");
    }

    public async Task ClearLoginAsync(string? destination = null)
    {
        if (!IsReady || disposed) return;
        view.CoreWebView2.Stop();
        new SessionCookieStore(activeRoot).Clear();
        new ProfileCookieStore(activeRoot).Clear();
        // 清除登录数据与删除模型密钥是两个独立操作。
        await view.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        Navigate(destination ?? BrowserPolicy.HomeUrl);
        log.Write("INFO", "此程序的浏览器资料已清除；模型设置和密钥保持不变。");
    }

    public async Task SaveSessionCookiesAsync()
    {
        if (!IsReady || disposed) return;
        var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(null);
        new SessionCookieStore(activeRoot).Save(cookies.Where(c => c.IsSession && SessionCookieStore.IsPlatformDomain(c.Domain))
            .Select(c => new SavedSessionCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly, (int)c.SameSite)));
        // 配置切换需要持久 Cookie 才能保持登录，单独保存完整快照（不改动上面的会话恢复语义）。
        SaveProfileCookies(cookies);
    }

    private void SaveProfileCookies(IReadOnlyList<CoreWebView2Cookie> cookies)
        => new ProfileCookieStore(activeRoot).Save(cookies
            .Where(c => SessionCookieStore.IsPlatformDomain(c.Domain))
            .Select(c => new SavedProfileCookie(c.Name, c.Value, c.Domain, c.Path,
                c.IsSecure, c.IsHttpOnly, (int)c.SameSite, c.Expires)));

    // 每个账户实例在自身的命名 profile 中恢复自己的会话快照；实例池切换时不再搬运 Cookie。
    private async Task RestoreSessionCookiesAsync()
    {
        try
        {
            var manager = view.CoreWebView2.CookieManager;
            var existing = await manager.GetCookiesAsync(null);
            foreach (var saved in new SessionCookieStore(activeRoot).Read())
            {
                if (existing.Any(c => c.Name == saved.Name && c.Domain == saved.Domain && c.Path == saved.Path)) continue;
                if (!Enum.IsDefined(typeof(CoreWebView2CookieSameSiteKind), saved.SameSite)) continue;
                var cookie = manager.CreateCookie(saved.Name, saved.Value, saved.Domain, saved.Path);
                cookie.IsSecure = saved.Secure; cookie.IsHttpOnly = saved.HttpOnly;
                cookie.SameSite = (CoreWebView2CookieSameSiteKind)saved.SameSite;
                manager.AddOrUpdateCookie(cookie);
            }
            // 完整快照优先补齐持久 Cookie；Expires 为 0 表示会话 Cookie，由浏览器维持。
            if (!new ProfileCookieStore(activeRoot).Exists) return;
            existing = await manager.GetCookiesAsync(null);
            foreach (var saved in new ProfileCookieStore(activeRoot).Read())
            {
                if (existing.Any(c => c.Name == saved.Name && c.Domain == saved.Domain && c.Path == saved.Path)) continue;
                if (!Enum.IsDefined(typeof(CoreWebView2CookieSameSiteKind), saved.SameSite)) continue;
                var cookie = manager.CreateCookie(saved.Name, saved.Value, saved.Domain, saved.Path);
                cookie.IsSecure = saved.Secure; cookie.IsHttpOnly = saved.HttpOnly;
                cookie.SameSite = (CoreWebView2CookieSameSiteKind)saved.SameSite;
                if (saved.Expires > DateTime.MinValue) cookie.Expires = saved.Expires;
                manager.AddOrUpdateCookie(cookie);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or JsonException)
        { log.Write("WARN", "本机会话恢复未完成，请正常登录；原加密文件保留。"); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (IsReady)
        {
            var core = view.CoreWebView2;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.SourceChanged -= OnSourceChanged;
            core.DocumentTitleChanged -= OnTitleChanged;
            core.NewWindowRequested -= OnNewWindow;
            core.ProcessFailed -= OnProcessFailed;
            core.ScriptDialogOpening -= OnScriptDialog;
        }
        IsReady = false;
        view.Dispose();
    }
}
