using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Desktop.App.Browser;
using Desktop.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Desktop.App;

// 离线性能基线：固定迭代次数、固定本地页面、固定运行时缓存场景。
// 只读取当前进程与 WebView2 状态，不写 Cookie、不清除登录资料。
internal static class PerfTest
{
    private const int NavigationSamples = 5;
    private const int ReadSamples = 30;
    private const int InteractionSamples = 20;
    private const int RuntimeSamples = 5;

    public static async Task RunAsync(MainWindow window, BrowserHost browser, WebView2 view, string root, string phase)
    {
        var label = phase.EndsWith("after", StringComparison.Ordinal) ? "after" : "before";
        var output = Path.Combine(root, "perf-" + label + ".json");
        try
        {
            if (view.CoreWebView2 is null) throw new InvalidOperationException("CoreWebView2 未就绪。");
            var study = window.StudyForTest ?? throw new InvalidOperationException("学习代理未就绪。");
            var process = Process.GetCurrentProcess();
            var result = new Dictionary<string, object>
            {
                ["Phase"] = label,
                ["Timestamp"] = DateTimeOffset.Now.ToString("O"),
                ["StartupToReadyMs"] = Math.Round((DateTime.Now - process.StartTime).TotalMilliseconds, 1)
            };

            // ① 页面加载：同一本地起始页重复导航，测 NavigationStarting → NavigationCompleted。
            var navigation = new List<double>();
            for (var i = 0; i < NavigationSamples; i++)
                navigation.Add(await MeasureNavigationAsync(browser));
            result["Navigation"] = Stats(navigation);

            // ② 交互响应与注入体积：轮询读取与配置命令都经过同一脚本通道。
            var callsBefore = study.ExecuteCalls;
            var bytesBefore = study.InjectedBytes;
            var cpuBefore = process.TotalProcessorTime;

            var reads = new List<double>();
            for (var i = 0; i < ReadSamples; i++)
            {
                var sw = Stopwatch.StartNew();
                await study.ReadAsync(CancellationToken.None);
                sw.Stop();
                reads.Add(sw.Elapsed.TotalMilliseconds);
            }

            var interactions = new List<double>();
            var settings = new AppSettings();
            for (var i = 0; i < InteractionSamples; i++)
            {
                var sw = Stopwatch.StartNew();
                await study.ConfigureAsync(settings);
                sw.Stop();
                interactions.Add(sw.Elapsed.TotalMilliseconds);
            }

            var calls = study.ExecuteCalls - callsBefore;
            var bytes = study.InjectedBytes - bytesBefore;
            result["ReadAsync"] = Stats(reads);
            result["Interaction"] = Stats(interactions);
            result["Injection"] = new
            {
                Calls = calls,
                Bytes = bytes,
                AvgBytesPerCall = calls == 0 ? 0 : Math.Round((double)bytes / calls, 1)
            };
            result["CpuDuringWorkMs"] = Math.Round((process.TotalProcessorTime - cpuBefore).TotalMilliseconds, 1);

            // ③ 启动期运行时校验：冷/热两种缓存状态，复现 BrowserRuntime.Prepare 的开销。
            result["RuntimePrepare"] = MeasureRuntimePrepare(root);

            // ④ 资源占用：应用进程与 WebView2 进程组。
            result["Process"] = SnapshotProcess();
            result["WebView2"] = SnapshotWebView2();

            File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new
            {
                Success = false, Phase = label, Error = error.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static async Task<double> MeasureNavigationAsync(BrowserHost browser)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded() => completion.TrySetResult();
        browser.PageLoaded += OnLoaded;
        try
        {
            var sw = Stopwatch.StartNew();
            browser.Navigate(BrowserPolicy.LocalStartUrl);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        finally { browser.PageLoaded -= OnLoaded; }
    }

    private static object MeasureRuntimePrepare(string root)
    {
        var sourceRoot = Path.Combine(root, "perf-runtime");
        var source = Path.Combine(sourceRoot, "WebView2Runtime");
        var cache = Path.Combine(root, "perf-runtime-cache");
        Directory.CreateDirectory(source);
        var names = new[] { "msedgewebview2.exe", "lib.dll", "data.pak" };
        var payload = new byte[4 * 1024 * 1024];
        new Random(20261113).NextBytes(payload);
        var files = new List<object>();
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(source, name), payload);
            files.Add(new { Path = name, SHA256 = Convert.ToHexString(SHA256.HashData(payload)) });
        }
        File.WriteAllText(Path.Combine(source, "runtime-manifest.json"),
            JsonSerializer.Serialize(new { Files = files }));
        if (Directory.Exists(cache)) Directory.Delete(cache, true);

        var coldSw = Stopwatch.StartNew();
        BrowserRuntime.Prepare(sourceRoot, cache);
        coldSw.Stop();

        var warm = new List<double>();
        for (var i = 0; i < RuntimeSamples; i++)
        {
            var sw = Stopwatch.StartNew();
            BrowserRuntime.Prepare(sourceRoot, cache);
            sw.Stop();
            warm.Add(sw.Elapsed.TotalMilliseconds);
        }
        return new { ColdMs = Math.Round(coldSw.Elapsed.TotalMilliseconds, 2), WarmMs = Stats(warm) };
    }

    private static object SnapshotProcess()
    {
        var process = Process.GetCurrentProcess();
        return new
        {
            WorkingSetMB = Math.Round(process.WorkingSet64 / 1048576d, 1),
            PrivateMB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1),
            Handles = process.HandleCount,
            Threads = process.Threads.Count,
            CpuMs = Math.Round(process.TotalProcessorTime.TotalMilliseconds, 1)
        };
    }

    private static object SnapshotWebView2()
    {
        long total = 0;
        var processes = Process.GetProcessesByName("msedgewebview2");
        foreach (var process in processes)
        {
            try { total += process.WorkingSet64; } catch (Exception) { }
        }
        return new { Processes = processes.Length, TotalWorkingSetMB = Math.Round(total / 1048576d, 1) };
    }

    private static object Stats(List<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        return new
        {
            Samples = values.Count,
            AvgMs = Math.Round(values.Average(), 2),
            P50Ms = Math.Round(sorted[values.Count / 2], 2),
            P95Ms = Math.Round(sorted[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * 0.95) - 1)], 2),
            MinMs = Math.Round(sorted[0], 2),
            MaxMs = Math.Round(sorted[^1], 2)
        };
    }
}
