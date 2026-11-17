using System.IO;
using System.Windows;
using System.Windows.Controls;
using Desktop.App.Browser;
using Desktop.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

internal static partial class SmokeTest
{
    private static async Task VerifyCustomAudioAsync(MainWindow window, BrowserHost browser, WebView2 view)
    {
        await window.StopStudyAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        await NavigateAndRead(browser, "https://study-desktop.local/video-tabs-fixture.html");
        await view.ExecuteScriptAsync("""
            document.querySelector('video').remove();document.querySelector('.prev_ul').remove();
            const player=document.createElement('div');player.id='audio';player.className='video-js vjs-audio';
            player.innerHTML='<audio id="audio_html5_api" class="vjs-tech" style="display:none"></audio><button class="vjs-play-control">播放</button>';
            document.body.append(player);const media=player.querySelector('audio');media.src=sources[0];media.muted=true;
            window.playerCalls=0;
            window.videojs=()=>{throw new Error('不应重新初始化已有播放器')};
            window.videojs.getPlayer=id=>id==='audio'?{play:()=>{window.playerCalls++;return media.play()}}:null;
            const background=document.createElement('audio');background.style.display='none';background.src=sources[1];document.body.append(background);
            const inactive=player.cloneNode(true);inactive.id='inactive';inactive.hidden=true;document.body.append(inactive);
            """);
        var frames=await study.ReadAsync(deadline.Token);
        var top=frames.Single(f=>f.Frame is null);
        Require(top.Snapshot.AudioCount == 1, "自定义播放器的隐藏 audio 未识别，或后台/隐藏任务音频误计入");
        await study.ConfigureAsync(new AppSettings { AdvanceAtPercent=100, PlaybackRate=1 });
        await study.CommandAsync(top, "play", deadline.Token);
        while (await view.ExecuteScriptAsync("document.querySelector('#audio_html5_api').paused") != "false")
            await Task.Delay(20, deadline.Token);
        Require(await view.ExecuteScriptAsync("window.playerCalls") == "1", "未通过已有音频播放器启动，或重复请求播放");
        await study.CommandAsync(top, "stop", deadline.Token);
        Require(await view.ExecuteScriptAsync("document.querySelector('#audio_html5_api').paused") == "true", "停止未暂停隐藏的实际音频");
        await view.ExecuteScriptAsync("document.querySelector('#audio').style.display='none'");
        Require((await study.ReadAsync(deadline.Token)).Single(f=>f.Frame is null).Snapshot.AudioCount == 0,
            "播放器容器隐藏后仍识别为当前音频");
    }

