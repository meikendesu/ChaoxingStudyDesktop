using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Desktop.App.Browser;
using Desktop.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

// 仅由 --smoke-test 显式启动，使用单独目录与本地 fixture，不访问真实课程。
internal static partial class SmokeTest
{
    public static async Task RunAsync(MainWindow window, BrowserHost browser, WebView2 view, string root, string phase)
    {
        if (phase.StartsWith("perf-", StringComparison.Ordinal))
        {
            await PerfTest.RunAsync(window, browser, view, root, phase);
            return;
        }
        try
        {
            var footer = ((Grid)window.Content).Children.OfType<DockPanel>().Single();
            var versionText = footer.Children.OfType<TextBlock>().Single(text => DockPanel.GetDock(text) == Dock.Right);
            var version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
            Require(versionText.Text.StartsWith(version + " ", StringComparison.Ordinal), "页脚版本与程序版本不一致");
            Require(window.FindName("CatalogButton") is null && window.FindName("CatalogHost") is null &&
                window.FindName("CatalogAddressText") is null, "课程后台入口或隐藏采集窗口仍存在");
            Require(typeof(StudyProgress).Assembly.GetType("Desktop.Core.CatalogWebServer") is null &&
                typeof(MainWindow).Assembly.GetType("Desktop.App.Browser.CourseCatalogBrowser") is null,
                "课程后台服务或采集器仍被编译到程序");
            Require(!view.CoreWebView2.Settings.AreDefaultContextMenusEnabled, "默认右键菜单仍启用");
            Require(view.CoreWebView2.IsMuted, "浏览器没有默认静音");
            var muteButton = (Button)window.FindName("MuteButton");
            muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!view.CoreWebView2.IsMuted, "取消静音按钮无效");
            await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
            Require(!view.CoreWebView2.IsMuted, "导航覆盖了用户取消静音的选择");
            muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(view.CoreWebView2.IsMuted, "重新静音按钮无效");
            Require(window.FindName("AddressBox") is null && window.FindName("GoButton") is null &&
                window.FindName("ScanButton") is null && window.FindName("LoginButton") is null, "冗余控件仍存在");
            Require(window.FindName("VideoCountText") is null && window.FindName("QuestionCountText") is null,
                "重复的统计卡片仍存在");
            Require(!view.CoreWebView2.Settings.IsWebMessageEnabled, "旧网页消息桥接仍启用");
            var automaticChapter = (CheckBox)window.FindName("AutoAdvanceBox");
            var originalAutomaticChapter = automaticChapter.IsChecked;
            automaticChapter.IsChecked = false;
            Require(!new SettingsStore(root).Load().Settings.AutoAdvance, "自动切章开关没有即时保存");
            automaticChapter.IsChecked = originalAutomaticChapter;
            var bundledRuntime = browser.RuntimeDirectory;
            var runtimeProcess = System.Diagnostics.Process.GetProcessById((int)view.CoreWebView2.BrowserProcessId);
            var bundledDotNet = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
            if (bundledDotNet)
                Require(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets"), "catalog*").Length == 0,
                    "正式发布目录仍携带课程后台资源");
            if (bundledDotNet)
                Require(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Any(module =>
                    module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(module.FileName, Path.Combine(AppContext.BaseDirectory, "coreclr.dll"), StringComparison.OrdinalIgnoreCase)),
                    "没有从发布包加载 .NET 运行时");
            if (bundledRuntime is not null)
                Require(runtimeProcess.MainModule?.FileName?.StartsWith(bundledRuntime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true,
                    "浏览器没有使用包内 WebView2 运行时");
            window.UpdateLayout();
            Require(view.ActualHeight > 400, "浏览器没有获得主内容区高度");
            var percentControl = (Slider)window.FindName("AdvancePercentSlider");
            var rateControl = (ComboBox)window.FindName("PlaybackRateBox");
            var originalPercent = percentControl.Value;
            var originalRate = rateControl.SelectedItem;
            percentControl.Value = 97;
            rateControl.SelectedItem = 1.5;
            var playbackSettings = new SettingsStore(root).Load().Settings;
            Require(playbackSettings.AdvanceAtPercent == 97 && playbackSettings.PlaybackRate == 1.5, "概览页调整倍速与阈值后未持久化，重启会丢失");
            percentControl.Value = originalPercent;
            rateControl.SelectedItem = originalRate;
            using (var stream = File.Create(Path.Combine(root, phase + "-browser.png")))
                await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(root, phase + "-window.png"))) encoder.Save(stream);

