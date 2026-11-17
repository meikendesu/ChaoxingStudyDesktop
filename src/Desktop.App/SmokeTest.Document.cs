using System.IO;
using System.Windows;
using System.Windows.Controls;
using Desktop.App.Browser;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

internal static partial class SmokeTest
{
    private static async Task VerifyRedirectedDocumentAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        await window.StopStudyAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        var auto = (CheckBox)window.FindName("AutoAdvanceBox");
        var previousAuto = auto.IsChecked;
        try
        {
            await NavigateAndRead(browser, "https://study-desktop.local/doc-fixture.html");
            await view.ExecuteScriptAsync("""
                history.replaceState(null,'','/ananas/modules/pdf/index.html');
                // 保留真实入口特征；离线映射用片段加载本地子页面，不访问平台。
                document.body.innerHTML='<iframe id="panView" src="/doc-fixture.html#/mooc-ans/screen/file" style="width:650px;height:300px"></iframe>';
                """);
            while (await view.ExecuteScriptAsync("!!document.querySelector('iframe')?.contentDocument?.querySelector('.ans-ppt')") != "true")
                await Task.Delay(30, deadline.Token);
            await view.ExecuteScriptAsync("""
                const doc=document.querySelector('iframe').contentDocument;
                doc.body.innerHTML='<div class="document-reader" id="reader" style="height:220px;width:600px;overflow:auto"><div style="height:2000px">已加载正文</div></div>';
                doc.defaultView.readerScrolls=0;doc.querySelector('#reader').addEventListener('scroll',()=>doc.defaultView.readerScrolls++);
                """);
            var frames = await study.ReadAsync(deadline.Token);
            Require(frames.Single(f=>f.Frame is null).Snapshot.DocumentCount == 0,
                "网盘跳转入口的外层被当作文档，阻塞内层正文");
            Require(frames.Where(f=>f.Frame is not null).Sum(f=>f.Snapshot.DocumentCount) == 1, "内层正文未识别");
            auto.IsChecked=false;
            ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!((Button)window.FindName("StartStudyButton")).IsEnabled)
                await Task.Delay(30, deadline.Token);
            while (!(await study.ReadAsync(deadline.Token)).Where(f=>f.Frame is not null).Any(f=>f.Snapshot.DocumentComplete))
            { await window.RefreshStudyAsync(); await Task.Delay(50, deadline.Token); }
            await window.RefreshStudyAsync();
            Require(await view.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.querySelector('#reader').scrollTop>1500&&document.querySelector('iframe').contentWindow.readerScrolls>0") == "true",
                "原生流程未驱动内层正文滚动");
            Require(((TextBlock)window.FindName("StudyStatusText")).Text != "等待文档加载", "正文完成后仍被外层等待阻塞");
            // 隐藏或未指定地址的占位 iframe 不能掩盖外层自己的正文。
            await view.ExecuteScriptAsync("document.querySelector('iframe').hidden=true");
            Require((await study.ReadAsync(deadline.Token)).Single(f=>f.Frame is null).Snapshot.DocumentCount == 1,
                "隐藏阅读器导致外层文档被错误忽略");
            await view.ExecuteScriptAsync("document.querySelector('iframe').hidden=false;document.querySelector('iframe').removeAttribute('src')");
            Require((await study.ReadAsync(deadline.Token)).Single(f=>f.Frame is null).Snapshot.DocumentCount == 1,
                "空地址阅读器导致外层文档被错误忽略");
            File.AppendAllText(Path.Combine(root,"study-trace.txt"),"redirected-document-done\n");
        }
        finally { await window.StopStudyAsync(); auto.IsChecked=previousAuto; }
    }
}
