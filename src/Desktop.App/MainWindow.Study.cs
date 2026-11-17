using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Desktop.App.Browser;
using Desktop.Core;

namespace Desktop.App;

public partial class MainWindow
{
    private StudyBrowser? study;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CompletionTracker completionTracker = new();
    private readonly HttpClient aiHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private CancellationTokenSource workflow = new();
    private bool ticking, playing, asking;
    private string? pendingChapter;
    private Dictionary<string, AiAnswer> answeredQuestions = [];
    private HashSet<string> verifiedQuestions = [];

    // 学习运行时状态按账户隔离：切换时保存当前账户，恢复目标账户，避免答案缓存与进度串号。
    // CompletionTracker 以对象引用保存，每个账户始终持有自己的实例。
    private readonly Dictionary<string, StudyRuntimeState> studyStates = new(StringComparer.Ordinal);
    private sealed class StudyRuntimeState
    {
        public Dictionary<string, AiAnswer> Answered = [];
        public HashSet<string> Verified = [];
        public CompletionTracker Completion = new();
        public string? PendingChapter, LastSavedCourse;
        public DateTime ChapterRequestedAt, NextAutoAnswerAt;
        public bool AutomaticAnswersEnabled = true, VideoPositionAdvanced, Playing;
        public double? LastVideoPosition;
        public int ChapterTransitions, CompletionRefreshes, AiAnswersGenerated, AnswersFilled, SubmissionsRequested;
    }

    private void CaptureStudyState(string id)
    {
        studyStates[id] = new StudyRuntimeState
        {
            Answered = new Dictionary<string, AiAnswer>(answeredQuestions),
            Verified = new HashSet<string>(verifiedQuestions),
            Completion = completionTracker,
            PendingChapter = pendingChapter,
            LastSavedCourse = lastSavedCourse,
            ChapterRequestedAt = chapterRequestedAt,
            NextAutoAnswerAt = nextAutoAnswerAt,
            AutomaticAnswersEnabled = automaticAnswersEnabled,
            VideoPositionAdvanced = videoPositionAdvanced,
            LastVideoPosition = lastVideoPosition,
            ChapterTransitions = chapterTransitions,
            CompletionRefreshes = completionRefreshes,
            AiAnswersGenerated = aiAnswersGenerated,
            AnswersFilled = answersFilled,
            SubmissionsRequested = submissionsRequested,
            Playing = playing
        };
    }

    private void RestoreStudyState(string id)
    {
        var state = studyStates.TryGetValue(id, out var saved) ? saved : new StudyRuntimeState();
        answeredQuestions = new Dictionary<string, AiAnswer>(state.Answered);
        verifiedQuestions = new HashSet<string>(state.Verified);
        completionTracker = state.Completion;
        pendingChapter = state.PendingChapter;
        lastSavedCourse = state.LastSavedCourse;
        chapterRequestedAt = state.ChapterRequestedAt;
        nextAutoAnswerAt = state.NextAutoAnswerAt;
        automaticAnswersEnabled = state.AutomaticAnswersEnabled;
        videoPositionAdvanced = state.VideoPositionAdvanced;
        lastVideoPosition = state.LastVideoPosition;
        chapterTransitions = state.ChapterTransitions;
        completionRefreshes = state.CompletionRefreshes;
        aiAnswersGenerated = state.AiAnswersGenerated;
        answersFilled = state.AnswersFilled;
        submissionsRequested = state.SubmissionsRequested;
        playing = state.Playing;
        // 让切换前仍在途的异步答题结果失效，避免写回新账户。
        InvalidateAnswers();
    }
    private OcrClient? ocrClient;
    private string ocrExecutable = "";
    private bool automaticAnswersEnabled = true;
    private long answerGeneration;
    private DateTime nextAutoAnswerAt;
    private string? lastSavedCourse;
    private DateTime chapterRequestedAt;
    internal string? AcceptancePath { get; set; }
    internal string? DumpUrl { get; set; }
    internal string? DumpTarget { get; set; }
    private int chapterTransitions, completionRefreshes, aiAnswersGenerated, answersFilled;
    private int submissionsRequested;
    private bool videoPositionAdvanced;
    private double? lastVideoPosition;

