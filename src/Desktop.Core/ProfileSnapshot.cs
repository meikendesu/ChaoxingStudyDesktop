namespace Desktop.Core;

// 切换配置时在后台一次性读取目标配置的全部本地数据。
// 只做读取与可用性校验，不写文件、不触碰 UI、不改动运行时已加载的配置。
public sealed record ProfileSnapshot(
    string Root,
    AppSettings Settings,
    string? SettingsWarning,
    string EnvPath,
    bool HasEnv,
    bool HasTask,
    bool HasSessionCookies,
    int CookieCount)
{
    public static ProfileSnapshot Read(string root, string globalEnvPath)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var loaded = new SettingsStore(root).Load();
        var localEnv = Path.Combine(root, ".env");
        var envPath = File.Exists(localEnv) ? localEnv : globalEnvPath;
        // 只确认 .env 可读取，不把密钥带进快照对象。
        try { _ = AiConfiguration.LoadFile(envPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var hasEnv = File.Exists(envPath);

        var hasTask = new TaskStateStore(root).Load() is not null;
        var hasSession = File.Exists(Path.Combine(root, "session-cookies.bin"));
        int cookies;
        try { cookies = new ProfileCookieStore(root).Read().Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { cookies = 0; }
        return new(root, loaded.Settings, loaded.Warning, envPath, hasEnv, hasTask, hasSession, cookies);
    }
}