            await NavigateAndRead(browser, "https://study-desktop.local/fixture.html");
            var snapshot = JsonSerializer.Deserialize<FrameStudySnapshot>(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot()"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Require(view.CoreWebView2.DocumentTitle == "离线桥接验收课程", "原生页面标题读取失败");
            Require(snapshot.VideoCount == 2 && snapshot.Questions.Length == 3, "可见元素统计或嵌套去重失败");
            Require(await view.ExecuteScriptAsync("typeof window.__studyDesktop?.snapshot") == "\"function\"", "学习代理尚未接入");
            await VerifyMediaLoadingStatusAsync(browser, view);
            await VerifyReadOnlyReviewAsync(window, browser, view);
            await VerifyOcrBindingAsync(browser, view);
            await VerifyOcrScriptParityAsync(browser, view);
            await VerifyOcrEngineAsync(browser, view, root);
            await VerifyFullPaperAsync(browser, view);
            await VerifyAdditionalScriptParityAsync(browser, view);
            await VerifyQuestionParityAsync(browser, view);
            await VerifyAnswerCacheParityAsync(window, browser, view);
            await VerifyQuestionPaginationAsync(window, browser, view);
            await VerifyRichTextBlanksAsync(browser, view);
            await VerifyMixedVisibilityAndAdvanceAsync(browser, view);
            await VerifyTaskPointControlsAsync(browser, view);
            await VerifyComputedFontSourceAsync(browser, view);
            await VerifyStudyAsync(browser, view, root);
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "configuration-start\n");
            await VerifyConfigurationAfterNavigationAsync(browser, view);
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "anti-pause-start\n");
            await VerifyAntiPauseAsync(browser, view);
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "anti-pause-done\n");
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "auto-answer-start\n");
            await VerifyAutomaticAnswersAsync(window, browser, view);
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "auto-answer-done\n");
            await VerifyTaskPointAsync(browser, view, root);
            await VerifyThresholdNavigationAsync(window, browser, view);
            await VerifyVideoTabsAsync(window, browser, view, root);
            await VerifyAudioAsync(window, browser, view, root);
            await VerifyCustomAudioAsync(window, browser, view);
            await VerifyAutoplayBlockedClickAsync(window, browser, view);
            await VerifyIncompleteAnswerIsolationAsync(window, browser, view);
            await VerifyConfigurationFailureDiagnosticAsync(window, browser, view, root);
            await VerifyDocumentAsync(browser, view, root);
            await VerifyRedirectedDocumentAsync(window, browser, view, root);
            await NavigateAndRead(browser, "https://study-desktop.local/fixture.html");

            var providerBox = (ComboBox)window.FindName("ProviderBox");
            var keyBox = (PasswordBox)window.FindName("ApiKeyBox");
            const string testKey = "smoke-only-fake-key-do-not-use";
            if (phase == "seed")
            {
                providerBox.SelectedValue = "deepseek";
                ((TextBox)window.FindName("EndpointBox")).Text = "https://example.test/v1/chat/completions";
                ((TextBox)window.FindName("ModelBox")).Text = "fixture-model";
                keyBox.Password = testKey;
                ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(keyBox.Password.Length == 0, "保存后密钥输入框未清空");
                var cookie = view.CoreWebView2.CookieManager.CreateCookie("desktop_smoke", "persisted", BrowserPolicy.LocalHost, "/");
                cookie.Expires = DateTime.UtcNow.AddDays(1);
                cookie.IsSecure = true;
                view.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                var sessionCookie = view.CoreWebView2.CookieManager.CreateCookie("desktop_session", "fake-session-only", ".chaoxing.com", "/");
                sessionCookie.IsSecure = true; sessionCookie.IsHttpOnly = true;
                view.CoreWebView2.CookieManager.AddOrUpdateCookie(sessionCookie);
                await browser.SaveSessionCookiesAsync();
                Require(new SessionCookieStore(root).Read().Any(c => c.Name == "desktop_session"), "会话 Cookie 未保存到加密文件");
                await view.ExecuteScriptAsync("localStorage.setItem('desktop_smoke', 'persisted')");
            }
            else
            {
                Require((string?)providerBox.SelectedValue == "deepseek", "模型选择未跨进程恢复");
                Require(keyBox.Password.Length == 0, "启动时回显了密钥");
                var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(BrowserPolicy.LocalStartUrl);
                Require(cookies.Any(cookie => cookie.Name == "desktop_smoke" && cookie.Value == "persisted"), "Cookie 未跨进程保留");
                Require(await view.ExecuteScriptAsync("localStorage.getItem('desktop_smoke')") == "\"persisted\"", "网站存储未跨进程保留");
                var sessions = await view.CoreWebView2.CookieManager.GetCookiesAsync("https://i.chaoxing.com");
                Require(sessions.Any(c => c.Name == "desktop_session" && c.Value == "fake-session-only" && c.IsSession && c.IsSecure && c.IsHttpOnly), "会话 Cookie 未跨进程恢复或属性丢失");
            }
            Require(AiConfiguration.LoadFile(Path.Combine(root, ".env")).ApiKey == testKey, "密钥未正确写入 .env 并读取");
            Require(!File.ReadAllText(Path.Combine(root, "settings.json")).Contains(testKey), "密钥进入了普通设置");
            var tabs = (TabControl)window.FindName("SidebarTabs");
            tabs.SelectedItem = window.FindName("SettingsTab");
            Require(((TextBox)window.FindName("ModelBox")).Text == "fixture-model", "进入设置未读取 .env");
            ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(AiConfiguration.LoadFile(Path.Combine(root, ".env")).ApiKey == testKey, "留空保存丢失密钥");
            providerBox.SelectedValue = "zhipu";
            ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(AiConfiguration.LoadFile(Path.Combine(root, ".env")).Provider == "deepseek", "换提供商复用了旧密钥");
            tabs.SelectedIndex = 0;
            File.AppendAllText(Path.Combine(root, ".env"), "\nCX_AI_MODEL=externally-updated\n");
            tabs.SelectedItem = window.FindName("SettingsTab");
            Require(((TextBox)window.FindName("ModelBox")).Text == "externally-updated", "重新进入设置没有检测外部更新");
            ((TextBox)window.FindName("ModelBox")).Text = "fixture-model";
            ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            tabs.SelectedIndex = 0;

            browser.StopLoading();

            // 确认受信任页面本身也读不到原生密钥。
            Require(await view.ExecuteScriptAsync($"document.documentElement.outerHTML.includes('{testKey}')") == "false", "密钥泄漏到网页");
            if (phase == "verify")
            {
                await browser.ClearLoginAsync(BrowserPolicy.LocalStartUrl);
                var cookies = await view.CoreWebView2.CookieManager.GetCookiesAsync(BrowserPolicy.LocalStartUrl);
                Require(cookies.All(cookie => cookie.Name != "desktop_smoke"), "浏览器资料未清除");
                Require(AiConfiguration.LoadFile(Path.Combine(root, ".env")).ApiKey == testKey, "清除登录错误删除了模型密钥");
                Require(new SessionCookieStore(root).Read().Length == 0, "清除登录后仍残留会话恢复文件");
                await VerifyCourseWorkflowAsync(window, browser, view, root);
                await VerifyAccountWebViewPoolAsync(window, browser, view, root);
            }
            File.WriteAllText(Path.Combine(root, phase + ".json"), JsonSerializer.Serialize(new
            {
                Success = true, Phase = phase, snapshot.VideoCount, QuestionCount = snapshot.Questions.Length,
                BrowserVersion = view.CoreWebView2.Environment.BrowserVersionString,
                BundledWebView2 = bundledRuntime is not null,
                BundledDotNet = bundledDotNet,
                Checks = new[] { "native-settings", "env-settings", "mute-toggle", "no-context-menu", "bridge", "cancel", "profile-persistence", "nested-cross-origin", "real-media-play-stop-ended", "chapter-once", "answer-fill-stale-rejection", "nested-task-click", "task-navigation-rerender", "computed-font-family", "hidden-task-frame", "quiz-active-no-replay", "quiz-current-no-reopen", "ueditor-blank-sync", "rich-blank-submit-readback", "atomic-question-containers", "original-option-layouts", "id-and-answer-cache", "async-pagination-no-duplicate", "full-paper-view", "image-ocr-binding", "font-ocr-fallback", "real-tesseract-chinese-and-image-options", "accessible-question-and-options", "title-priority", "nearby-mark-name", "local-save-once", "nested-option-label-dedup", "media-loading-buffer-error", "autoplay-blocked-play-button-click", "incomplete-answer-isolation", "overview-playback-persistence", "account-webview-one-to-one", "account-webview-hidden-preload", "account-webview-reuse-on-switch", "account-webview-profile-isolation", "account-webview-add-remove-sync", "account-webview-no-reload", "ocr-question-cleaning-parity", "ocr-option-aria-label-parity", "ocr-private-use-fallback", "course-page-vs-study-page", "course-task-point-completion-check", "course-flow-first-pending-task", "course-flow-finish-dialog", "course-flow-already-completed-dialog", "course-page-no-navigation", "account-autoplay-state-continuity" }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { WriteFailure(root, phase, error); }
    }

    private static async Task VerifyMediaLoadingStatusAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML='<video></video>';
            const video=document.querySelector('video');
            Object.defineProperties(video,{
              paused:{get:()=>false,configurable:true},
              readyState:{get:()=>window.fixtureReady||0},
              error:{get:()=>window.fixtureError?{code:4}:null}
            });
            """);
        var state = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null).Snapshot;
        Require(state.MediaStatus == "等待视频加载", "视频尚未加载就误报正在播放");
        await view.ExecuteScriptAsync("window.fixtureReady=2");
        state = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null).Snapshot;
        Require(state.MediaStatus == "视频正在缓冲", "视频缓冲时误报正在播放");
        await view.ExecuteScriptAsync("window.fixtureReady=4");
        state = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null).Snapshot;
        Require(state.MediaStatus == "正在播放", "已就绪视频的播放状态错误");
        await view.ExecuteScriptAsync("window.fixtureError=true");
        state = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null).Snapshot;
        Require(state.MediaStatus == "视频加载失败，请检查网页", "视频错误被播放状态覆盖");
    }

    private static async Task VerifyRichTextBlanksAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<form><section class='singleQuesId'><h3>填空：依次填写两个空</h3>
                <div class='blankItemDiv'><div class='edui-editor-iframeholder'><iframe srcdoc='<body contenteditable="true"></body>'></iframe></div></div>
                <div class='blankItemDiv'><div class='edui-editor-iframeholder'><iframe srcdoc='<body contenteditable="true"></body>'></iframe></div></div>
                </section><button type='button' onclick='window.blankSubmits++'>提交答案</button></form>`;
            window.blankSubmits=0;window.editorSyncs=0;
            """);
        while (await view.ExecuteScriptAsync("[...document.querySelectorAll('iframe')].every(f=>f.contentDocument?.body?.isContentEditable)") != "true")
            await Task.Delay(50, deadline.Token);
        await view.ExecuteScriptAsync("""
            window.UE={instants:{}};
            [...document.querySelectorAll('iframe')].forEach((frame,index)=>{
                const field=document.createElement('textarea');field.hidden=true;document.querySelector('form').append(field);
                window.UE.instants['editor'+index]={isReady:true,body:frame.contentDocument.body,textarea:field,
                    setContent(html){this.body.innerHTML=html},getContent(){return this.body.innerHTML},
                    sync(){window.editorSyncs++;this.textarea.value=this.getContent()}};
            });
            """);
        var top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        var question = top.Snapshot.Questions.Single();
        Require(question.Kind == "blank" && question.BlankCount == 2, "UEditor iframe 填空仍被识别为 controls");
        await view.ExecuteScriptAsync("document.querySelectorAll('.edui-editor-iframeholder').forEach(el=>el.style.display='none')");
        var collapsed = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.Questions.Single();
        Require(collapsed.Kind == "blank" && collapsed.BlankCount == 2, "外层空格可见、内部编辑器折叠时漏识别填空");
        Require(!await study.FillAsync(top, question, new AiAnswer([], ["缺一个"]), deadline.Token), "富文本填空漏空时仍然填写");
        Require(await study.FillAsync(top, question, new AiAnswer([], ["甲<&>", "乙"]), deadline.Token), "UEditor 填写或表单同步核验失败");
        Require(await view.ExecuteScriptAsync("window.editorSyncs===2&&UE.instants.editor0.body.textContent==='甲<&>'&&!UE.instants.editor0.body.querySelector('img,script')") == "true", "富文本答案未转义或未同步");
        const string entries = "[{question:window.__studyDesktop.snapshot().questions[0],answer:{choices:[],blanks:['甲<&>','乙']}}]";
        await view.ExecuteScriptAsync("UE.instants.editor0.textarea.value='未同步'");
        Require(await view.ExecuteScriptAsync($"window.__studyDesktop.submitAnswers(window.__studyDesktop.snapshot().documentId,{entries})") == "\"incomplete\"", "只核验可见正文，没有核验提交表单内容");
        await view.ExecuteScriptAsync("UE.instants.editor0.sync()");
        Require(await view.ExecuteScriptAsync($"window.__studyDesktop.submitAnswers(window.__studyDesktop.snapshot().documentId,{entries})") == "\"clicked\"", "富文本填空核验通过后未允许提交");
        Require(await view.ExecuteScriptAsync("window.blankSubmits") == "1", "富文本测验没有实际触发提交");
        await view.ExecuteScriptAsync("UE.instants.editor1.body.contentEditable='false'");
        Require(!await study.FillAsync(top, question, new AiAnswer([], ["甲", "乙"]), deadline.Token), "只读编辑器仍被填写");
    }

    private static async Task VerifyMixedVisibilityAndAdvanceAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("document.body.innerHTML=\"<video></video><div id='hiddenTask' style='display:none'><iframe src='/study-frame.html?nested=1'></iframe></div>\"");
        while (await view.ExecuteScriptAsync("!!document.querySelector('iframe').contentDocument?.querySelector('.questionLi')") != "true")
            await Task.Delay(50, deadline.Token);
        await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.body.append(document.createElement('video'))");
        var frames = await study.ReadAsync(deadline.Token);
        Require(frames.Sum(f => f.Snapshot.VideoCount) == 1 && frames.Sum(f => f.Snapshot.Questions.Length) == 0,
            "隐藏任务 iframe 仍计入视频/题目，可能阻塞混合课程推进");
        await view.ExecuteScriptAsync("document.querySelector('#hiddenTask').style.display='block'");
        frames = await study.ReadAsync(deadline.Token);
        Require(frames.Sum(f => f.Snapshot.VideoCount) == 2 && frames.Sum(f => f.Snapshot.Questions.Length) == 1,
            "显示后的任务 iframe 未重新参与读取");
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<ul id='coursetree'><li class='currents' onclick='void(0)'>当前章<span class='roundpoint'></span></li><li onclick='window.chapterClicks++'>下一章</li></ul>
                <ul class='prev_ul'><li onclick='window.videoClicks++'>1 视频</li><li class='active'><span onclick='window.quizClicks++'>2 章节测验</span></li></ul>`;
            window.videoClicks=0;window.quizClicks=0;window.chapterClicks=0;
            """);
        var top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(await study.CommandAsync(top, "play", deadline.Token) == "\"ended\"", "测验已激活仍重新点击之前的视频");
        Require(await study.CommandAsync(top, "advanceTaskPoint", deadline.Token) == "\"none\"", "当前测验再次被当成下一任务点");
        Require(await study.CommandAsync(top, "advance", deadline.Token) == "\"waiting\"", "平台未确认测验完成时提前跳章");
        await view.ExecuteScriptAsync("document.querySelector('.roundpoint').classList.add('done')");
        Require(await study.CommandAsync(top, "advance", deadline.Token) == "\"clicked\"", "测验完成后未进入下一章");
        Require(await view.ExecuteScriptAsync("window.videoClicks===0&&window.quizClicks===0&&window.chapterClicks===1") == "true", "混合课程提交后重播/重进测验或未推进章节");
    }

    private static async Task VerifyComputedFontSourceAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NavigateAndRead(browser, "https://study-desktop.local/font-fixture.html");
        await view.ExecuteScriptAsync("document.styleSheets[0].cssRules[0].style.fontFamily='quiz-generated-font';document.querySelector('h3').style.fontFamily='quiz-generated-font'");
        await study.PrepareFontsAsync(deadline.Token);
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.fontResult().status") == "\"applied\"", "题目实际字体名称变化后无法定位字体源");
    }

    private static async Task VerifyTaskPointControlsAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var layout in new[] { "prev_ul", "chapter_tab", "ans-cc-tab" })
        {
            await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
            var html = $"<ul class='{layout}'><li class='active'>1 视频</li><li><span onclick='window.quizClicks++'>2 章节测验</span></li></ul><div class='tabs'><ul><li onclick='window.wrongClicks++'>讨论</li></ul></div>";
            await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(html)};window.quizClicks=0;window.wrongClicks=0");
            var top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
            Require(top.Snapshot.TaskPointCount == 2, $"{layout} 任务点标签未识别");
            Require(await study.CommandAsync(top, "advanceTaskPoint", deadline.Token) == "\"clicked\"", "任务点未请求切换");
            Require(await view.ExecuteScriptAsync("window.quizClicks") == "1", "测验点击事件位于子控件，点击 li 未进入测验");
            await study.CommandAsync(top, "advanceTaskPoint", deadline.Token);
            Require(await view.ExecuteScriptAsync("window.quizClicks===1&&window.wrongClicks===0") == "true", "重复点击测验或误点目录侧栏");
        }
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        const string reloadedTabs = "<ul class='prev_ul'><li class='active' onclick='window.videoClicks++'>1 视频</li><li><span onclick='window.quizClicks++'>2 章节测验</span></li></ul>";
        await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(reloadedTabs)};window.videoClicks=0;window.quizClicks=0");
        var tabFrame = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        await study.CommandAsync(tabFrame, "startVideos", deadline.Token);
        // 模拟视频在子框架播放期间，平台重绘外层任务点导航。
        await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(reloadedTabs)}");
        Require(await study.CommandAsync(tabFrame, "play", deadline.Token) == "\"ended\"", "重绘标签后重复请求当前视频，阻止进入测验");
        await study.CommandAsync(tabFrame, "advanceTaskPoint", deadline.Token);
        Require(await view.ExecuteScriptAsync("window.videoClicks===1&&window.quizClicks===1") == "true", "导航重绘后没有按顺序进入测验");
    }

    private static async Task VerifyStudyAsync(BrowserHost browser, WebView2 view, string root)
    {
        void Trace(string step) => File.AppendAllText(Path.Combine(root, "study-trace.txt"), step + Environment.NewLine);
        Trace("start");
        view.CoreWebView2.SetVirtualHostNameToFolderMapping("fixture.chaoxing.com", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, "https://study-desktop.local/study-fixture.html");
        Trace("navigated");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        List<StudyFrame> frames;
        do { await Task.Delay(100); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 3 || frames.Sum(f => f.Snapshot.Duration) == 0);
        Trace("frames-ready");
        // 保留旧测试语义：视频播到自然结束，不受阈值提前完成影响。
        await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 100 });
        Require(frames.Sum(f => f.Snapshot.Questions.Length) == 6, "嵌套跨域题目漏读或重复");
        var top = frames.Single(f => f.Frame is null);
        Require(top.Snapshot.VideoCount == 1 && top.Snapshot.Total == 2 && top.Snapshot.Completed == 0, "隐藏内容或章节统计错误");
        Require(await study.CommandAsync(top, "advance", deadline.Token) == "\"waiting\"", "未完成章节被提前推进");
        await study.CommandAsync(top, "play", deadline.Token);
        Trace("play-requested");
        while (await view.ExecuteScriptAsync("document.querySelector('video').paused") != "false") { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
        await study.StopAsync();
        Trace("stopped");
        Require(await view.ExecuteScriptAsync("document.querySelector('video').paused") == "true", "停止未暂停媒体");
        var stopped = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot;
        Require(stopped.MediaPaused && stopped.MediaEvents.Length > 0, "媒体快照未反映真实暂停状态和事件");
        await study.CommandAsync(top, "play", deadline.Token);
        while (await view.ExecuteScriptAsync("document.querySelector('video').ended") != "true") { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(100); }
        Trace("ended");
        Require(await study.CommandAsync(top, "advance", deadline.Token) == "\"waiting\"", "视频结束被错误当成平台完成");
        await view.ExecuteScriptAsync("document.querySelector('.roundpoint').classList.add('done')");
        Require(await study.CommandAsync(top, "advance", deadline.Token) == "\"clicked\"", "完成章节未推进");
        await study.CommandAsync(top, "advance", deadline.Token);
        Require(await view.ExecuteScriptAsync("window.chapterClicks") == "1", "同章被重复点击");
        Trace("advanced");
        Require(await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([1], []), deadline.Token), "单选填写失败");
        Require(await study.FillAsync(top, top.Snapshot.Questions[1], new AiAnswer([0, 2], []), deadline.Token), "多选填写失败");
        Require(await study.FillAsync(top, top.Snapshot.Questions[2], new AiAnswer([], ["甲", "乙"]), deadline.Token), "多空填写失败");
        Require(top.Snapshot.Questions[3].Kind == "single", "自定义单选控件未识别");
        Require(await study.FillAsync(top, top.Snapshot.Questions[3], new AiAnswer([1], []), deadline.Token), "自定义单选填写失败");
        var child = frames.First(f => f.Frame is not null);
        Require(await study.FillAsync(child, child.Snapshot.Questions[0], new AiAnswer([0], []), deadline.Token), "跨域判断填写失败");
        Trace("filled");
        Require(await study.CommandAsync(top, "submit", deadline.Token) == "\"clicked\"", "提交按钮未点击");
        Require(await view.ExecuteScriptAsync("window.submitClicks") == "1", "提交未触发");
        await view.ExecuteScriptAsync("""
            window.addEventListener('message', e=>{
                if(e.data!=='fixture-submit')return;
                setTimeout(()=>{
                    const dialog=document.createElement('div');dialog.setAttribute('role','dialog');dialog.textContent='确认提交答案？';
                    const button=document.createElement('button');button.textContent='提交';
                    button.onclick=()=>{window.parentConfirmed=true;dialog.remove()};dialog.append(button);document.body.append(dialog);
                },1500);
            });
            """);
        await child.Frame!.ExecuteScriptAsync("const submit=document.createElement('button');submit.textContent='提交答案';submit.onclick=()=>top.postMessage('fixture-submit','https://study-desktop.local');document.body.append(submit)");
        await study.SubmitAnswersAsync(child, [(child.Snapshot.Questions[0], new AiAnswer([0], []))], deadline.Token);
        Require(await view.ExecuteScriptAsync("window.parentConfirmed === true") == "true", "跨域题目提交后未点击外层对话框的提交按钮");
        await view.ExecuteScriptAsync("document.querySelector('h3').textContent='题目已变化'");
        Trace("changed-question");
        Require(!await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([0], []), deadline.Token), "旧题答案误写入变化后的题目");
        Trace("rejected-stale");
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        Trace("navigated-away");
        Require(!await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([0], []), deadline.Token), "导航后旧答案未拒绝");
        Trace("rejected-old-document");
        // 原脚本会到外层文档查找字体：题目本身可能没有 style。
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("document.body.innerHTML = `<style>@font-face{font-family:font-cxsecret;src:url('/font-fixture.ttf')}</style><iframe src='/font-fixture.html'></iframe>`");
        await Task.Delay(700, deadline.Token);
        await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('style').remove()");
        await study.PrepareFontsAsync(deadline.Token);
        Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentWindow.__studyDesktop.fontResult().status") == "\"applied\"",
            "题目框架没有使用外层文档定义的字体");
        await NavigateAndRead(browser, "https://study-desktop.local/font-fixture.html");
        var fontFrames = await study.ReadAsync(deadline.Token);
        Require(fontFrames.Single().Snapshot.Questions[0].Issue == "font", "未阻止未还原字体");
        await study.PrepareFontsAsync(deadline.Token);
        var fontResult = await view.ExecuteScriptAsync("window.__studyDesktop.fontResult()");
        using (var parsed = JsonDocument.Parse(fontResult))
            Require(parsed.RootElement.GetProperty("status").GetString() == "applied", "字体资源下载或 Typr 解析失败：" + fontResult);
        var fontBytes = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "font-fixture.ttf")));
        Require(await view.ExecuteScriptAsync($"window.__studyDesktop.parseFontBytes('stale-document',{JsonSerializer.Serialize(fontBytes)})") == "false", "旧文档字体结果未拒绝");
        Require(await view.ExecuteScriptAsync($"window.__studyDesktop.parseFontBytes(window.__studyDesktop.snapshot().documentId,{JsonSerializer.Serialize(fontBytes)})") == "true", "原生字体字节未进入 Typr 解析");
        await view.ExecuteScriptAsync("window.__studyDesktop.applyFontMap(window.__studyDesktop.snapshot().documentId, {'19968':'你'})");
        var decoded = (await study.ReadAsync(deadline.Token)).Single().Snapshot.Questions[0];
        Require(decoded.Text == "你二" && decoded.Kind == "single" && decoded.Options.Length == 2, "字体回退或视觉可见选项识别失败");
        Require(await view.ExecuteScriptAsync("document.querySelector('h3').textContent") == "\"一二\"", "字体还原意外改写了 DOM");
        using var diagnostic = JsonDocument.Parse(await view.ExecuteScriptAsync("window.__studyDesktop.diagnostics()"));
        Require(diagnostic.RootElement.GetProperty("font").GetProperty("mappedCount").GetInt32() == 1, "字体诊断未记录映射数量");
        Require(diagnostic.RootElement.GetProperty("font").GetProperty("stage").GetString() == "mapped", "字体诊断阶段不正确");
        await view.ExecuteScriptAsync("document.querySelector('style').remove(); window.__studyDesktop.prepareFont()");
        using var failure = JsonDocument.Parse(await view.ExecuteScriptAsync("window.__studyDesktop.diagnostics()"));
        Require(failure.RootElement.GetProperty("font").GetProperty("status").GetString() == "failed" &&
            failure.RootElement.GetProperty("font").GetProperty("stage").GetString() == "source", "未定位字体源缺失阶段");
    }

    private static async Task VerifyConfigurationAfterNavigationAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await study.ConfigureAsync(new AppSettings { PlaybackRate = 1, AdvanceAtPercent = 100 });
        await NavigateAndRead(browser, "https://study-desktop.local/study-fixture.html?settings=1");
        List<StudyFrame> frames;
        do { await Task.Delay(100); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 3 || frames.Sum(f => f.Snapshot.Duration) == 0);
        Require(frames.All(f => f.Snapshot.PlaybackRate == 1 && f.Snapshot.AdvanceAtPercent == 100),
            "新页面或嵌套框架未继承用户倍速/阈值配置");
        var top = frames.Single(f => f.Frame is null);
        await view.ExecuteScriptAsync("window.rateChanges=0;document.querySelector('video').addEventListener('ratechange',()=>window.rateChanges++)");
        await study.CommandAsync(top, "play", deadline.Token);
        Require(await view.ExecuteScriptAsync("document.querySelector('video').playbackRate") == "1", "实际播放器未使用所选 1 倍速");
        await Task.Delay(100);
        await study.CommandAsync(top, "play", deadline.Token);
        await Task.Delay(100);
        Require(await view.ExecuteScriptAsync("window.rateChanges") == "0", "相同倍速仍被重复写入并触发 ratechange");
        await study.StopAsync();
        await view.ExecuteScriptAsync("""
            window.playerCalls=0;window.buttonCalls=0;
            window.videojs=()=>({play:()=>{window.playerCalls++;return new Promise((resolve,reject)=>setTimeout(()=>document.querySelector('video').play().then(resolve,reject),50))}});
            const button=document.createElement('button');button.className='vjs-big-play-button';
            button.onclick=()=>{window.buttonCalls++;document.querySelector('video').play()};document.body.append(button);
            """);
        await study.CommandAsync(top, "play", deadline.Token);
        await Task.Delay(150);
        Require(await view.ExecuteScriptAsync("window.playerCalls + window.buttonCalls") == "1", "同一次启动同时调用播放器和播放按钮");
        await view.ExecuteScriptAsync("document.querySelector('video').pause()");
        await study.CommandAsync(top, "play", deadline.Token);
        Require(await view.ExecuteScriptAsync("window.playerCalls + window.buttonCalls") == "1", "播放器暂停后轮询仍强制重复播放");
        await study.StopAsync();
        await view.ExecuteScriptAsync("""
            window.rateResets=0;
            document.querySelector('video').addEventListener('ratechange',()=>{
                const video=document.querySelector('video');
                if(video.playbackRate>1){window.rateResets++;video.playbackRate=1;}
            });
            """);
        await study.ConfigureAsync(new AppSettings { PlaybackRate = 2, AdvanceAtPercent = 100 });
        await study.CommandAsync(top, "play", deadline.Token);
        await Task.Delay(100);
        await study.CommandAsync(top, "play", deadline.Token);
        await Task.Delay(100);
        Require(await view.ExecuteScriptAsync("window.rateResets") == "1", "轮询反复覆盖网页调整后的实际倍速");
        Require(await view.ExecuteScriptAsync("document.querySelector('video').playbackRate") == "1", "未保留网页调整后的实际倍速");
        await study.StopAsync();
    }

    private static async Task VerifyAntiPauseAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await NavigateAndRead(browser, "https://study-desktop.local/anti-pause-fixture.html");
        List<StudyFrame> frames;
        do { await Task.Delay(50); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 2 || frames.Any(f => f.Snapshot.Duration <= 0));
        await study.ConfigureAsync(new AppSettings { PlaybackRate = 1, AdvanceAtPercent = 100 });
        var top = frames.Single(f => f.Frame is null);
        var child = frames.Single(f => f.Frame is not null);
        await view.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('mouseout',{bubbles:true}))");
        Require(await view.ExecuteScriptAsync("window.leaveCalls") == "1", "未启动时不应拦截鼠标离开");
        await study.CommandAsync(top, "play", deadline.Token);
        await study.CommandAsync(child, "play", deadline.Token);
        await Task.Delay(100);
        foreach (var expression in new[] {
            "document.dispatchEvent(new MouseEvent('mouseout',{bubbles:true}))",
            "document.dispatchEvent(new MouseEvent('mouseleave'))",
            "window.dispatchEvent(new Event('blur'))",
            "document.dispatchEvent(new Event('visibilitychange'))" })
        {
            await view.ExecuteScriptAsync(expression);
            await Task.Delay(50);
            Require(await view.ExecuteScriptAsync("document.querySelector('video').paused") == "false", "鼠标离开、失焦或可见性事件仍导致视频暂停：" + expression);
            Require(await child.Frame!.ExecuteScriptAsync("document.querySelector('video').paused") == "false", "外层页面事件导致跨域视频暂停");
            await child.Frame.ExecuteScriptAsync(expression);
            Require(await child.Frame.ExecuteScriptAsync("document.querySelector('video').paused") == "false", "跨域框架内防暂停未生效");
        }
        Require(await view.ExecuteScriptAsync("window.leaveCalls") == "1", "自动播放时离开事件仍传入页面");
        await view.ExecuteScriptAsync("document.querySelector('input').dispatchEvent(new Event('blur'));document.dispatchEvent(new MouseEvent('mouseout',{bubbles:true,relatedTarget:document.body}))");
        Require(await view.ExecuteScriptAsync("window.inputBlurs===1 && window.internalMoves===1") == "true", "防暂停误拦截输入框失焦或页面内部鼠标移动");
        await view.ExecuteScriptAsync("document.querySelector('video').pause()");
        await study.CommandAsync(top, "play", deadline.Token);
        await Task.Delay(600);
        Require(await view.ExecuteScriptAsync("document.querySelector('video').paused") == "true", "用户主动暂停后被强制重播");
        await study.StopAsync();
        await view.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('mouseout',{bubbles:true}))");
        Require(await view.ExecuteScriptAsync("window.leaveCalls") == "2", "停止后未解除防暂停");
        Require(await child.Frame!.ExecuteScriptAsync("document.querySelector('video').paused") == "true", "停止未暂停跨域视频");
        await study.CommandAsync(top, "play", deadline.Token);
        await view.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('mouseout',{bubbles:true}))");
        Require(await view.ExecuteScriptAsync("document.querySelector('video').paused") == "false", "重新开始后防暂停未恢复");
        await view.ExecuteScriptAsync("document.querySelector('video').currentTime=19.8");
        while (await view.ExecuteScriptAsync("document.querySelector('video').ended") != "true")
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
        Require((await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.VideoFinished == 1, "防暂停破坏自然播放结束判定");
        await NavigateAndRead(browser, "https://study-desktop.local/anti-pause-fixture.html?new-document=1");
        frames = await study.ReadAsync(deadline.Token);
        var next = frames.Single(f => f.Frame is null);
        await study.CommandAsync(next, "play", deadline.Token);
        await view.ExecuteScriptAsync("window.dispatchEvent(new Event('blur'))");
        Require(await view.ExecuteScriptAsync("window.leaveCalls===0 && !document.querySelector('video').paused") == "true", "新文档未继承运行中的防暂停状态");
        await study.StopAsync();
    }

    private sealed class AnswerHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"choices\\\":[0],\\\"blanks\\\":[]}\"}}]}") });
        }
    }

    private static async Task VerifyAutomaticAnswersAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        await NavigateAndRead(browser, "https://study-desktop.local/study-frame.html?nested=1");
        await view.ExecuteScriptAsync("""
            window.autoSubmits=0;
            const submit=document.createElement('button');submit.textContent='提交答案';
            submit.onclick=()=>{if(confirm('确认提交当前答案？'))window.autoSubmits++};document.body.append(submit);
            """);
        using var handler = new AnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var client = new AiClient(http);
        var config = new AiConfiguration { Provider = "fixture", Model = "fixture", ApiKey = "fake-only", Endpoint = "https://example.test/chat/completions" };
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1, "未启动视频时，检测到答题页未自动调用模型");
        Require(await view.ExecuteScriptAsync("document.querySelector('input').checked") == "true", "自动答案未填写");
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "1", "答案核验后未自动提交并确认");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1, "同一道题被重复请求模型");
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "1", "同一组答案被重复提交");
        await view.ExecuteScriptAsync("document.querySelector('h3').textContent='新的测试题'");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 2, "变化后的题目未重新答题");
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "2", "新题未自动提交");
        await view.ExecuteScriptAsync("""
            document.querySelector('h3').textContent='带页面弹窗的新题';
            document.querySelector('button').onclick=()=>{
                const dialog=document.createElement('div');dialog.setAttribute('role','dialog');dialog.textContent='确认提交答案？';
                const confirm=document.createElement('button');confirm.textContent='确定';
                confirm.onclick=()=>{window.autoSubmits++;dialog.remove()};dialog.append(confirm);document.body.append(dialog);
            };
            """);
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "3", "未处理本次提交触发的页面确认弹窗");
        await view.ExecuteScriptAsync("""
            document.querySelector('h3').textContent='延迟确认弹窗的新题';
            document.querySelector('button').onclick=()=>setTimeout(()=>{
                const dialog=document.createElement('div');dialog.setAttribute('role','dialog');dialog.textContent='确认提交答案？';
                const button=document.createElement('button');button.textContent='提交';
                button.onclick=()=>{window.delayedConfirmed=true;dialog.remove()};dialog.append(button);document.body.append(dialog);
            },1500);
            """);
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.delayedConfirmed === true") == "true", "超过一秒才出现的提交对话框未自动点击提交");
        await view.ExecuteScriptAsync("""
            document.querySelector('h3').textContent='平台自定义提交确认框';
            const oldDialog=document.createElement('div');oldDialog.className='popDiv';oldDialog.id='previous-submit-dialog';oldDialog.textContent='已有提交窗口';
            const oldButton=document.createElement('button');oldButton.textContent='提交';oldButton.onclick=()=>window.clickedPreviousDialog=true;oldDialog.append(oldButton);document.body.append(oldDialog);
            document.querySelector('button').onclick=()=>{
                const dialog=document.createElement('div');dialog.className='popDiv';dialog.textContent='确认提交答案？';
                const button=document.createElement('span');button.textContent='提 交';button.setAttribute('onclick','window.customDialogConfirmed=true;this.parentElement.remove()');
                dialog.append(button);document.body.append(dialog);
            };
            """);
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.customDialogConfirmed === true") == "true", "未点击平台自定义对话框中的二次提交按钮");
        Require(await view.ExecuteScriptAsync("window.clickedPreviousDialog !== true") == "true", "错误点击了提交前已经存在的弹窗");
        await view.ExecuteScriptAsync("document.querySelector('#previous-submit-dialog').remove()");
        await view.ExecuteScriptAsync("document.querySelector('h3').textContent='网页还有未答题提示';document.querySelector('button').onclick=()=>{if(confirm('还有未答题，确认提交？'))window.autoSubmits++}");
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "3", "网页提示还有未答题时仍确认提交");
        await view.ExecuteScriptAsync("document.querySelector('h3').textContent='含不支持题型的页面';const q=document.createElement('section');q.className='questionLi';q.textContent='无填写控件';document.body.append(q)");
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.autoSubmits") == "3", "存在未答或不支持题目时仍提交");
        await window.StopStudyAsync();
        await view.ExecuteScriptAsync("document.querySelector('h3').textContent='停止后的题目'");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 7, "停止后仍自动调用模型");
    }

    private static async Task VerifyTaskPointAsync(BrowserHost browser, WebView2 view, string root)
    {
        void Trace(string step) => File.AppendAllText(Path.Combine(root, "study-trace.txt"), step + Environment.NewLine);
        Trace("taskpoint-start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, "https://study-desktop.local/task-fixture.html");
        Trace("taskpoint-navigated");
        List<StudyFrame> frames;
        do { await Task.Delay(100); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 1 || frames.Sum(f => f.Snapshot.Duration) == 0);
        var top = frames.Single(f => f.Frame is null);
        Require(top.Snapshot.TaskPointCount == 2, "任务点 tab 未识别");
        Require(top.Snapshot.VideoTabCount == 1, "视频标签未识别");
        // 低阈值：越过阈值后视频标记完成，并可推进到下一个任务点（而非下一章节）。
        await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 1 });
        await study.CommandAsync(top, "play", deadline.Token);
        while (await view.ExecuteScriptAsync("document.querySelector('video').paused") != "false") { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
        for (var i = 0; i < 300; i++)
        {
            var s = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot;
            if (s.VideoFinished >= 1) break;
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(50);
        }
        Require((await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.VideoFinished >= 1, "阈值到达后视频未标记完成");
        Require(await study.CommandAsync(top, "advanceTaskPoint", deadline.Token) == "\"clicked\"", "任务点未推进");
        Require(await view.ExecuteScriptAsync("window.tabClicks") == "1", "任务点 tab 未点击");
        Trace("taskpoint-done");
    }

    private static async Task VerifyThresholdNavigationAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        var slider = (Slider)window.FindName("AdvancePercentSlider");
        var auto = (CheckBox)window.FindName("AutoAdvanceBox");
        var previousPercent = slider.Value;
        var previousAuto = auto.IsChecked;
        try
        {
            slider.Value = 1;
            foreach (var (hasCatalog, autoAdvance, completed) in new[] { (true, true, false), (false, false, false), (true, false, true) })
            {
                await window.StopStudyAsync();
                await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
                var catalog = hasCatalog ? $"<ul id='coursetree'><li class='currents' onclick='window.chapterClicks++'>当前章节<span class='roundpoint{(completed ? " done" : "")}'></span></li><li onclick='window.chapterClicks++'>下一章</li></ul>" : "";
                await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(catalog + "<iframe src='/task-fixture.html' style='width:600px;height:450px'></iframe>")};window.chapterClicks=0");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (await view.ExecuteScriptAsync("!!document.querySelector('iframe')?.contentDocument?.querySelector('video')?.duration") != "true")
                    await Task.Delay(50, deadline.Token);
                await view.ExecuteScriptAsync("""
                    const doc=document.querySelector('iframe').contentDocument;
                    doc.querySelector('#coursetree').remove();
                    const quiz=doc.querySelectorAll('.prev_ul li')[1];
                    quiz.onclick=()=>{doc.defaultView.quizClicks=(doc.defaultView.quizClicks||0)+1};
                    """);
                auto.IsChecked = autoAdvance;
                if (!hasCatalog) await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('.prev_ul').className='prev_tab'");
                ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!deadline.IsCancellationRequested && await view.ExecuteScriptAsync("document.querySelector('iframe').contentWindow.quizClicks || 0") == "0")
                {
                    await Task.Delay(50);
                    await window.RefreshStudyAsync();
                }
                Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentWindow.quizClicks || 0") == "1", "达到阈值后未切入另一框架中的测验任务点");
                await window.RefreshStudyAsync();
                Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentWindow.quizClicks") == "1", "阈值推进重复点击测验");
                Require(await view.ExecuteScriptAsync("window.chapterClicks") == "0", "阈值推进错误跳过测验进入下一章");
            }
            await window.StopStudyAsync();
            await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
            await view.ExecuteScriptAsync("""
                document.body.innerHTML = "<ul id='coursetree'><li class='currents' onclick='void(0)'>当前章节<span class='roundpoint done'></span></li><li onclick='window.chapterClicks++'>下一章</li></ul><iframe src='/task-fixture.html' style='width:600px;height:450px'></iframe>";
                window.chapterClicks = 0;
                """);
            using (var directDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                while (await view.ExecuteScriptAsync("!!document.querySelector('iframe')?.contentDocument?.querySelector('video')?.duration") != "true")
                    await Task.Delay(50, directDeadline.Token);
                await view.ExecuteScriptAsync("""
                    const doc = document.querySelector('iframe').contentDocument;
                    doc.querySelector('#coursetree').remove();
                    doc.defaultView.quizClicks = 0;
                    doc.querySelectorAll('.prev_ul li')[1].onclick = () => doc.defaultView.quizClicks++;
                    // 真实十秒媒体，避免启动后立即自然结束掩盖“未到阈值不跳章”。
                    const bytes = new Uint8Array(160044), head = new DataView(bytes.buffer);
                    const wav = (offset, str) => [...str].forEach((c,i) => bytes[offset+i] = c.charCodeAt(0));
                    wav(0,'RIFF'); head.setUint32(4,160036,true); wav(8,'WAVE'); wav(12,'fmt ');
                    head.setUint32(16,16,true);head.setUint16(20,1,true);head.setUint16(22,1,true);
                    head.setUint32(24,8000,true);head.setUint32(28,16000,true);head.setUint16(32,2,true);head.setUint16(34,16,true);
                    wav(36,'data');head.setUint32(40,160000,true);
                    doc.querySelector('video').src = URL.createObjectURL(new Blob([bytes],{type:'audio/wav'}));
                    const second = doc.createElement('video');second.id='second';second.muted=true;second.controls=true;
                    second.src = URL.createObjectURL(new Blob([bytes],{type:'audio/wav'}));doc.body.append(second);
                    """);
                while (await view.ExecuteScriptAsync("[...document.querySelector('iframe').contentDocument.querySelectorAll('video')].every(v=>v.duration>0)") != "true")
                    await Task.Delay(50, directDeadline.Token);
                slider.Value = 95;
                auto.IsChecked = true;
                ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!((Button)window.FindName("StartStudyButton")).IsEnabled)
                    await Task.Delay(50, directDeadline.Token);
                await window.RefreshStudyAsync();
                Require(await view.ExecuteScriptAsync("window.chapterClicks") == "0", "目录完成但视频未到阈值时提前跳章");
                await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('video').currentTime=9.6");
                for (var i = 0; i < 10 && await view.ExecuteScriptAsync("window.chapterClicks") == "0"; i++)
                { await Task.Delay(50, directDeadline.Token); await window.RefreshStudyAsync(); }
                Require(await view.ExecuteScriptAsync("window.chapterClicks") == "1", "达到阈值且目录完成时未优先进入下一章");
                Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentWindow.quizClicks") == "0", "目录完成仍错误进入同章测验");
                Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('#second').paused") == "true", "目录完成仍错误启动同章剩余视频");
                await window.RefreshStudyAsync();
                Require(await view.ExecuteScriptAsync("window.chapterClicks") == "1", "等待章节更新时重复跳章");
            }
            await window.StopStudyAsync();
            await NavigateAndRead(browser, "https://study-desktop.local/anti-pause-fixture.html");
            using var mediaDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var study = new StudyBrowser(view.CoreWebView2, browser);
            List<StudyFrame> mediaFrames;
            do { await Task.Delay(50, mediaDeadline.Token); mediaFrames = await study.ReadAsync(mediaDeadline.Token); }
            while (mediaFrames.Sum(f => f.Snapshot.Duration) == 0);
            await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 1, PlaybackRate = 1 });
            await view.ExecuteScriptAsync("document.querySelector('video').play();document.querySelector('video').currentTime=2");
            var mediaFrame = mediaFrames.Single(f => f.Frame is null);
            await study.CommandAsync(mediaFrame, "play", mediaDeadline.Token);
            var state = (await study.ReadAsync(mediaDeadline.Token)).Single(f => f.Frame is null).Snapshot;
            Require(state.VideoFinished == 1 && state.VideoEnded == 0, "网页已经播放的视频未按阈值完成判定");
        }
        finally { await window.StopStudyAsync(); slider.Value = previousPercent; auto.IsChecked = previousAuto; }
    }

    private static async Task VerifyVideoTabsAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        void Trace(string step) => File.AppendAllText(Path.Combine(root, "study-trace.txt"), step + Environment.NewLine);
        Trace("video-tabs-start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, "https://study-desktop.local/video-tabs-fixture.html");
        // 页面可能默认选中后面的视频；用户开始学习仍应从第一条开始。
        await view.ExecuteScriptAsync("selectVideo(1); window.videoOrder = []; window.tabClicks = 0;");
        List<StudyFrame> frames;
        do { await Task.Delay(100); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 1 || frames.Sum(f => f.Snapshot.Duration) == 0);
        var top = frames.Single(f => f.Frame is null);
        Require(top.Snapshot.VideoTabCount == 2, "视频标签未识别为 2 个");
        ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 40 && await view.ExecuteScriptAsync("window.tabClicks || 0") == "0"; i++) await Task.Delay(50);
        while (!((Button)window.FindName("StartStudyButton")).IsEnabled)
        { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
        Require(await view.ExecuteScriptAsync("JSON.stringify(window.videoOrder)") == "\"[0]\"", "点击开始未先点击网页视频标签");
        await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 1 });
        await study.CommandAsync(top, "play", deadline.Token);
        while (await view.ExecuteScriptAsync("document.querySelector('video').paused && window.__studyDesktop.snapshot().videoFinished === 0") == "true") { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
        for (var i = 0; i < 300; i++)
        {
            var s = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot;
            if (s.VideoFinished >= 1) break;
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(50);
        }
        Require(await study.CommandAsync(top, "play", deadline.Token) == "\"requested\"", "未自动点击下一个视频标签");
        Require(await view.ExecuteScriptAsync("JSON.stringify(window.videoOrder)") == "\"[0,1]\"", "多条视频未依次推进或重新点击了第一条");
        for (var i = 0; i < 300; i++)
        {
            var result = await study.CommandAsync(top, "play", deadline.Token);
            var s = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot;
            if (i % 20 == 0) File.WriteAllText(Path.Combine(root, "video-tab-diagnostic.json"), JsonSerializer.Serialize(new { result, s.VideoCount, s.VideoFinished, s.Position, s.Duration, s.MediaStatus,
                Details = await view.ExecuteScriptAsync("JSON.stringify({paused:document.querySelector('video').paused,ready:document.querySelector('video').readyState,source: sources.indexOf(document.querySelector('video').currentSrc),order:window.videoOrder})") }));
            if (s.VideoFinished == 1) break;
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(50);
        }
        Require(await study.CommandAsync(top, "play", deadline.Token) == "\"ended\"", "视频播完后仍重复切换");
        Require(await view.ExecuteScriptAsync("JSON.stringify(window.videoOrder)") == "\"[0,1]\"", "播放顺序出现重复");
        await window.StopStudyAsync();
        Trace("video-tabs-done");
    }

    private static async Task VerifyDocumentAsync(BrowserHost browser, WebView2 view, string root)
    {
        void Trace(string step) => File.AppendAllText(Path.Combine(root, "study-trace.txt"), step + Environment.NewLine);
        Trace("document-start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, "https://study-desktop.local/doc-fixture.html");
        List<StudyFrame> frames;
        do { await Task.Delay(100); frames = await study.ReadAsync(deadline.Token); }
        while (frames.Count < 1);
        var top = frames.Single(f => f.Frame is null);
        Require(top.Snapshot.DocumentCount == 1, "文档任务点未识别");
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"clicked\"", "文档未翻页");
        Require(await view.ExecuteScriptAsync("window.docClicks") == "1", "下一页未点击");
        await study.CommandAsync(top, "advanceDocument", deadline.Token);
        Require((await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.DocumentComplete, "分页文档末页未完成");
        // 模拟实际网盘预览：内层阅读区滚动，外层课程页及侧栏不应被滚动。
        await view.ExecuteScriptAsync("""
            document.body.innerHTML = '<div class="document-reader"><aside class="thumbnail-list" style="height:120px;overflow:auto;width:180px"><div style="height:2000px">缩略图</div></aside><div id="reader" style="height:240px;width:600px;overflow:auto"><div id="pages" style="height:1600px">文档正文</div></div><div hidden style="height:400px;overflow:auto"><div style="height:9000px"></div></div></div>';
            history.replaceState(null, '', '/screen/v2/document-fixture');
            window.readerScrolls = 0;
            document.getElementById('reader').addEventListener('scroll', () => window.readerScrolls++);
            """);
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(!top.Snapshot.DocumentComplete, "未滚动文档误报完成");
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"scrolled\"", "滚动式文档未滚到底部");
        await Task.Delay(100, deadline.Token);
        Require(await view.ExecuteScriptAsync("document.getElementById('reader').scrollTop > 1000 && window.readerScrolls > 0 && document.querySelector('aside').scrollTop === 0") == "true", "未触发阅读区滚动或误滚侧栏");
        Require(!(await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.DocumentComplete, "滚动后未等待延迟加载就提前完成");
        await view.ExecuteScriptAsync("document.getElementById('pages').style.height = '2200px'");
        Require(!(await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.DocumentComplete, "新增文档内容未阻止完成");
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"scrolled\"", "延迟加载后未继续滚动");
        await Task.Delay(2200, deadline.Token);
        Require((await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.DocumentComplete, "滚到底部稳定后未完成");
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"done\"", "滚动末尾未返回完成");
        await view.ExecuteScriptAsync("document.body.innerHTML = '<div id=reader style=\"height:240px;width:600px;overflow:auto\"><div style=\"height:1800px\">预览正文</div></div>'");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.DocumentCount == 1, "网盘预览框架未按 URL 识别文档");
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"scrolled\"", "无文档标记的预览框架未滚动");
        await view.ExecuteScriptAsync("document.body.innerHTML = '<div style=\"height:2400px\">整页文档正文</div>'; window.scrollTo(0,0)");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(await study.CommandAsync(top, "advanceDocument", deadline.Token) == "\"scrolled\"", "真实网盘的整页滚动方式未处理");
        Require(await view.ExecuteScriptAsync("document.scrollingElement.scrollTop > 0 && document.scrollingElement.scrollHeight - document.scrollingElement.clientHeight - document.scrollingElement.scrollTop <= 2") == "true", "整页阅读器未到底");
        await view.ExecuteScriptAsync("document.body.innerHTML = '<div class=ans-doc><iframe src=\"about:blank#/screen/v2/fixture\"></iframe></div>'");
        Require(!(await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null).Snapshot.DocumentCount.Equals(1), "外层阅读器承载框架重复阻塞内层文档");
        Trace("document-done");
    }

    // 以原油猴脚本为基准核对 OCR/题干识别链路：题干清洗、选项文本节点 aria-label、
    // 私用区混淆字符的 OCR 回退，以及课程页面 / 学习页面的互斥判定与课程流程弹窗。
    private static async Task VerifyOcrScriptParityAsync(BrowserHost browser, WebView2 view)
    {
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        // ① 原脚本 _cleanQuestionText：HTML 实体（含双重编码）、题号、题型标签、分值角标。
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>3. 【单选题】&amp;quot;真实题干&amp;quot;（5.0分）</h3>
              <label><input type='radio' name='x'>甲</label><label><input type='radio' name='x'>乙</label></section>`;
            """);
        var cleaned = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().questions[0].text"))!;
        Require(cleaned.Contains("真实题干") && !cleaned.Contains("3.") && !cleaned.Contains("单选题") &&
            !cleaned.Contains("5.0分") && !cleaned.Contains("&"), "原脚本题干清洗（HTML 实体/题号/题型/分值）未对齐：" + cleaned);

        // ② 原脚本 _extractOptionPairs.pickText：选项文本节点自身的 aria-label 优先于混淆文字。
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3 class='font-cxsecret'>混淆题干</h3><ul class='Zy_ulTop'>
              <li role='radio'><a class='fl after' aria-label='A. 真实甲'>混淆甲</a></li>
              <li role='radio'><a class='fl after' aria-label='B. 真实乙'>混淆乙</a></li></ul></section>`;
            """);
        var options = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync(
            "JSON.stringify(window.__studyDesktop.snapshot().questions[0].options)"))!;
        using (var parsed = JsonDocument.Parse(options))
            Require(parsed.RootElement.EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "真实甲", "真实乙" }),
                "选项文本节点上的 aria-label 未被优先读取：" + options);

        // ③ 原脚本 DOM 提取失败时按实际字体渲染后 OCR：私用区字符无需 .font-cxsecret 也应成为 OCR 候选。
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>题干 &#xE000;&#xE001; 结束</h3>
              <label><input type='radio' name='x'>甲</label><label><input type='radio' name='x'>乙</label></section>`;
            """);
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().questions[0].issue") == "\"private-use\"",
            "私用区混淆题干未阻止提交");
        Require(await view.ExecuteScriptAsync("(window.__studyDesktop.ocrTasks?.()||[]).length") == "1",
            "私用区混淆题干未生成 OCR 回退任务");

        // ④ 页面类型互斥：学习页面（任务点标签）不能被当成课程页面（章节任务点目录）。
        await NavigateAndRead(browser, "https://study-desktop.local/course-study-fixture.html");
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.courseOverview().pageKind") == "\"study\"",
            "学习页面未被识别为 study");
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.courseOverview().taskTotal") == "0",
            "学习页面报告了课程任务点总数");
    }

    // 课程流程：以当前已打开的、带各个任务点的课程页面为起点（不导航/不重载）→ 全量检查任务点
    // → 全部完成直接弹窗；有未完成则点击第一个未完成任务点并启动自动流程 → 全部完成后弹窗，内容明确对应课程。
    private static async Task VerifyCourseWorkflowAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        var notices = new List<string>();
        window.CourseNoticeHandler = (title, message) => notices.Add($"{title}|{message}");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        try
        {
            // ① 课程列表（已登录界面）应识别为 catalog，并给出可点击的课程入口。
            await NavigateAndRead(browser, "https://study-desktop.local/course-list-fixture.html");
            var catalog = JsonSerializer.Deserialize<CourseOverview>(await view.ExecuteScriptAsync("window.__studyDesktop.courseOverview()"), web)!;
            Require(catalog.PageKind == "catalog" && catalog.HasCourseLink, "已登录课程列表未识别为课程入口页");
            Require(catalog.CourseLinks[0].Href.Contains("courseid=1001"), "课程入口地址未指向课程页面");

            // ①b 课程列表页不是流程起点：启动流程不得点击进入课程入口，也不得重载当前页面。
            await view.ExecuteScriptAsync("window.__noNavMarker = 'kept'");
            var catalogSource = view.CoreWebView2.Source;
            await window.StartCourseFlowForTestAsync();
            Require(view.CoreWebView2.Source == catalogSource, "课程列表页启动流程触发了页面导航");
            Require(await view.ExecuteScriptAsync("window.__noNavMarker") == "\"kept\"", "课程列表页启动流程重载了页面 DOM");
            Require(window.CourseFlowPhaseForTest == "Locating" && notices.Count == 0,
                "课程列表页被误判为课程页面或提前弹窗");
            window.StopCourseFlowForTest();

            // ② 课程页面：全部任务点（4 个）与其完成状态，且课程名/标识可用于弹窗。
            await NavigateAndRead(browser, "https://study-desktop.local/course-fixture.html?courseid=1001&clazzid=2002");
            await WaitForAsync(async () => await view.ExecuteScriptAsync(
                "document.querySelector('#lesson').contentDocument?.readyState") == "\"complete\"", TimeSpan.FromSeconds(10));
            var course = JsonSerializer.Deserialize<CourseOverview>(await view.ExecuteScriptAsync("window.__studyDesktop.courseOverview()"), web)!;
            Require(course.PageKind == "course" && course.IsCoursePage, "课程页面未识别为 course");
            Require(course.TaskTotal == 4 && course.TaskCompleted == 2 && course.FirstPendingIndex == 2,
                "课程全部任务点的完成状态统计不正确");
            Require(course.CourseName == "高等数学（上）" && course.CourseKey.Contains("1001"),
                $"课程名或课程标识缺失，弹窗无法明确对应课程：name={course.CourseName} key={course.CourseKey}");

            // ③ 当前已打开的课程页面就是流程起点：启动前后不得导航、不得重载。
            await view.ExecuteScriptAsync("window.taskClicks=0");
            await view.ExecuteScriptAsync("window.__noNavMarker = 'kept'");
            var courseSource = view.CoreWebView2.Source;
            await window.StartCourseFlowForTestAsync();
            Require(view.CoreWebView2.Source == courseSource, "课程流程启动触发了页面导航");
            Require(await view.ExecuteScriptAsync("window.__noNavMarker") == "\"kept\"", "课程流程启动重载了当前课程页面");
            Require(window.CourseFlowPhaseForTest == "Running", "存在未完成任务点时未进入自动流程");
            Require(await view.ExecuteScriptAsync("window.taskClicks") == "1", "未点击第一个未完成任务点或重复点击");
            Require(notices.Count == 0, "存在未完成任务点时提前弹出了完成提示");
            Require(window.CourseFlowLabelForTest.Contains("高等数学（上）"), "课程流程未锁定当前课程");

            // ④ 全部任务点完成后（连续确认）弹窗，内容明确对应课程。
            await view.ExecuteScriptAsync("window.markAllDone()");
            await window.CourseFlowTickForTestAsync();
            Require(notices.Count == 0, "目录未完成连续确认就提前弹出完成提示");
            await window.CourseFlowTickForTestAsync();
            Require(window.CourseFlowPhaseForTest == "Finished", "全部任务点完成后流程未结束");
            Require(notices.Count == 1 && notices[0].Contains("高等数学（上）") && notices[0].Contains("1001") && notices[0].Contains("4/4"),
                "自动流程完成弹窗未明确对应课程：" + string.Join(";", notices));

            // ⑤ 已全部完成的课程：直接弹窗提示，不点击任务点、不启动自动流程。
            notices.Clear();
            await view.ExecuteScriptAsync("window.taskClicks=0");
            await window.StartCourseFlowForTestAsync();
            Require(window.CourseFlowPhaseForTest == "Finished" && notices.Count == 1, "已完成的课程未直接弹窗提示");
            Require(notices[0].Contains("高等数学（上）") && notices[0].Contains("全部 4 个任务点已完成"),
                "课程已完成自动流程弹窗未明确对应课程：" + notices[0]);
            Require(await view.ExecuteScriptAsync("window.taskClicks") == "0", "已全部完成的课程仍点击了任务点");

            // ⑥ 恢复原状，避免影响后续回归断言。
            await view.ExecuteScriptAsync("window.resetTasks()");
            await window.StopStudyAsync();
            File.WriteAllText(Path.Combine(root, "course-workflow.json"), JsonSerializer.Serialize(new
            {
                Success = true, course.PageKind, course.TaskTotal, course.TaskCompleted, course.CourseName,
                Notices = notices.ToArray(), StudyPageIsolated = true, OcrParity = true
            }));
        }
        finally { window.CourseNoticeHandler = null; }
    }

    // 多账户 WebView 实例池：每个配置一个实例，后台隐藏启动并加载；
    // 切换只改显隐并复用实例，会话按 profile 隔离；主页面不导航、不重载、DOM 状态保留。
    private static async Task VerifyAccountWebViewPoolAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        // 前一步 ClearLogin 的导航可能仍在进行，先等待页面稳定再记录基线。
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        var core = view.CoreWebView2;
        var defaultProfilePath = core.Profile.ProfilePath;
        var before = core.Source;
        var navigations = 0;
        void OnNavigation(object? sender, CoreWebView2NavigationStartingEventArgs args) => navigations++;
        core.NavigationStarting += OnNavigation;
        try
        {
            await view.ExecuteScriptAsync("window.__silentSwitchMarker = 'kept'");
            Require(window.AccountViewCount == 1, "启动时应只有当前账户一个 WebView 实例");
            Require(ReferenceEquals(window.AccountViewFor(ProfileRegistry.DefaultId), view),
                "默认账户未复用 XAML 中的主 WebView");
            Require(window.IsAccountViewActive(ProfileRegistry.DefaultId), "默认账户实例未激活");

            var created = window.Profiles.Create("切换回归");
            WebView2? target = null;
            try
            {
                // ① 切换到新账户：实例池后台隐藏创建目标实例，隐藏当前、显示目标。
                await window.SwitchProfileForTestAsync(created.Id);
                target = window.AccountViewFor(created.Id);
                Require(target is not null && !ReferenceEquals(target, view), "新账户未建立独立 WebView 实例");
                Require(window.AccountViewCount == 2, "切换后实例数不符合一一映射");
                Require(window.AccountViewCount <= AccountViewPolicy.MaxInstances, "实例数超出资源上限");
                Require(window.ActiveAccountId == created.Id, "切换状态（当前配置 id）未回传");
                Require(window.IsAccountViewActive(created.Id) && !window.IsAccountViewActive(ProfileRegistry.DefaultId),
                    "切换未隐藏当前实例或未显示目标实例");
                Require(target!.Visibility == Visibility.Visible && view.Visibility == Visibility.Hidden,
                    "切换后的 WebView 显隐状态不正确");
                var targetCore = target!.CoreWebView2;
                Require(targetCore is not null, "后台实例的 CoreWebView2 未初始化");
                Require(targetCore!.Profile.ProfilePath != defaultProfilePath,
                    "两个账户共用同一浏览器 profile，会话未隔离");
                Require(window.AccountViewState(created.Id) is "Ready" or "Loading",
                    "后台 WebView 未进入加载/就绪状态");
                // 切换不得触发主 WebView 的导航、重载或 DOM 重建。
                Require(core.Source == before, $"切换配置触发了主页面导航（{before} → {core.Source}）");
                Require(navigations == 0, $"切换配置启动了页面重新加载（{navigations} 次）");
                Require(await view.ExecuteScriptAsync("window.__silentSwitchMarker") == "\"kept\"", "切换配置重新加载了页面 DOM");
                Require(window.Profiles.CurrentId == created.Id, "切换状态未回传到配置清单");
                Require(window.FindName("ProfileTabs") is ItemsControl { Items.Count: >= 2 }, "配置标签未刷新");
                Require(File.Exists(Path.Combine(window.Profiles.DirectoryOf(created.Id), "profile.json")),
                    "新配置的独立数据文件未保留");
                // 后台实例确已加载自己的页面并注入学习代理。
                Require(await WaitForAsync(async () =>
                    targetCore.Source.StartsWith(BrowserPolicy.LocalStartUrl, StringComparison.OrdinalIgnoreCase) &&
                    await target!.ExecuteScriptAsync("typeof window.__studyDesktop?.snapshot") == "\"function\"",
                    TimeSpan.FromSeconds(15)), "后台 WebView 未加载对应页面或未注入学习代理");

                // ② 边界场景：未知配置静默忽略，不导航、不改变显隐与状态。
                await window.SwitchProfileForTestAsync("p-does-not-exist");
                Require(core.Source == before && navigations == 0, "未知配置触发了导航或状态污染");
                Require(window.ActiveAccountId == created.Id && window.AccountViewCount == 2, "未知配置改变了实例映射");

                // ③ 切回默认：复用同一控件，实例数不变，页面与 DOM 保持原状。
                await window.SwitchProfileForTestAsync(ProfileRegistry.DefaultId);
                Require(ReferenceEquals(window.AccountViewFor(ProfileRegistry.DefaultId), view), "切回默认账户重建了 WebView");
                Require(window.AccountViewCount == 2, "切回默认账户后实例被重复创建或丢失");
                Require(window.IsAccountViewActive(ProfileRegistry.DefaultId) && !window.IsAccountViewActive(created.Id),
                    "切回默认账户后显隐状态不正确");
                Require(core.Source == before && navigations == 0, "切回默认配置触发了导航或重载");
                Require(await view.ExecuteScriptAsync("window.__silentSwitchMarker") == "\"kept\"", "切回默认配置重新加载了页面 DOM");
                Require(window.Profiles.CurrentId == ProfileRegistry.DefaultId, "切回后当前配置 id 不正确");

                // ④ 再次切到新账户：目标实例仍是同一个对象，页面状态不丢失。
                await window.SwitchProfileForTestAsync(created.Id);
                Require(ReferenceEquals(window.AccountViewFor(created.Id), target), "再次切换未复用已有 WebView 实例");
                Require(window.AccountViewCount == 2, "再次切换重复创建了实例");

                // ⑤ 未启动播放时，切换不得自动启动播放（按账户隔离）。
                Require(!window.PlayingForTest, "未启动播放的账户被错误地自动启动了播放");

                // ⑥ 播放开关属于会话状态：切出前正在播放时，切换后必须自动接续且不重载页面。
                window.SetPlayingForTest(true);
                await window.SwitchProfileForTestAsync(ProfileRegistry.DefaultId);
                Require(window.PlayingForTest, "切换配置后正在进行的自动播放被静默停掉");
                Require(core.Source == before && navigations == 0, "接续自动播放时触发了页面导航或重载");
                window.SetPlayingForTest(false);
            }
            finally
            {
                if (window.ActiveAccountId != ProfileRegistry.DefaultId)
                    await window.SwitchProfileForTestAsync(ProfileRegistry.DefaultId);
                // ⑤ 账户移除：先释放实例（保存 Cookie），再删除配置目录；实例池与清单保持同步。
                try { await window.ReleaseAccountViewAsync(created.Id); } catch (Exception) { }
                try { await window.SyncInstancesForTestAsync(); } catch (Exception) { }
                try { window.Profiles.Delete(created.Id); } catch (InvalidOperationException) { }
                Require(window.AccountViewCount == 1, "删除配置后 WebView 实例未释放");
                Require(window.IsAccountViewActive(ProfileRegistry.DefaultId), "删除配置后当前实例未保持激活");
            }
        }
        finally { core.NavigationStarting -= OnNavigation; }
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            try { if (await condition()) return true; }
            catch (Exception) { }
            await Task.Delay(250);
        }
        return false;
    }

    private static async Task NavigateAndRead(BrowserHost browser, string url)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded() => completion.TrySetResult();
        browser.PageLoaded += OnLoaded;
        try
        {
            browser.Navigate(url);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { browser.PageLoaded -= OnLoaded; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void WriteFailure(string root, string phase, Exception error)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, phase + ".json"), JsonSerializer.Serialize(new
        {
            Success = false, Error = error.ToString()
        }));
    }
}
