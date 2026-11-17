using System.IO;
using System.Windows;

namespace Desktop.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--check-ai")
        {
            try
            {
                using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false });
                var config = Desktop.Core.AiConfiguration.Load(AppContext.BaseDirectory);
                var answer = await new Desktop.Core.AiClient(http).AskAsync(config,
                    new Desktop.Core.StudyQuestion("connection-check", "single", "1 + 1 等于多少？", ["1", "2", "3"], 0), CancellationToken.None);
                File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(new { Success = answer.Choices.SequenceEqual(new[] { 1 }), config.Model, answer.Choices }));
            }
            catch (Exception error)
            {
                File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(new { Success = false, Error = error.GetType().Name,
                    Status = error is System.Net.Http.HttpRequestException http ? http.StatusCode?.ToString() : null }));
            }
            Shutdown();
            return;
        }
        var smoke = e.Args.Length == 3 && e.Args[0] == "--smoke-test";
        if (smoke)
        {
            DispatcherUnhandledException += (_, args) => SmokeTest.WriteFailure(e.Args[1], e.Args[2], args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception error) SmokeTest.WriteFailure(e.Args[1], e.Args[2], error);
            };
        }
        if (e.Args.Length == 4 && e.Args[0] == "--dump-page")
        {
            var dumpWindow = new MainWindow(Path.GetFullPath(e.Args[1]), null) { DumpUrl = e.Args[2], DumpTarget = Path.GetFullPath(e.Args[3]) };
            MainWindow = dumpWindow;
            dumpWindow.Show();
            return;
        }
        string root;
        try { root = smoke ? Path.GetFullPath(e.Args[1]) : (e.Args.Length == 2 && e.Args[0] == "--data-root" ? Path.GetFullPath(e.Args[1]) : ResolveDataRoot()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("登录资料迁移未完成。请关闭旧版客户端后重试；原资料保留。", "登录资料暂不可用");
            Shutdown(); return;
        }
        var acceptance = e.Args.Length == 2 && e.Args[0] == "--acceptance";
        // 首次启动协议：仅交互式启动需要确认；烟测、诊断与自动验收模式跳过。
        // 对话框期间避免"最后一个窗口关闭"触发的自动退出，主窗口显示后恢复。
        var interactive = !smoke && !acceptance;
        if (interactive && !new Desktop.Core.AgreementStore(root).IsAccepted())
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var agreement = new AgreementWindow(root) { Owner = null };
            agreement.ShowDialog();
            if (!agreement.Accepted)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        var window = new MainWindow(root, smoke ? e.Args[2] : null);
        if (acceptance) window.AcceptancePath = Path.GetFullPath(e.Args[1]);
        MainWindow = window;
        if (smoke)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
        }
        window.Show();
    }

    // 优先使用 %LOCALAPPDATA%；受限/沙箱环境不可写时回退到程序目录，
    // 避免 WebView2 无法创建 profile 时抛出 0x800700AA (ERROR_BUSY)。
    private static string ResolveDataRoot()
    {
        var preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChaoxingStudyDesktop");
        var fallback = Desktop.Core.UserDataPaths.Fallback(AppContext.BaseDirectory);
        if (Directory.Exists(Path.Combine(fallback, "BrowserProfile")) && IsWritable(fallback)) return fallback;
        if (IsWritable(preferred)) return preferred;
        Desktop.Core.UserDataPaths.MigrateLegacy(fallback);
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}
