using System.Text.Json;
using System.IO;
using Desktop.App.Browser;
using Desktop.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

internal static partial class SmokeTest
{
    private static async Task VerifyConfigurationFailureDiagnosticAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        var start = (System.Windows.Controls.Button)window.FindName("StartStudyButton");
        start.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!start.IsEnabled) await Task.Delay(20, deadline.Token);
        await view.ExecuteScriptAsync("document.body.innerHTML=`<section class='questionLi'><h3>配置诊断</h3><label><input type='radio' name='config'>甲</label></section><button onclick='window.configSubmits++'>提交答案</button>`;window.configSubmits=0");
        using var handler = new AnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var path = Path.Combine(root, "incomplete-local", ".env");
        await window.RefreshStudyAsync(new AiConfiguration { Provider = "fixture", ApiKey = "config-diagnostic-fake-key", ConfigPath = path }, new AiClient(http));
        var logs = ((System.Windows.Controls.ListBox)window.FindName("LogList")).Items.OfType<LogEntry>().Select(entry => entry.Message).ToArray();
        Require(logs.Any(message => message.Contains("CX_AI_MODEL") && message.Contains("CX_AI_ENDPOINT") && message.Contains(path)), "配置失败仍只显示异常类型，没有缺失字段和实际配置路径");
        Require(logs.All(message => !message.Contains("config-diagnostic-fake-key")), "配置错误日志泄漏密钥");
        await window.RefreshStudyAsync(new AiConfiguration { ApiKey = "config-diagnostic-fake-key" }, new AiClient(http));
        Require(handler.Calls == 0 && await view.ExecuteScriptAsync("window.configSubmits") == "0", "配置无效时仍请求模型或提交题目");
        await window.StopStudyAsync();
    }

    private static async Task VerifyReadOnlyReviewAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='singleQuesId'><h3>单选题：已批阅题目</h3>
              <ul class='Zy_ulTop'><li role='radio' aria-checked='false' onclick='window.reviewClicks++'>甲</li></ul>
              <div class='newAnswerBx'><div class='myAnswerBx'><div class='myAnswer'>我的答案：甲</div></div></div></section>
              <button onclick='window.reviewSubmits++'>提交答案</button>`;
            window.reviewClicks=0;window.reviewSubmits=0;
            """);
        var editable = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        await view.ExecuteScriptAsync("history.replaceState(null,'','/mooc-ans/work/selectWorkQuestionYiPiYue?submit=true')");
        var review = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(review.Snapshot.Questions.Length == 0 && review.Snapshot.QuestionTotal == 0, "已批阅结果页仍被识别为待答题目");
        Require(!await study.FillAsync(editable, editable.Snapshot.Questions.Single(), new AiAnswer([0], []), CancellationToken.None), "批阅结果页仍允许使用旧快照填写");
        using var handler = new AnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var config = new AiConfiguration { Provider = "fixture", Model = "fixture", ApiKey = "fake-only", Endpoint = "https://example.test/chat/completions" };
        await window.RefreshStudyAsync(config, new AiClient(http));
        Require(handler.Calls == 0 && await view.ExecuteScriptAsync("window.reviewClicks===0 && window.reviewSubmits===0") == "true", "批阅结果页重复请求模型、填写或提交");
        await view.ExecuteScriptAsync("history.replaceState(null,'','/start.html');document.title='查看已批阅作业'");
        review = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(review.Snapshot.Questions.Length == 0, "批阅页标题及结果标记回退未生效");
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
    }

    private sealed class IncompleteAnswerHandler : System.Net.Http.HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var content = ++Calls == 1 ? "{\"choices\":[],\"blanks\":[]}" : "{\"choices\":[0],\"blanks\":[]}";
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })) });
        }
    }

    private static async Task VerifyIncompleteAnswerIsolationAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        var start = (System.Windows.Controls.Button)window.FindName("StartStudyButton");
        start.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!start.IsEnabled) await Task.Delay(20, deadline.Token);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>单选题：模型无法确定</h3><label><input type='radio' name='uncertain'>甲</label></section>
              <section class='questionLi'><h3>单选题：可以确定</h3><label><input type='radio' name='certain'>乙</label></section>
              <button onclick='window.isolationSubmits++'>提交答案</button>`;window.isolationSubmits=0;
            """);
        using var handler = new IncompleteAnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var config = new AiConfiguration { Provider = "fixture", Model = "fixture", ApiKey = "fake-only", Endpoint = "https://example.test/chat/completions" };
        await window.RefreshStudyAsync(config, new AiClient(http));
        Require(handler.Calls == 2 && await view.ExecuteScriptAsync("document.querySelector('[name=certain]').checked") == "true", $"模型无法确定一题时中断了后续题目（请求次数：{handler.Calls}）");
        Require(await view.ExecuteScriptAsync("!document.querySelector('[name=uncertain]').checked && window.isolationSubmits===0") == "true", "模型无法确定时仍填写或提交漏答试卷");
        var failureLogs = ((System.Windows.Controls.ListBox)window.FindName("LogList")).Items.OfType<LogEntry>().Select(entry => entry.Message);
        Require(failureLogs.Any(message => message.Contains("第 1/2 题") && message.Contains("空数组")), "模型答案失败日志没有指出题号和具体原因");
        await window.RefreshStudyAsync(config, new AiClient(http));
        Require(handler.Calls == 2, "不完整答案没有等待重试间隔");
    }

    private static async Task VerifyAdditionalScriptParityAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        // 真实章测的字母标签与选项正文位于同一个 li，不能把 label.fl 再算作一个选项。
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='singleQuesId'><h3>单选题：选择乙</h3><ul class='Zy_ulTop'>
              <li role='radio' aria-checked='false' onclick="document.querySelectorAll('li').forEach(n=>n.setAttribute('aria-checked','false'));this.setAttribute('aria-checked','true');window.rowClicks=(window.rowClicks||0)+1"><label class='fl before'><span>A</span></label><a class='fl after'>甲</a></li>
              <li role='radio' aria-checked='false' onclick="document.querySelectorAll('li').forEach(n=>n.setAttribute('aria-checked','false'));this.setAttribute('aria-checked','true');window.rowClicks=(window.rowClicks||0)+1"><label class='fl before'><span>B</span></label><a class='fl after'>乙</a></li>
              </ul></section>`;
            """);
        var nestedOptions = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        var nestedQuestion = nestedOptions.Snapshot.Questions.Single();
        Require(nestedQuestion.Options.SequenceEqual(new[] { "甲", "乙" }), "真实章测的嵌套字母标签导致选项重复或索引错位");
        Require(await study.FillAsync(nestedOptions, nestedQuestion, new AiAnswer([1], []), CancellationToken.None), "真实章测选项点击或读回失败");
        Require(await study.FillAsync(nestedOptions, nestedQuestion, new AiAnswer([1], []), CancellationToken.None), "真实章测重复读回失败");
        Require(await view.ExecuteScriptAsync("window.rowClicks") == "1", "真实章测选项被重复点击");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3 aria-label='单选题：真实题干'><span class='font-cxsecret'>混淆</span></h3><label aria-label='A. 真实甲'><input type='radio' name='x'><span class='font-cxsecret'>混淆甲</span></label><label><input type='radio' name='x' aria-label='B. 真实乙'><span class='font-cxsecret'>混淆乙</span></label></section>`;
            """);
        var top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "single" && top.Snapshot.Questions[0].Text.Contains("真实题干") && top.Snapshot.Questions[0].Options.SequenceEqual(new[] { "真实甲", "真实乙" }), "原脚本题干或选项父容器/input 上的 aria-label 未使用");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><p>附注</p><h3><div class='clearfix'>单选题：真实题干</div></h3><label><input type='radio' name='x'>甲</label></section>`;
            """);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Text.Contains("真实题干") && !top.Snapshot.Questions[0].Text.Contains("附注"), "题干选择器未按原脚本优先级取值");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<h3 class='mark_name'>多选题：选择全部</h3><section class='singleQuesId'><label><input type='radio' name='x'>甲</label><label><input type='radio' name='x'>乙</label></section>`;
            """);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "multiple" && top.Snapshot.Questions[0].Text.Contains("选择全部"), "邻接 mark_name 题干或题型未读取");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='completionQuesId'><h3>填空题</h3><input type='text'><button class='save_btn' onclick='window.localSaves=(window.localSaves||0)+1'>保存答案</button></section>`;
            """);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        var q = top.Snapshot.Questions.Single(); var answer = new AiAnswer([], ["内容"]);
        Require(await study.FillAsync(top, q, answer, CancellationToken.None), "逐题保存前填写失败");
        Require(await study.AdvanceQuestionPageAsync(top, [(q, answer)], CancellationToken.None) == "saved", "逐题保存未执行");
        Require(await study.AdvanceQuestionPageAsync(top, [(q, answer)], CancellationToken.None) == "none", "相同答案重复保存");
        Require(await view.ExecuteScriptAsync("window.localSaves") == "1", "逐题保存次数不正确");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<main class='questionItem'><section class='questionLi' id='local-a'><h3>单选甲</h3><label><input type='radio' name='x'>甲</label><button onclick='window.localA=(window.localA||0)+1'>提交答案</button></section><section class='questionLi' id='local-b'><h3>单选乙</h3><label><input type='radio' name='y'>乙</label><button onclick='window.localB=(window.localB||0)+1'>提交答案</button></section><button onclick='window.finalCount=(window.finalCount||0)+1'>提交作业</button></main>`;
            """);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        foreach (var question in top.Snapshot.Questions) Require(await study.FillAsync(top, question, new AiAnswer([0], []), CancellationToken.None), "逐题提交前填写失败");
        Require(await view.ExecuteScriptAsync("""
            (()=>{const a=window.__studyDesktop,s=a.snapshot(),e=s.questions.map(question=>({question,answer:{choices:[0],blanks:[]}}));
            a.submitAnswers(s.documentId,e,true);a.submitAnswers(s.documentId,e,true);a.submitAnswers(s.documentId,e,true);
            return window.localA===1 && window.localB===1 && !window.finalCount})()
            """) == "true", "逐题提交有遗漏、重复或提前交卷");
        Require(await view.ExecuteScriptAsync("(()=>{const a=window.__studyDesktop,s=a.snapshot();a.submitAnswers(s.documentId,s.questions.map(question=>({question,answer:{choices:[0],blanks:[]}})));return window.localA===1&&window.localB===1&&window.finalCount===1})()") == "true", "逐题提交后没有统一交卷");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>单选题</h3><label><input type='radio' name='x'>甲</label><button onclick="window.blockedClicks=(window.blockedClicks||0)+1;const d=document.createElement('div');d.className='popDiv';d.textContent='还有未作答的题目，是否提交？';document.body.append(d)">提交答案</button></section>`;
            """);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        q = top.Snapshot.Questions.Single(); answer = new AiAnswer([0], []);
        Require(await study.FillAsync(top, q, answer, CancellationToken.None), "阻断测试填写失败");
        Require(!await study.SubmitAnswersAsync(top, [(q, answer)], CancellationToken.None, localOnly: true), "未答题提示仍确认逐题提交");
        Require(await study.AdvanceQuestionPageAsync(top, [(q, answer)], CancellationToken.None) == "blocked", "逐题提交被网页拒绝后仍推进");
        Require(!await study.SubmitAnswersAsync(top, [(q, answer)], CancellationToken.None, localOnly: true), "网页拒绝后仍重复提交");
        Require(await view.ExecuteScriptAsync("window.blockedClicks") == "1", "被拒绝的逐题提交重复点击");
    }

    private static async Task VerifyFullPaperAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>第一页</h3><label><input type='radio' name='x'>甲</label></section><button>下一题</button><button id='full'>整卷浏览</button>`;
            document.querySelector('#full').onclick=()=>setTimeout(()=>{document.body.innerHTML=`<section class='questionLi'><h3>整卷第一题</h3><label><input type='radio' name='x'>甲</label></section><section class='questionLi'><h3>整卷第二题</h3><label><input type='radio' name='y'>乙</label></section><div class='submit_btn' onclick='window.fullSubmit=(window.fullSubmit||0)+1'>提交试卷</div>`},100);
            """);
        await study.ReadAsync(CancellationToken.None);
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.prepareQuestionView?.()") == "\"clicked\"", "未按原脚本切换整卷浏览");
        var top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.QuestionPagePending, "切换整卷时未等待正文加载");
        await Task.Delay(130);
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(!top.Snapshot.QuestionPagePending && top.Snapshot.Questions.Length == 2, "整卷正文未完整读取");
        foreach (var q in top.Snapshot.Questions) Require(await study.FillAsync(top, q, new AiAnswer([0], []), CancellationToken.None), "整卷题目未填写");
        Require(await view.ExecuteScriptAsync("window.__studyDesktop.submitAnswers(window.__studyDesktop.snapshot().documentId,window.__studyDesktop.snapshot().questions.map(question=>({question,answer:{choices:[0],blanks:[]}})))") == "\"clicked\"", "原脚本自定义提交控件未点击");
        Require(await view.ExecuteScriptAsync("window.fullSubmit") == "1", "整卷提交次数不正确");
    }

    private static async Task VerifyOcrEngineAsync(BrowserHost browser, WebView2 view, string root)
    {
        var executable = OcrClient.ResolveExecutable(Path.Combine(root, ".env"));
        if (!System.IO.File.Exists(executable)) throw new InvalidOperationException("OCR 实机验收需要已安装的 Tesseract。");
        var client = new OcrClient(executable, Path.Combine(root, "ocr-work"));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>单选题：<img id='stem'></h3><label><input type='radio' name='x'><img id='answer'></label><label><input type='radio' name='x'>Five</label></section>`;
            const make=text=>{const c=document.createElement('canvas');c.width=1100;c.height=110;const x=c.getContext('2d');x.fillStyle='white';x.fillRect(0,0,c.width,c.height);x.fillStyle='black';x.font='48px Microsoft YaHei';x.fillText(text,20,75);return c.toDataURL()};
            document.querySelector('#stem').src=make('What is two plus two?');document.querySelector('#answer').src=make('Four');
            window.chineseOcrImage=make('学习助手 你好世界');
            """);
        await Task.Delay(100);
        await study.PrepareOcrAsync(client, CancellationToken.None);
        var top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "single" && top.Snapshot.Questions[0].Text.Contains("two") && top.Snapshot.Questions[0].Options[0].Contains("Four"), "真实 OCR 题干/选项识别失败：" + study.OcrDiagnostic);
        Require(await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([0], []), CancellationToken.None), "真实图片题未正确填写");
        var data = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("window.chineseOcrImage"))!;
        var chinese = await client.RecognizeAsync(Convert.FromBase64String(data[22..]), CancellationToken.None);
        Require(chinese.Usable && chinese.Text.Replace(" ", "").Contains("学习助手"), "真实中文图片 OCR 失败");
        Require(!System.IO.Directory.EnumerateFileSystemEntries(Path.Combine(root, "ocr-work")).Any(), "OCR 临时图片未清理");
        System.IO.File.WriteAllText(Path.Combine(root, "ocr-check.json"), JsonSerializer.Serialize(new { Success = true, Engine = System.IO.Path.GetFileName(executable), chinese.Confidence, ImageQuestion = true, ImageOption = true, Chinese = true, TemporaryImagesRemoved = true }));
    }

    private static async Task VerifyOcrBindingAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>单选题<img id='stem'></h3><label><input type='radio' name='x'><img id='option'></label><label><input type='radio' name='x'>乙</label></section>`;
            const canvas=document.createElement('canvas');canvas.width=500;canvas.height=90;
            const ctx=canvas.getContext('2d');ctx.fillStyle='white';ctx.fillRect(0,0,500,90);ctx.fillStyle='black';ctx.font='40px Arial';ctx.fillText('Hello world',20,60);
            document.querySelectorAll('img').forEach(img=>img.src=canvas.toDataURL());
            """);
        await Task.Delay(100);
        var top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Issue == "image", "OCR 前图片题未阻止提交");
        Require(await view.ExecuteScriptAsync("(window.__studyDesktop.ocrTasks?.()||[]).length") == "2", "图片题干或图片选项未分别生成 OCR 任务");
        Require(await view.ExecuteScriptAsync("""
            (()=>{const api=window.__studyDesktop,tasks=api.ocrTasks(),doc=api.snapshot().documentId;
            return !api.applyOcr('stale',tasks[0].id,tasks[0].source,'题干') &&
              api.applyOcr(doc,tasks[0].id,tasks[0].source,'题干') && api.applyOcr(doc,tasks[1].id,tasks[1].source,'甲')})()
            """) == "true", "OCR 文档校验或结果绑定失败");
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "single" && top.Snapshot.Questions[0].Text.Contains("题干") && top.Snapshot.Questions[0].Options.SequenceEqual(new[] { "甲", "乙" }), "OCR 结果未按题干和选项位置读取");
        Require(await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([0], []), CancellationToken.None), "图片选项未正确填写并读回");
        await view.ExecuteScriptAsync("document.querySelector('#stem').src+='changed'");
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Issue == "image", "图片变化仍复用旧 OCR 文本");
        await view.ExecuteScriptAsync("document.body.innerHTML=\"<section class='questionLi'><h3 class='font-cxsecret'>字体未映射</h3><label><input type='radio' name='x'>甲</label><label><input type='radio' name='x'>乙</label></section>\"");
        await study.ReadAsync(CancellationToken.None);
        Require(await view.ExecuteScriptAsync("(()=>{const a=window.__studyDesktop,t=a.ocrTasks();return t.length===1 && t[0].data.startsWith('data:image/png;base64,') && a.applyOcr(a.snapshot().documentId,t[0].id,t[0].source,'单选题：真实题干')})()") == "true", "字体 OCR 回退未生成实际字形图像");
        top = (await study.ReadAsync(CancellationToken.None)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "single" && top.Snapshot.Questions[0].Text.Contains("真实题干"), "字体 OCR 回退仍被错误阻塞");
    }

    private static async Task VerifyQuestionParityAsync(BrowserHost browser, WebView2 view)
    {
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        const string nested = "<div class='questionItem'><section class='questionLi' id='q-a'><div class='TiMu'><h3>相同题干</h3><label><input type='radio' name='a'>相同选项</label><label><input type='radio' name='a'>相同选项</label></div></section><section class='questionLi' id='q-b'><h3>相同题干</h3><label><input type='radio' name='b'>甲</label><label><input type='radio' name='b'>乙</label></section></div>";
        await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(nested)}");
        var top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Length == 2, "嵌套题目容器把两题合并或重复统计");
        Require(top.Snapshot.Questions[0].Options.Length == 2, "相同选项文字被错误合并");
        var firstId = top.Snapshot.Questions[0].Id;
        await view.ExecuteScriptAsync("document.querySelector('.questionItem').append(document.querySelector('#q-a'))");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions[1].Id == firstId, "题目排序变化后身份改变，导致重复请求模型");

        foreach (var layout in new[] { "examQ", "examQuestion", "subject-item", "exam-paper-question", "question-item", "quesItem", "quesBody", "mark_table" })
        {
            var html = $"<section class='{layout}'><h3>单选题：一加一</h3><label><input type='radio' name='x'>一</label><label><input type='radio' name='x'>二</label></section>";
            await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(html)}");
            top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
            Require(top.Snapshot.Questions.Length == 1 && top.Snapshot.Questions[0].Kind == "single", $"原脚本题目布局 {layout} 漏读");
            Require(await study.FillAsync(top, top.Snapshot.Questions[0], new AiAnswer([1], []), deadline.Token), $"{layout} 未正确填写");
        }

        foreach (var option in new[] {
            "<div class='textDIV test-option'>甲</div><div class='textDIV test-option'>乙</div>",
            "<div class='eidtDiv test-option'>甲</div><div class='eidtDiv test-option'>乙</div>",
            "<label class='fl test-option'>甲</label><label class='fl test-option'>乙</label>",
            "<div class='answerBg'><label class='test-option'>甲</label><label class='test-option'>乙</label></div>",
            "<ul class='answerList'><li class='test-option'>甲</li><li class='test-option'>乙</li></ul>",
            "<ul class='Zy_ulBottom'><li class='test-option'>甲</li><li class='test-option'>乙</li></ul>",
            "<ul><li class='test-option'><span class='after'>甲</span></li><li class='test-option'><span class='after'>乙</span></li></ul>"
        })
        {
            var html = "<section class='questionLi'><h3>单选题：选择乙</h3>" + option + "</section>";
            await view.ExecuteScriptAsync($"document.body.innerHTML={JsonSerializer.Serialize(html)};window.optionClicks=0;document.querySelectorAll('.test-option').forEach(el=>el.onclick=()=>{{window.optionClicks++;document.querySelectorAll('.test-option').forEach(n=>n.classList.remove('cur'));el.classList.add('cur')}})");
            top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
            var question = top.Snapshot.Questions.Single();
            Require(question.Options.SequenceEqual(new[] { "甲", "乙" }), "原脚本自定义选项顺序、数量或文字不一致");
            Require(await study.FillAsync(top, question, new AiAnswer([1], []), deadline.Token), "自定义选项点击或 cur 状态读回失败");
            Require(await study.FillAsync(top, question, new AiAnswer([1], []), deadline.Token), "重复核验选项失败");
            Require(await view.ExecuteScriptAsync("window.optionClicks") == "1", "相同答案重复点击导致反选");
        }

        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<h2 class='type_tit'>二、多选题</h2><section class='singleQuesId'><h3>请选择全部</h3><div role='radio' onclick="this.classList.toggle('checked')">甲</div><div role='radio' onclick="this.classList.toggle('checked')">乙</div></section>`;
            """);
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "multiple", "分组标题的多选题被 singleQuesId 或 role 误判");
        Require(await study.FillAsync(top, top.Snapshot.Questions.Single(), new AiAnswer([0, 1], []), deadline.Token), "分组多选未填全");
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<section class='questionLi'><h3>单选题：选择乙</h3><label hidden><input type='radio' name='x'>模板</label><label><input style='display:none' type='radio' name='x'>甲</label><label><input style='display:none' type='radio' name='x'>乙</label></section>`;
            """);
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Options.SequenceEqual(new[] { "甲", "乙" }), "隐藏模板使可见选项索引错位");
        Require(await study.FillAsync(top, top.Snapshot.Questions.Single(), new AiAnswer([1], []), deadline.Token), "CSS 隐藏原生 input 的可见选项未填写");

        await view.ExecuteScriptAsync("document.body.innerHTML=\"<section class='completionQuesId'><h3>填空</h3><input type='text'></section>\";window.blankChanges=0;document.querySelector('input').onchange=()=>window.blankChanges++");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(await study.FillAsync(top, top.Snapshot.Questions.Single(), new AiAnswer([], ["答案"]), deadline.Token), "普通填空未填写");
        Require(await study.FillAsync(top, top.Snapshot.Questions.Single(), new AiAnswer([], ["答案"]), deadline.Token), "普通填空重复核验失败");
        Require(await view.ExecuteScriptAsync("window.blankChanges") == "1", "普通填空重复写入并重复触发事件");

        await view.ExecuteScriptAsync("document.body.innerHTML=Array.from({length:101},(_,i)=>`<section class='questionLi' id='bulk-${i}'><h3>第${i}题</h3><label><input type='radio' name='b${i}'>甲</label><label><input type='radio' name='b${i}'>乙</label></section>`).join('')");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Length == 101, "超过100题时静默遗漏尾部题目");
        await view.ExecuteScriptAsync("document.body.innerHTML=Array.from({length:1001},(_,i)=>`<section class='questionLi' id='limit-${i}'><h3>第${i}题</h3><label><input type='radio' name='l${i}'>甲</label><label><input type='radio' name='l${i}'>乙</label></section>`).join('')");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.QuestionOverflow && top.Snapshot.QuestionTotal == 1001, "题量超限没有明确记录，可能静默漏题");
        Require(top.Snapshot.Questions.Length == 0, "超限试卷仍展开前1000题，可能耗尽读取超时");
        await view.ExecuteScriptAsync("document.body.innerHTML=\"<section class='questionLi'><h3 class='font-cxsecret' aria-label='单选题：可访问的完整题干'>一</h3><label><input type='radio' name='aria'>甲</label><label><input type='radio' name='aria'>乙</label></section>\"");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(top.Snapshot.Questions.Single().Kind == "single" && top.Snapshot.Questions.Single().Text.Contains("完整题干"), "原脚本可访问文本路径遗漏，错误等待字体解析");
        await view.ExecuteScriptAsync("document.body.innerHTML=\"<section class='questionLi'><h3>单选题：乙</h3><ul><li><a class='after' onclick='this.parentElement.classList.add(&quot;cur&quot;)'>甲</a></li><li><a class='after' onclick='this.parentElement.classList.add(&quot;cur&quot;)'>乙</a></li></ul></section>\"");
        top = (await study.ReadAsync(deadline.Token)).Single(f => f.Frame is null);
        Require(await study.FillAsync(top, top.Snapshot.Questions.Single(), new AiAnswer([1], []), deadline.Token), "选项事件只绑定内层控件时没有触发");
    }

    private static async Task VerifyAnswerCacheParityAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML='<div id="questions"></div>';
            window.cacheClicks=0;
            window.renderCachedQuestion=()=>document.querySelector('#questions').innerHTML=`<section class='questionLi' id='cached-q'><h3>缓存题目</h3><label><input type='radio' name='cache' onchange='window.cacheClicks++'>甲</label><label><input type='radio' name='cache' onchange='window.cacheClicks++'>乙</label></section>`;
            window.renderCachedQuestion();
            """);
        using var handler = new AnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var config = new AiConfiguration { Provider = "fixture", Model = "fixture", ApiKey = "fake-only", Endpoint = "https://example.test/chat/completions" };
        var client = new AiClient(http);
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1 && await view.ExecuteScriptAsync("document.querySelector('input').checked") == "true", "缓存测试首次答题失败");
        await view.ExecuteScriptAsync("window.renderCachedQuestion()");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1 && await view.ExecuteScriptAsync("document.querySelector('input').checked") == "true", "页面重绘后没有恢复缓存答案，或重复请求模型");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1 && await view.ExecuteScriptAsync("window.cacheClicks") == "2", "已核验答案仍重复请求或重复点击");
    }

    private static async Task VerifyQuestionPaginationAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        await NavigateAndRead(browser, BrowserPolicy.LocalStartUrl);
        await view.ExecuteScriptAsync("""
            document.body.innerHTML=`<div class='exam-sidebar'><button type='button' onclick='showQuestion(1)'>1</button><button type='button' onclick='showQuestion(2)'>2</button><button type='button' onclick='showQuestion(3)'>3</button></div><div id='question-area'></div><button type='button' onclick="if(confirm('确认提交？'))window.paperSubmits++">交卷</button>`;
            window.paperSubmits=0;window.pageVisits=[];window.savedChoices={};
            window.renderPaperQuestion=number=>{
                document.querySelector('#question-area').innerHTML=`<section class='examQuestion' data-question-id='paper-${number}'><h3>相同题干的不同题目</h3><label><input type='radio' name='paper' onchange='window.savedChoices[${number}]=0'>甲</label><label><input type='radio' name='paper' onchange='window.savedChoices[${number}]=1'>乙</label></section>`;
            };
            window.showQuestion=number=>{
                window.pageVisits.push(number);window.currentPage=number;
                document.querySelectorAll('.exam-sidebar button').forEach((button,i)=>button.classList.toggle('active',i+1===number));
                if(!document.querySelector('.examQuestion'))window.renderPaperQuestion(number);
                else setTimeout(()=>window.renderPaperQuestion(number),100);
            };
            window.showQuestion(2);
            """);
        using var handler = new AnswerHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var config = new AiConfiguration { Provider = "fixture", Model = "fixture", ApiKey = "fake-only", Endpoint = "https://example.test/chat/completions" };
        var client = new AiClient(http);
        await window.RefreshStudyAsync(config, client);
        Require(await view.ExecuteScriptAsync("window.paperSubmits") == "0", "分页考试只填写当前页就提前交卷");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 1, "题号已切换但旧题内容尚未更新时重复请求模型");
        for (var i = 0; i < 5 && await view.ExecuteScriptAsync("window.paperSubmits") == "0"; i++)
        {
            await Task.Delay(130);
            await window.RefreshStudyAsync(config, client);
        }
        Require(handler.Calls == 3, "分页同题干误合并、漏题或重复调用模型");
        Require(await view.ExecuteScriptAsync("Object.keys(window.savedChoices).sort().join(',')") == "\"1,2,3\"", "从中间页开始时遗漏前面或后面题目");
        Require(await view.ExecuteScriptAsync("window.paperSubmits") == "1", "全部分页核验后未提交或重复提交");
        await window.RefreshStudyAsync(config, client);
        Require(handler.Calls == 3 && await view.ExecuteScriptAsync("window.paperSubmits") == "1", "分页完成后再次答题或交卷");
    }
}