    // 仅显式验收启动时写入计数，不含题干、答案、URL、账号或密钥。
    private void WriteAcceptance()
    {
        if (AcceptancePath is null) return;
        try
        {
            File.WriteAllText(AcceptancePath, JsonSerializer.Serialize(new
            {
                UpdatedAt = DateTimeOffset.Now, CourseType = CourseTypeText.Text,
                CourseProgress = CourseProgressText.Text, VideoProgress = VideoProgressText.Text,
                StudyStatus = StudyStatusText.Text, VideoPositionAdvanced = videoPositionAdvanced,
                ChapterTransitions = chapterTransitions, CompletionRefreshes = completionRefreshes,
                AiAnswersGenerated = aiAnswersGenerated, AnswersFilled = answersFilled,
                SubmissionsRequested = submissionsRequested,
                Structure = study?.Diagnostics, Questions = study?.QuestionDiagnostics,
                Media = study?.MediaDiagnostics
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }

    // 每个账户实例的 StudyBrowser 由实例池在创建 WebView 时注入并完成配置；这里只绑定窗口级定时器与状态。
    private Task InitializeStudyAsync()
    {
        study = pool.ActiveStudy ?? throw new InvalidOperationException("当前账户的学习代理尚未就绪。");
        StartStudyButton.IsEnabled = true;
        StartCourseFlowButton.IsEnabled = true;
        RefreshEnvStatus();
        progressTimer.Tick += ProgressTick;
        if (smokePhase is null && DumpTarget is null) progressTimer.Start();
        return Task.CompletedTask;
    }

    private void RefreshEnvStatus()
    {
        try
        {
            var config = AiConfiguration.LoadFile(envPath);
            config.Validate();
            log.RegisterSecret(config.ApiKey);
            EnvStatusText.Text = $"当前 .env：{config.Provider} / {config.Model}（已配置，未代表连通）";
        }
        catch (InvalidOperationException error) { EnvStatusText.Text = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { EnvStatusText.Text = "无法读取 AI 配置文件，请检查 .env 的路径和访问权限。"; }
    }

    private void InvalidateAnswers() => answerGeneration++;

    // 供离线回归读写播放开关，不绕过任何业务流程。
    internal bool PlayingForTest => playing;
    internal void SetPlayingForTest(bool value) => playing = value;

    private async Task DumpPageAsync(string output)
    {
        try
        {
            if (study is null) return;
            // 诊断只读取结构，不播放视频、不答题、不提交。
            await study.ReadAsync(lifetime.Token);
            var before = await study.DumpPageAsync(lifetime.Token);
            await Task.Delay(3000);
            var after = await study.DumpPageAsync(lifetime.Token);
            var payload = string.Join("\n\n", before.Select((d, i) => $"===== BEFORE frame {i} =====\n{d}")) +
                "\n\n" + string.Join("\n\n", after.Select((d, i) => $"===== AFTER frame {i} =====\n{d}"));
            File.WriteAllText(output, payload);
            log.Write("INFO", $"页面诊断已写入：{output}");
        }
        catch (Exception error) { log.Write("ERROR", $"页面诊断失败：{error.GetType().Name}"); }
    }

    private async void ProgressTick(object? sender, EventArgs e)
    {
        await RefreshStudyAsync();
        // 课程流程只在显式启动后推进，未启动时不产生任何页面读取。
        if (courseWorkflow.IsActive) await CourseFlowTickAsync(lifetime.Token);
    }
    internal async Task RefreshStudyAsync(AiConfiguration? aiConfig = null, AiClient? aiClient = null)
    {
        // 切换配置在后台静默进行，不打断轮询与当前会话。
        if (study is null || ticking || lifetime.IsCancellationRequested) return;
        ticking = true;
        var token = workflow.Token;
        try
        {
            var frames = await study.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            var snapshots = frames.Select(f => f.Snapshot).ToArray();
            var videoCount = snapshots.Sum(s => s.VideoCount);
            var audioCount = snapshots.Sum(s => s.AudioCount);
            var mediaType = audioCount > 0 ? videoCount > 0 ? "音视频学习" : "音频学习" : "视频学习";
            var questionCount = snapshots.Sum(s => Math.Max(s.QuestionTotal, s.Questions.Length));
            CourseTypeText.Text = questionCount > 0 ? videoCount + audioCount > 0 ? $"{mediaType} + 答题考核" : "答题考核类" : videoCount + audioCount > 0 ? $"{mediaType}类" : "待识别 / 其他内容";
            var catalog = frames.FirstOrDefault(f => f.Snapshot.Total > 0);
            if (catalog is not null && catalog.Snapshot.Url.Length > 0 && catalog.Snapshot.Url != lastSavedCourse)
            {
                lastSavedCourse = catalog.Snapshot.Url;
                taskStore.Save(new TaskSnapshot { CourseUrl = catalog.Snapshot.Url, CourseId = catalog.Snapshot.CourseId, ChapterId = catalog.Snapshot.ChapterId, SavedAt = DateTimeOffset.Now });
            }
            var percent = catalog is null ? null : StudyProgress.Percent(catalog.Snapshot.Completed, catalog.Snapshot.Total);
            CourseProgressBar.Value = percent ?? 0;
            CourseProgressText.Text = percent is null ? "待确认：未识别完整章节目录" : $"{percent:0.#}% · {catalog!.Snapshot.Completed}/{catalog.Snapshot.Total} 章（网页标记）";
            var media = snapshots.Where(s => s.MediaCount > 0).ToArray();
            var duration = media.Sum(s => s.Duration);
            double? videoPercent = media.Length > 0 && media.All(s => s.Duration > 0) ? Math.Clamp(100 * media.Sum(s => s.Position) / duration, 0, 100) : null;
            VideoProgressBar.Value = videoPercent ?? 0;
            var position = media.Sum(s => s.Position);
            if (videoPercent is not null && lastVideoPosition is not null && position > lastVideoPosition + 0.1) videoPositionAdvanced = true;
            lastVideoPosition = videoPercent is null ? null : position;
            VideoProgressText.Text = videoPercent is null ? "待确认：音视频时长未就绪" : $"{videoPercent:0.#}% · 当前课时播放比例";
            ProgressUpdatedText.Text = $"更新于 {DateTime.Now:HH:mm:ss} · 每 2 秒读取";
            if (snapshots.Any(s => s.QuestionOverflow))
            {
                StudyStatusText.Text = "题目数量或内容过大，已停止自动提交与推进，请分批查看题目";
                return;
            }
            if (snapshots.Any(s => s.QuestionPagePending))
            {
                StudyStatusText.Text = "等待新题目内容加载，暂不调用模型或交卷";
                return;
            }
            if (catalog is not null && completionTracker.Update(catalog.Snapshot.CourseId, catalog.Snapshot.Completed, catalog.Snapshot.Total))
            {
                log.Write("INFO", "网页章节全部标记完成，重新加载以核对全部进度。");
                completionRefreshes++;
                ActiveView.Reload();
                return;
            }
            // 题目检测独立于视频启动；停止按钮同时关闭自动答题。
            if (automaticAnswersEnabled && questionCount > 0 && !asking && DateTime.UtcNow >= nextAutoAnswerAt)
            {
                var generation = answerGeneration;
                asking = true;
                var filled = false;
                try { filled = await AutoAnswerCurrentAsync(token, aiConfig, aiClient); }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    nextAutoAnswerAt = DateTime.UtcNow.AddSeconds(30);
                    StudyStatusText.Text = "自动答题未完成，请查看日志与 .env 配置";
                    // 原脚本对 401 / 429 / 超时分别给出原因；只输出受控摘要，不带网址、题干或密钥。
                    var reason = error is HttpRequestException http
                        ? http.StatusCode is { } code ? $"HTTP {(int)code} 请求失败" : "网络请求失败"
                        : error is InvalidOperationException ? "配置或流程校验未通过" : error.GetType().Name;
                    log.Write("WARN", $"自动答题未完成：{reason}，30 秒后重试；请检查 .env 或页面。");
                }
                finally { asking = false; }
                // 答题过程中可能发生导航，不使用请求前的旧快照继续点击。
                if (filled || generation != answerGeneration) return;
            }
            if (!playing)
            {
                // 自动播放是每个配置独立的开关：切换后默认不自动启动。
                // 明确写出原因，避免页面能读、进度能刷新，却看起来像「自动播放坏了」。
                StudyStatusText.Text = "当前配置未启动自动播放；点「开始自动播放」启动";
                return;
            }
            var chapter = catalog is null ? "" : $"{catalog.Snapshot.CourseId}:{catalog.Snapshot.ChapterId}";
            if (pendingChapter is not null)
            {
                if (chapter.Length > 0 && chapter != pendingChapter) { pendingChapter = null; chapterTransitions++; }
                else
                {
                    if (DateTime.UtcNow - chapterRequestedAt > TimeSpan.FromSeconds(12))
                    { playing = false; pendingChapter = null; StudyStatusText.Text = "章节未切换，自动流程已暂停，请检查网页"; }
                    return;
                }
            }
            // 音视频达到阈值后，目录确认整章完成时优先跨章；否则保留原同章流程。
            // MediaFinished 来自逐个媒体的阈值/自然结束判断，不能用多个媒体的平均进度代替。
            if (AutoAdvanceBox.IsChecked == true && catalog?.Snapshot.CurrentComplete == true &&
                snapshots.Any(s => s.MediaFinished > 0))
            {
                var result = await study.CommandAsync(catalog, "advance", token);
                if (result == "\"clicked\"") { pendingChapter = chapter; chapterRequestedAt = DateTime.UtcNow; }
                StudyStatusText.Text = result == "\"clicked\"" ? "音视频已达阈值且章节已完成，已请求下一章节" : "等待章节更新或已到目录末尾";
                return;
            }
            // 1) 优先播放未完成的音视频（通常位于子 iframe 帧中）。
            var target = frames.FirstOrDefault(f => f.Snapshot.MediaCount > f.Snapshot.MediaFinished);
            if (target is not null)
            {
                await study.CommandAsync(target, "play", token);
                StudyStatusText.Text = string.IsNullOrEmpty(target.Snapshot.MediaStatus) ? "正在准备音视频" : target.Snapshot.MediaStatus;
                return;
            }
            // 2) 没有未完成的媒体时，点击下一个音视频标签。
            var tabFrame = frames.FirstOrDefault(f => f.Snapshot.MediaTabCount > 0);
            if (tabFrame is not null)
            {
                var tabResult = await study.CommandAsync(tabFrame, "play", token);
                if (tabResult != "\"ended\"") { StudyStatusText.Text = "正在切换音视频标签"; return; }
            }
            var document = frames.FirstOrDefault(f => f.Snapshot.DocumentCount > 0 && !f.Snapshot.DocumentComplete);
            if (document is not null)
            {
                var docResult = await study.CommandAsync(document, "advanceDocument", token);
                StudyStatusText.Text = docResult == "\"clicked\"" ? "正在翻阅文档" : docResult == "\"scrolled\"" ? "已滚动到文档底部，等待加载" : docResult == "\"done\"" ? "文档已浏览完毕" : "等待文档加载";
                return;
            }
            // 任务点标签与章节目录可能分属不同 iframe，同章推进不依赖目录完成标记。
            var taskFrame = frames.FirstOrDefault(f => f.Snapshot.TaskPointCount > 0);
            if (taskFrame is not null && await study.CommandAsync(taskFrame, "advanceTaskPoint", token) == "\"clicked\"")
            {
                StudyStatusText.Text = "已进入下一个任务点";
                return;
            }
            if (AutoAdvanceBox.IsChecked != true) { StudyStatusText.Text = "当前课时任务已处理，等待手动切换章节"; return; }
            if (catalog?.Snapshot.CurrentComplete == true)
            {
                var result = await study.CommandAsync(catalog, "advance", token);
                if (result == "\"clicked\"") { pendingChapter = chapter; chapterRequestedAt = DateTime.UtcNow; }
                StudyStatusText.Text = result == "\"clicked\"" ? "已请求下一章节，等待页面更新" : "等待章节更新或已到目录末尾";
            }
            else StudyStatusText.Text = questionCount > 0 ? "等待网页提交结果或平台完成标记" : "等待平台确认当前章节完成";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.Write("WARN", $"学习状态读取未完成：{error.GetType().Name}"); }
        finally { ticking = false; WriteAcceptance(); }
    }

    private async void StartStudyClick(object sender, RoutedEventArgs e)
    {
        if (study is null) return;
        StartStudyButton.IsEnabled = false;
        try
        {
            await BeginAutomaticStudyAsync(workflow.Token);
            await RefreshStudyAsync();
        }
        finally { StartStudyButton.IsEnabled = true; }
    }

    internal async Task StopStudyAsync()
    {
        playing = false;
        automaticAnswersEnabled = false;
        pendingChapter = null;
        workflow.Cancel();
        workflow.Dispose();
        workflow = new();
        InvalidateAnswers();
        if (study is not null) await study.StopAsync();
        StudyStatusText.Text = "已停止自动流程";
    }

    private async Task<bool> AutoAnswerCurrentAsync(CancellationToken token, AiConfiguration? suppliedConfig = null, AiClient? suppliedClient = null)
    {
        if (study is null) return false;
        var config = suppliedConfig ?? AiConfiguration.LoadFile(envPath);
        log.RegisterSecret(config.ApiKey);
        try { config.Validate(); }
        catch (InvalidOperationException error)
        {
            nextAutoAnswerAt = DateTime.UtcNow.AddSeconds(30);
            StudyStatusText.Text = "AI 配置不完整或无效，请查看日志中的缺失字段及配置文件路径";
            // 此处消息仅来自配置校验，包含字段名和文件路径，不包含配置值或密钥。
            log.Write("WARN", $"自动答题暂停：{error.Message}30 秒后重新读取配置。");
            return true;
        }
        foreach (var frame in (await study.ReadAsync(token)).Where(frame => frame.Snapshot.Questions.Length > 0))
        {
            var result = await study.CommandAsync(frame, "prepareQuestionView", token);
            if (result is "\"clicked\"" or "\"waiting\"")
            { StudyStatusText.Text = "正在切换整卷浏览，等待完整题目加载"; return true; }
        }
        await study.PrepareFontsAsync(token);
        var executable = OcrClient.ResolveExecutable(envPath);
        if (ocrClient is null || executable != ocrExecutable)
        {
            ocrExecutable = executable;
            ocrClient = new OcrClient(executable, Path.Combine(dataRoot, "ocr-work"));
        }
        StudyStatusText.Text = "正在检查图片文字和字体 OCR 回退";
        await study.PrepareOcrAsync(ocrClient, token);
        var frames = await study.ReadAsync(token);
        var questions = frames.SelectMany(f => f.Snapshot.Questions.Select(q => (Frame: f, Question: q))).ToArray();
        if (questions.Length == 0) return false;
        var generation = answerGeneration;
        var client = suppliedClient ?? new AiClient(aiHttp);
        var filled = 0;
        var incompleteAnswer = false;
        var submitted = false;
        var paging = false;
        var questionNumber = 0;
        foreach (var item in questions)
        {
            questionNumber++;
            token.ThrowIfCancellationRequested();
            var key = item.Frame.Snapshot.DocumentId + ":" + item.Question.Id;
            if (item.Question.Kind == "unsupported")
            {
                nextAutoAnswerAt = DateTime.UtcNow.AddSeconds(30);
                log.Write("WARN", item.Question.Issue is "font" or "image" ? $"题目文字未还原：{study.FontDiagnostic}；{study.OcrDiagnostic}。" : $"自动答题跳过不支持题目：{item.Question.Issue}。");
                continue;
            }
            if (!answeredQuestions.TryGetValue(key, out var answer))
            {
                StudyStatusText.Text = "正在自动生成并填写答案";
                try { answer = await client.AskAsync(config, item.Question, token); }
                catch (AiAnswerFormatException error)
                {
                    // 无法确定或缺空的答案只阻断本题，不阻断同页其余题目的复习。
                    // 不缓存无效答案；下次重试仍需完整核验，整卷漏题时不提交。
                    token.ThrowIfCancellationRequested();
                    if (generation != answerGeneration) throw new OperationCanceledException();
                    incompleteAnswer = true;
                    nextAutoAnswerAt = DateTime.UtcNow.AddSeconds(30);
                    var kind = item.Question.Kind switch { "blank" => "填空", "multiple" => "多选", _ => "单选/判断" };
                    log.Write("WARN", $"本轮第 {questionNumber}/{questions.Length} 题（{kind}）：{error.Message}已跳过并继续其余题目；30 秒后重试，未全部核验前不提交。");
                    continue;
                }
                token.ThrowIfCancellationRequested();
                if (generation != answerGeneration) throw new OperationCanceledException();
                answeredQuestions[key] = answer;
                aiAnswersGenerated++;
            }
            token.ThrowIfCancellationRequested();
            if (generation != answerGeneration) throw new OperationCanceledException();
            if (await study.FillAsync(item.Frame, item.Question, answer, token))
            { if (verifiedQuestions.Add(key)) { filled++; answersFilled++; } }
            else nextAutoAnswerAt = DateTime.UtcNow.AddSeconds(30);
        }
        if (filled > 0)
        {
            StudyStatusText.Text = $"自动答题已填写 {filled}/{questions.Length} 题，正在核验提交条件";
            log.Write("INFO", StudyStatusText.Text);
        }
        foreach (var group in questions.GroupBy(q => q.Frame))
        {
            token.ThrowIfCancellationRequested();
            if (generation != answerGeneration) throw new OperationCanceledException();
            var frame = group.Key;
            if (group.Any(q => q.Question.Kind == "unsupported" || !answeredQuestions.ContainsKey(frame.Snapshot.DocumentId + ":" + q.Question.Id))) continue;
            var entries = group.Select(q => (q.Question, answeredQuestions[frame.Snapshot.DocumentId + ":" + q.Question.Id])).ToArray();
            if (await study.SubmitAnswersAsync(frame, entries, token, localOnly: true))
            {
                paging = true;
                StudyStatusText.Text = "已处理当前题目的保存/提交，下一轮重新核验后继续";
                continue;
            }
            var pageResult = await study.AdvanceQuestionPageAsync(frame, entries, token);
            if (pageResult is not ("none" or "done"))
            {
                paging = true;
                StudyStatusText.Text = pageResult == "clicked" ? "当前题目已核验，正在遍历其余题目" : pageResult == "saved" ? "已保存当前答案，等待重新读回核验" : pageResult == "waiting" ? "等待题目页面更新" : "题目分页或填写尚未全部核验，已暂停交卷";
                continue;
            }
            if (await study.SubmitAnswersAsync(frame, entries, token))
            {
                submissionsRequested++;
                submitted = true;
                StudyStatusText.Text = "答案核验通过，已自动点击提交，请以网页反馈为准";
                log.Write("INFO", StudyStatusText.Text);
            }
        }
        if (incompleteAnswer && !submitted && !paging)
            StudyStatusText.Text = "仍有题目未获得完整答案，其余题目已继续处理；30 秒后重试，暂不提交";
        return filled > 0 || submitted || paging;
    }

    private void DisposeStudy()
    {
        progressTimer.Stop();
        workflow.Cancel();
        study?.Dispose();
        workflow.Dispose();
        aiHttp.Dispose();
    }
}
