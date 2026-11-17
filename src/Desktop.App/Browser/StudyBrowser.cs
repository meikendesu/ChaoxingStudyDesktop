using System.IO;
using System.Net.Http;
using System.Text.Json;
using Desktop.Core;
using Microsoft.Web.WebView2.Core;

namespace Desktop.App.Browser;

public sealed record StudyFrame(CoreWebView2Frame? Frame, FrameStudySnapshot Snapshot);

// 原生端直接调用各 frame 的小型代理，不通过网页消息接收任意命令。
public sealed partial class StudyBrowser : IDisposable
{
    private readonly CoreWebView2 core;
    private readonly BrowserHost host;
    private readonly HashSet<CoreWebView2Frame> frames = [];
    private readonly Dictionary<CoreWebView2Frame, CoreWebView2Frame?> parents = [];
    private readonly string script;
    private string configurationScript = "";
    // 学习代理已由 AddScriptToExecuteOnDocumentCreatedAsync 注入所有文档（含 iframe）；
    // 每次调用只需重新应用配置与播放保护，前缀按保护状态缓存，避免重复拼接整份脚本。
    private string prefixNormal = "";
    private string prefixProtected = "";
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private long revision;
    private bool disposed;
    private bool playbackProtected;
    private readonly HttpClient fontHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public string FontDiagnostic { get; private set; } = "尚未解析";
    public event Action? ContextChanged;
    public long Revision => revision;
    public string[] Diagnostics { get; private set; } = [];
    public object[] QuestionDiagnostics { get; private set; } = [];
    public object[] MediaDiagnostics { get; private set; } = [];

