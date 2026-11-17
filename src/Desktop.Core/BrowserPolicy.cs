namespace Desktop.Core;

public static class BrowserPolicy
{
    public const string LocalHost = "study-desktop.local";
    public const string HomeUrl = "https://i.chaoxing.com";
    public const string LocalStartUrl = "https://study-desktop.local/start.html";
    public const string LoginUrl = "https://passport2.chaoxing.com/login";

    // 已取消域名白名单：允许浏览任意 HTTP/HTTPS 网页，不再拦截校园 SSO 或第三方站点。
    // 仅拒绝 javascript:/file:/data: 等非网页协议，避免把顶层导航变成脚本或本地文件访问。
    public static bool IsAllowed(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
