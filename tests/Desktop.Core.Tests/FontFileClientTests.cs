using System.Net;
using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class FontFileClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Assert.Null(request.Headers.Authorization); return send(request, token); }
    }

    [Fact]
    public async Task Native_download_follows_platform_redirect_without_model_key()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/first"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/font", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 1, 2]) }));
        using var http = new HttpClient(handler);
        Assert.Equal(new byte[] { 0, 1, 2 }, await new FontFileClient(http).DownloadAsync(new Uri("https://mooc1.chaoxing.com/first"), CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Rejects_nonplatform_redirect_and_oversized_font()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
        { Headers = { Location = new Uri("https://chaoxing.com.evil.test/font") } }));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FontFileClient(http).DownloadAsync(new Uri("https://chaoxing.com/font"), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
        using var largeHandler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]) }));
        using var largeHttp = new HttpClient(largeHandler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FontFileClient(largeHttp).DownloadAsync(new Uri("https://chaoxing.com/font"), CancellationToken.None));
    }

    [Fact]
    public async Task Stop_cancels_download()
    {
        var started = new TaskCompletionSource();
        using var handler = new Handler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var task = new FontFileClient(http).DownloadAsync(new Uri("https://chaoxing.com/font"), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
