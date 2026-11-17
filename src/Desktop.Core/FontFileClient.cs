using System.Net;

namespace Desktop.Core;

// 对应原脚本 GM_xmlhttpRequest：字体下载独立于网页 CORS，不使用模型密钥。
public sealed class FontFileClient(HttpClient http)
{
    public static bool IsAllowed(Uri uri) => uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
        (uri.Host == "chaoxing.com" || uri.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "xuexitong.com" || uri.Host.EndsWith(".xuexitong.com", StringComparison.OrdinalIgnoreCase));

    public async Task<byte[]> DownloadAsync(Uri uri, CancellationToken cancellation)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (!IsAllowed(uri)) throw new InvalidOperationException("font-host-unsupported");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                uri = new Uri(uri, response.Headers.Location ?? throw new HttpRequestException("font-redirect-failed"));
                continue;
            }
            response.EnsureSuccessStatusCode();
            const int limit = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("font-too-large");
            using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var result = new MemoryStream();
            var buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                if (result.Length + length > limit) throw new InvalidOperationException("font-too-large");
                result.Write(buffer, 0, length);
            }
            return result.ToArray();
        }
        throw new HttpRequestException("font-redirect-limit");
    }
}