    private static async Task VerifyAudioAsync(MainWindow window, BrowserHost browser, WebView2 view, string root)
    {
        await window.StopStudyAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var study = new StudyBrowser(view.CoreWebView2, browser);
        var slider = (Slider)window.FindName("AdvancePercentSlider");
        var auto = (CheckBox)window.FindName("AutoAdvanceBox");
        var previousPercent = slider.Value;
        var previousAuto = auto.IsChecked;
        try
        {
            await NavigateAndRead(browser, "https://study-desktop.local/video-tabs-fixture.html");
            await view.ExecuteScriptAsync("""
                const old=document.querySelector('video'), audio=document.createElement('audio');
                audio.id='media';audio.controls=true;audio.muted=true;old.replaceWith(audio);
                document.querySelectorAll('.prev_ul li')[0].textContent='1 音频';
                document.querySelectorAll('.prev_ul li')[1].textContent='2 Audio';
                selectVideo(1);window.videoOrder=[];window.tabClicks=0;
                const hidden=audio.cloneNode();hidden.style.display='none';document.body.append(hidden);
                """);
            var frames = await study.ReadAsync(deadline.Token);
            Require(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().audioCount") == "1", "音频未识别或把隐藏音频计入");
            slider.Value = 100;
            auto.IsChecked = false;
            ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!((Button)window.FindName("StartStudyButton")).IsEnabled)
                await Task.Delay(50, deadline.Token);
            Require(await view.ExecuteScriptAsync("JSON.stringify(window.videoOrder)") == "\"[0]\"", "开始时没有选择首个音频标签");
            await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 100, PlaybackRate = 1.5 });
            var top = frames.Single(f => f.Frame is null);
            while (await view.ExecuteScriptAsync("document.querySelector('#media').duration>0") != "true")
                await Task.Delay(50, deadline.Token);
            await study.CommandAsync(top, "play", deadline.Token);
            while (await view.ExecuteScriptAsync("document.querySelector('#media').paused") != "false")
                await Task.Delay(20, deadline.Token);
            Require(await view.ExecuteScriptAsync("document.querySelector('#media').playbackRate") == "1.5", "音频倍速未应用");
            await view.ExecuteScriptAsync("document.querySelector('#media').pause()");
            await study.CommandAsync(top, "play", deadline.Token);
            Require(await view.ExecuteScriptAsync("document.querySelector('#media').paused") == "true", "音频手动暂停后被轮询重播");
            await study.CommandAsync(top, "stop", deadline.Token);
            await study.CommandAsync(top, "play", deadline.Token);
            while (await view.ExecuteScriptAsync("document.querySelector('#media').ended") != "true")
                await Task.Delay(30, deadline.Token);
            Require(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().audioFinished") == "1", "100% 音频自然结束未识别");
            await study.CommandAsync(top, "play", deadline.Token);
            Require(await view.ExecuteScriptAsync("JSON.stringify(window.videoOrder)") == "\"[0,1]\"", "多音频没有顺序推进");
            await study.ConfigureAsync(new AppSettings { AdvanceAtPercent = 1, PlaybackRate = 1 });
            while (await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().audioFinished") != "1")
            { await study.CommandAsync(top, "play", deadline.Token); await Task.Delay(30, deadline.Token); }
            Require(await study.CommandAsync(top, "play", deadline.Token) == "\"ended\"", "音频阈值后重复切换");
            Require(await study.CommandAsync(top, "advanceTaskPoint", deadline.Token) == "\"clicked\"", "音频结束未进入测验");
            Require(await study.CommandAsync(top, "advanceTaskPoint", deadline.Token) == "\"none\"", "音频测验重复打开");
            await view.ExecuteScriptAsync("""
                document.querySelector('#media').src=sources[0];
                const video=document.createElement('video');video.controls=true;video.src=sources[1];document.body.append(video);
                """);
            await study.ReadAsync(deadline.Token);
            Require(await view.ExecuteScriptAsync("window.__studyDesktop.snapshot().videoCount===1&&window.__studyDesktop.snapshot().audioCount===1") == "true", "混合音视频计数错误");
            await study.CommandAsync(top, "stop", deadline.Token);
            Require(await view.ExecuteScriptAsync("[...document.querySelectorAll('video,audio')].every(m=>m.paused)") == "true", "停止没有暂停全部音视频");
            // 原生轮询必须能驱动另一框架中的音频，且仍以目录标记决定是否跨章。
            foreach (var completed in new[] { false, true })
            {
                await window.StopStudyAsync();
                await NavigateAndRead(browser, "https://study-desktop.local/task-fixture.html");
                await view.ExecuteScriptAsync("""
                    const media=document.querySelector('video');media.remove();
                    document.querySelector('.prev_ul').remove();
                    const frame=document.createElement('iframe');frame.src='/task-fixture.html';document.body.append(frame);
                    """);
                while (await view.ExecuteScriptAsync("!!document.querySelector('iframe')?.contentDocument?.querySelector('video')?.duration") != "true")
                    await Task.Delay(30, deadline.Token);
                await view.ExecuteScriptAsync("""
                    const doc=document.querySelector('iframe').contentDocument, old=doc.querySelector('video');
                    const audio=doc.createElement('audio');audio.controls=true;audio.muted=true;audio.src=old.src;old.replaceWith(audio);
                    doc.querySelector('#coursetree').remove();doc.querySelector('.prev_ul li').textContent='音频';
                    """);
                if (completed) await view.ExecuteScriptAsync("document.querySelector('.roundpoint').classList.add('done')");
                slider.Value = 1; auto.IsChecked = completed;
                ((Button)window.FindName("StartStudyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!((Button)window.FindName("StartStudyButton")).IsEnabled)
                    await Task.Delay(30, deadline.Token);
                var condition = completed ? "(window.chapterClicks||0)===1" : "(document.querySelector('iframe').contentWindow.tabClicks||0)===2";
                while (await view.ExecuteScriptAsync(condition) != "true")
                { await window.RefreshStudyAsync(); await Task.Delay(30, deadline.Token); }
                Require(((TextBlock)window.FindName("CourseTypeText")).Text.Contains("音频"), "原生界面没有识别音频课程");
                Require(await view.ExecuteScriptAsync(completed ? "document.querySelector('iframe').contentWindow.tabClicks===1" : "(window.chapterClicks||0)===0") == "true",
                    "音频未沿用章节完成标记和自动切章开关");
            }
            File.AppendAllText(Path.Combine(root, "study-trace.txt"), "audio-done\n");
        }
        finally { await window.StopStudyAsync(); slider.Value = previousPercent; auto.IsChecked = previousAuto; }
    }
}