    public StudyBrowser(CoreWebView2 core, BrowserHost host)
    {
        this.core = core;
        this.host = host;
        script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "study-agent.js"));
        RebuildPrefixes();
        core.FrameCreated += FrameCreated;
        core.NavigationStarting += Navigating;
    }

    private void RebuildPrefixes()
    {
        prefixNormal = configurationScript + "\nwindow.__studyDesktop?.setPlaybackProtection(false);\n";
        prefixProtected = configurationScript + "\nwindow.__studyDesktop?.setPlaybackProtection(true);\n";
    }

    public Task<string> InstallAsync() => core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    private void Navigating(object? sender, CoreWebView2NavigationStartingEventArgs args)
    { revision++; ContextChanged?.Invoke(); }

    private void FrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args)
    {
        if (!frames.Add(args.Frame)) return;
        parents[args.Frame] = sender as CoreWebView2Frame;
        args.Frame.FrameCreated += FrameCreated;
        args.Frame.Destroyed += FrameDestroyed;
        args.Frame.NavigationStarting += Navigating;
    }
    private void FrameDestroyed(object? sender, object args)
    {
        // Destroyed 回调中 COM frame 已失效，不能再访问其事件接口。
        if (sender is CoreWebView2Frame frame) { frames.Remove(frame); parents.Remove(frame); }
        revision++; ContextChanged?.Invoke();
    }
    private void Detach(CoreWebView2Frame frame)
    {
        parents.Remove(frame);
        // 嵌套 frame 的 Destroyed 通知可能晚于顶层导航完成；先检查存活状态。
        if (frame.IsDestroyed() != 0) { frames.Remove(frame); return; }
        frame.FrameCreated -= FrameCreated;
        frame.Destroyed -= FrameDestroyed;
        frame.NavigationStarting -= Navigating;
        frames.Remove(frame);
    }
    // 仅用于离线性能基线计数，不参与任何业务判定。
    public long ExecuteCalls { get; private set; }
    public long InjectedBytes { get; private set; }

    private Task<string> Execute(CoreWebView2Frame? frame, string expression)
    {
        // 标签切换会创建新文档/框架；每次操作前应用当前配置，避免回退到脚本默认值。
        // 整份代理脚本只在文档创建时注入一次（InstallAsync），此处不再重复下发。
        var code = (playbackProtected ? prefixProtected : prefixNormal) + expression;
        ExecuteCalls++;
        InjectedBytes += System.Text.Encoding.UTF8.GetByteCount(code);
        return frame is null ? core.ExecuteScriptAsync(code) : frame.ExecuteScriptAsync(code);
    }

    // 兜底：若某文档创建时注入未生效，重新下发一次完整代理，随后按原路径重试。
    private async Task<string> ReinstallAsync(CoreWebView2Frame? frame)
    {
        ExecuteCalls++;
        InjectedBytes += System.Text.Encoding.UTF8.GetByteCount(script);
        return frame is null ? await core.ExecuteScriptAsync(script) : await frame.ExecuteScriptAsync(script);
    }

    public async Task<List<StudyFrame>> ReadAsync(CancellationToken cancellation)
    {
        var version = revision;
        var result = new List<StudyFrame>();
        var diagnostics = new List<string>();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source)) return result;
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame is not null && frame.IsDestroyed() != 0) { frames.Remove(frame); continue; }
            try
            {
                const string snapshotExpression = "window.__studyDesktop?.snapshot() ?? null";
                var raw = await Execute(frame, snapshotExpression).WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                if (raw is "null" or "undefined")
                {
                    await ReinstallAsync(frame).WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                    raw = await Execute(frame, snapshotExpression).WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                }
                if (revision != version) throw new OperationCanceledException();
                if (raw.Length > 4 * 1024 * 1024)
                    raw = await Execute(frame, "(()=>{const s=window.__studyDesktop?.snapshot();if(s){s.questions=[];s.questionOverflow=true;}return s??null})()").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                var snapshot = JsonSerializer.Deserialize<FrameStudySnapshot>(raw, json);
                if (snapshot?.Questions.Length > 0)
                    diagnostics.Add(await Execute(frame, "window.__studyDesktop?.diagnostics() ?? null").WaitAsync(TimeSpan.FromSeconds(3), cancellation));
                if (snapshot is null || (snapshot.MediaCount == 0 && snapshot.Questions.Length == 0 && snapshot.Total == 0))
                {
                    // 仅结构信息：不读取表单值、文本、Cookie、存储或 URL 查询参数。
                    diagnostics.Add(await Execute(frame, "JSON.stringify({host:location.hostname,path:location.pathname,ready:document.readyState,videos:document.querySelectorAll('video').length,audios:document.querySelectorAll('audio').length,iframes:document.querySelectorAll('iframe').length,classes:[...new Set([...document.querySelectorAll('[class]')].flatMap(e=>[...e.classList]).filter(c=>/chapter|catalog|course|video|ques|task|study/i.test(c)))].slice(0,60)})").WaitAsync(TimeSpan.FromSeconds(3), cancellation));
                }
                if (snapshot is not null && BrowserPolicy.IsAllowed(snapshot.Url) && snapshot.DocumentId.Length > 0)
                    result.Add(new(frame, snapshot));
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException or TimeoutException)
            { /* frame 正在销毁或尚未创建文档，下次刷新重试。 */ }
        }
        Diagnostics = diagnostics.ToArray();
        MediaDiagnostics = result.Where(f => f.Snapshot.MediaCount > 0).Select(f => (object)new
        {
            f.Snapshot.VideoCount, f.Snapshot.VideoFinished, f.Snapshot.AudioCount, f.Snapshot.AudioFinished, f.Snapshot.MediaPaused,
            f.Snapshot.Position, f.Snapshot.Duration, f.Snapshot.MediaStatus,
            f.Snapshot.PlaybackRate, f.Snapshot.AdvanceAtPercent, f.Snapshot.MediaEvents
        }).ToArray();
        QuestionDiagnostics = result.SelectMany(f => f.Snapshot.Questions).Select(q => (object)new
            { q.Kind, q.Issue, OptionCount = q.Options.Length, q.BlankCount }).ToArray();
        return result;
    }

    // 课程流程：读取所有框架（含 iframe）的页面类型与任务点总览。
    // 学习页面与课程页面由网页代理分别标记，宿主层据此区分，不做 URL 猜测。
    public async Task<List<CourseOverview>> ReadCourseOverviewsAsync(CancellationToken cancellation)
    {
        var result = new List<CourseOverview>();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source)) return result;
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame is not null && frame.IsDestroyed() != 0) { frames.Remove(frame); continue; }
            try
            {
                var raw = await Execute(frame, "window.__studyDesktop?.courseOverview() ?? null").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                if (raw is "null" or "undefined")
                {
                    await ReinstallAsync(frame).WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                    raw = await Execute(frame, "window.__studyDesktop?.courseOverview() ?? null").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                }
                if (raw.Length > 2 * 1024 * 1024) continue;
                var overview = JsonSerializer.Deserialize<CourseOverview>(raw, json);
                if (overview is not null && overview.DocumentId.Length > 0 && BrowserPolicy.IsAllowed(overview.Url))
                    result.Add(overview);
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException or TimeoutException)
            { /* 框架正在销毁或尚未创建文档，下次轮询重试。 */ }
        }
        return result;
    }

    // 只在读取总览的那个框架上执行命令，避免把点击发到已经重绘的旧框架。
    public async Task<string> CourseCommandAsync(string documentId, string action, int index, CancellationToken cancellation)
    {
        // 只保留「点击当前已打开课程页面里的任务点」；不提供任何导航/进入课程页面的命令。
        if (action is not "openCourseTask") throw new ArgumentException("未知课程操作。", nameof(action));
        cancellation.ThrowIfCancellationRequested();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source)) throw new OperationCanceledException();
        var id = JsonSerializer.Serialize(documentId, json);
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
        {
            if (frame is not null && frame.IsDestroyed() != 0) continue;
            try
            {
                var hit = await Execute(frame, $"window.__studyDesktop?.documentId === {id}").WaitAsync(TimeSpan.FromSeconds(3), cancellation);
                if (hit != "true") continue;
                return await Execute(frame, $"window.__studyDesktop?.{action}({index}) ?? 'none'").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        }
        return "none";
    }

    public async Task<string[]> DumpPageAsync(CancellationToken cancellation)
    {
        var result = new List<string>();
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
        {
            cancellation.ThrowIfCancellationRequested();
            if (frame is not null && frame.IsDestroyed() != 0) { frames.Remove(frame); continue; }
            try
            {
                var raw = await Execute(frame, "window.__studyDesktop?.dumpTabs() ?? null").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                result.Add(raw);
            }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        }
        return result.ToArray();
    }

    public async Task<string> CommandAsync(StudyFrame frame, string action, CancellationToken cancellation)
    {
        if (action is not ("play" or "startVideos" or "advance" or "advanceTaskPoint" or "advanceDocument" or "prepareQuestionView" or "submit" or "stop")) throw new ArgumentException("未知操作。", nameof(action));
        cancellation.ThrowIfCancellationRequested();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source)) throw new OperationCanceledException();
        if (frame.Frame is not null && frame.Frame.IsDestroyed() != 0) return "null";
        var id = JsonSerializer.Serialize(frame.Snapshot.DocumentId);
        if (action is "play" or "startVideos" && !playbackProtected)
        {
            // 外层页面也可能监听离开事件并暂停子框架，启动时一起启用保护。
            playbackProtected = true;
            foreach (var target in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
            {
                cancellation.ThrowIfCancellationRequested();
                try { if (target is null || target.IsDestroyed() == 0) await Execute(target, "true").WaitAsync(TimeSpan.FromSeconds(2), cancellation); }
                catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
            }
        }
        return await Execute(frame.Frame, $"window.__studyDesktop?.snapshot().documentId === {id} ? window.__studyDesktop.{action}() : null").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
    }

    public async Task<bool> FillAsync(StudyFrame frame, StudyQuestion question, AiAnswer answer, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source)) return false;
        if (frame.Frame is not null && frame.Frame.IsDestroyed() != 0) return false;
        var args = string.Join(",", JsonSerializer.Serialize(frame.Snapshot.DocumentId), JsonSerializer.Serialize(question, json), JsonSerializer.Serialize(answer, json));
        return await Execute(frame.Frame, $"window.__studyDesktop?.fill({args}) === true").WaitAsync(TimeSpan.FromSeconds(5), cancellation) == "true";
    }

    public async Task<string> AdvanceQuestionPageAsync(StudyFrame frame, (StudyQuestion Question, AiAnswer Answer)[] entries, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source) || (frame.Frame is not null && frame.Frame.IsDestroyed() != 0)) return "blocked";
        var id = JsonSerializer.Serialize(frame.Snapshot.DocumentId);
        var data = JsonSerializer.Serialize(entries.Select(e => new { e.Question, e.Answer }), json);
        var raw = await Execute(frame.Frame, $"window.__studyDesktop?.advanceQuestionPage({id},{data}) ?? 'blocked'").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        return JsonSerializer.Deserialize<string>(raw) ?? "blocked";
    }

    public async Task<bool> SubmitAnswersAsync(StudyFrame frame, (StudyQuestion Question, AiAnswer Answer)[] entries, CancellationToken cancellation, bool localOnly = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (disposed || !BrowserPolicy.IsAllowed(core.Source) || (frame.Frame is not null && frame.Frame.IsDestroyed() != 0)) return false;
        var id = JsonSerializer.Serialize(frame.Snapshot.DocumentId);
        var data = JsonSerializer.Serialize(entries.Select(e => new { e.Question, e.Answer }), json);
        var version = revision;
        var scopes = new List<(CoreWebView2Frame? Frame, string DocumentId)>();
        host.AutomaticSubmissionUri = frame.Snapshot.Url;
        host.AutomaticSubmissionBlocked = false;
        using var registration = cancellation.Register(() => host.AutomaticSubmissionUri = null);
        var requested = false;
        try
        {
            // 确认框可能显示在题目框架的父页面；只授权当前文档及其祖先，不扫描无关兄弟框架。
            var current = frame.Frame;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var scopeId = await Execute(current, "window.__studyDesktop?.snapshot().documentId ?? null").WaitAsync(TimeSpan.FromSeconds(2), cancellation);
                if (scopeId != "null")
                {
                    scopes.Add((current, scopeId));
                    await Execute(current, $"window.__studyDesktop?.beginSubmissionConfirmation({scopeId})").WaitAsync(TimeSpan.FromSeconds(2), cancellation);
                }
                if (current is null) break;
                current = parents.GetValueOrDefault(current);
            }
            cancellation.ThrowIfCancellationRequested();
            if (version != revision) return false;
            var result = await Execute(frame.Frame, $"window.__studyDesktop?.submitAnswers({id},{data},{(localOnly ? "true" : "false")})").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            if (result != "\"clicked\"") return false;
            if (host.AutomaticSubmissionBlocked) return false;
            requested = true;
            // 最多等待约 5 秒，兼容页面异步创建的确认框。
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(100, cancellation);
                if (version != revision) break;
                if (host.AutomaticSubmissionBlocked) return false;
                foreach (var scope in scopes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var confirmed = await Execute(scope.Frame, $"window.__studyDesktop?.snapshot().documentId === {scope.DocumentId} && window.__studyDesktop.confirmSubmission()").WaitAsync(TimeSpan.FromSeconds(2), cancellation);
                    if (confirmed == "true") return true;
                    if (confirmed == "\"blocked\"" || host.AutomaticSubmissionBlocked) { host.AutomaticSubmissionBlocked = true; return false; }
                }
            }
            foreach (var scope in scopes)
                if (await Execute(scope.Frame, $"window.__studyDesktop?.snapshot().documentId === {scope.DocumentId} && window.__studyDesktop.submissionUnresolved()").WaitAsync(TimeSpan.FromSeconds(2), cancellation) == "true")
                { host.AutomaticSubmissionBlocked = true; return false; }
            return true;
        }
        catch (System.Runtime.InteropServices.COMException) { return requested; /* 提交后框架可能立即销毁。 */ }
        finally
        {
            host.AutomaticSubmissionUri = null;
            foreach (var scope in scopes)
                try
                {
                    if (scope.Frame is null || scope.Frame.IsDestroyed() == 0)
                        await Execute(scope.Frame, $"window.__studyDesktop?.snapshot().documentId === {scope.DocumentId} && (()=>{{{(host.AutomaticSubmissionBlocked ? "window.__studyDesktop.blockSubmission();" : "")}return window.__studyDesktop.endSubmissionConfirmation()}})()").WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        }
    }

    public async Task PrepareFontsAsync(CancellationToken cancellation)
    {
        var candidates = await ReadAsync(cancellation);
        if (!candidates.Any(f => f.Snapshot.Questions.Any(q => q.Issue == "font"))) return;
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "vendor");
        var parser = "if (!window.__studyFontParser) { (()=>{" + File.ReadAllText(Path.Combine(folder, "Typr.min.js")) + "\n" +
            File.ReadAllText(Path.Combine(folder, "Typr.U.min.js")) + "\nwindow.__studyFontParser=Typr;})(); }\n";
        using var tableJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Table.json")));
        var table = tableJson.RootElement.EnumerateObject()
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(p.Name, "^[0-9a-f]{8}$") && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out _))
            .ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var version = revision;
        foreach (var frame in candidates.Where(f => f.Snapshot.Questions.Any(q => q.Issue == "font")))
        {
            cancellation.ThrowIfCancellationRequested();
            await Execute(frame.Frame, parser + "window.__studyDesktop?.prepareFont()").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(250, cancellation);
                if (revision != version) throw new OperationCanceledException();
                var raw = await Execute(frame.Frame, "window.__studyDesktop?.fontResult() ?? null").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                if (raw.Length > 8 * 1024 * 1024) break;
                using var result = JsonDocument.Parse(raw);
                if (result.RootElement.ValueKind == JsonValueKind.Null) break;
                var status = result.RootElement.GetProperty("status").GetString();
                if (status == "loading") continue;
                if (status == "failed" && result.RootElement.TryGetProperty("url", out var source) &&
                    Uri.TryCreate(source.GetString(), UriKind.Absolute, out var fontUri) && FontFileClient.IsAllowed(fontUri))
                {
                    try
                    {
                        var bytes = await new FontFileClient(fontHttp).DownloadAsync(fontUri, cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if (revision != version) throw new OperationCanceledException();
                        await Execute(frame.Frame, $"window.__studyDesktop?.parseFontBytes({JsonSerializer.Serialize(frame.Snapshot.DocumentId)},{JsonSerializer.Serialize(Convert.ToBase64String(bytes))})").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                        continue;
                    }
                    catch (Exception error) when (error is HttpRequestException or InvalidOperationException or TimeoutException)
                    { FontDiagnostic = "原生字体下载失败，请检查字体资源是否可访问"; break; }
                }
                if (status == "failed")
                    FontDiagnostic = result.RootElement.TryGetProperty("error", out var reason) ? reason.GetString() ?? "字体解析失败" : "字体解析失败";
                if (status != "ready" || result.RootElement.GetProperty("documentId").GetString() != frame.Snapshot.DocumentId) break;
                var paths = result.RootElement.GetProperty("paths").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetString()!);
                var map = FontGlyphMap.Decode(paths, table);
                FontDiagnostic = $"已解析 {paths.Count} 个字形，映射 {map.Count} 个；剩余未映射字符仍需处理";
                cancellation.ThrowIfCancellationRequested();
                await Execute(frame.Frame, $"window.__studyDesktop?.applyFontMap({JsonSerializer.Serialize(frame.Snapshot.DocumentId)},{JsonSerializer.Serialize(map)})").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                break;
            }
        }
    }

    public async Task ConfigureAsync(AppSettings settings)
    {
        if (disposed) return;
        var config = JsonSerializer.Serialize(new { advanceAtPercent = settings.AdvanceAtPercent, playbackRate = settings.PlaybackRate }, json);
        configurationScript = $"window.__studyDesktop?.configure({config});";
        RebuildPrefixes();
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
            try { if (frame is null || frame.IsDestroyed() == 0) await Execute(frame, "true").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
    }

    public async Task StopAsync()
    {
        if (disposed) return;
        playbackProtected = false;
        foreach (var frame in new CoreWebView2Frame?[] { null }.Concat(frames.Cast<CoreWebView2Frame?>().ToArray()))
            try { if (frame is null || frame.IsDestroyed() == 0) await Execute(frame, "window.__studyDesktop?.stop()").WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException) { }
    }

    public void Dispose()
    {
        disposed = true;
        fontHttp.Dispose();
        core.FrameCreated -= FrameCreated;
        core.NavigationStarting -= Navigating;
        foreach (var frame in frames.ToArray()) Detach(frame);
    }
}
