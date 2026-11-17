using System.Net;
using System.Text.Json;
using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class AiClientTests
{
    private static AiConfiguration Config => new() { ApiKey = "fake-key", Model = "test-model", Endpoint = "https://example.test/v1/chat/completions" };
    private static StudyQuestion Question => new("q", "single", "1 + 1 = ?", ["1", "2"], 0);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return send(request, cancellation); }
    }

    [Fact]
    public async Task Request_has_explicit_option_indices_and_secret_only_in_header()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var body = await request.Content!.ReadAsStringAsync(token);
            Assert.DoesNotContain("fake-key", body);
            using var parsed = JsonDocument.Parse(body);
            using var question = JsonDocument.Parse(parsed.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            Assert.Equal(1, question.RootElement.GetProperty("options")[1].GetProperty("index").GetInt32());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"choices\\\":[1],\\\"blanks\\\":[]}\"}}]}") };
        });
        using var http = new HttpClient(handler);
        var answer = await new AiClient(http).AskAsync(Config, Question, CancellationToken.None);
        Assert.Equal([1], answer.Choices);
    }

    [Fact]
    public async Task Invalid_blank_count_gets_one_targeted_repair_request()
    {
        var calls = 0;
        using var handler = new Handler(async (request, token) =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (calls == 2)
            {
                var repair = body.RootElement.GetProperty("messages")[2].GetProperty("content").GetString()!;
                Assert.Contains("2", repair);
                Assert.DoesNotContain("fake-key", repair);
            }
            var answer = calls == 1 ? "{\"blanks\":[\"甲\"]}" : "{\"blanks\":[\"甲\",\"乙\"]}";
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } })) };
        });
        using var http = new HttpClient(handler);
        var result = await new AiClient(http).AskAsync(Config, new StudyQuestion("b", "blank", "两个空", [], 2), CancellationToken.None);
        Assert.Equal(["甲", "乙"], result.Blanks);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{\"choices\":[],\"blanks\":[]}", 1)]
    [InlineData("{\"choices\":[9],\"blanks\":[]}", 2)]
    public async Task Unknown_answers_are_not_forced_and_format_repair_is_bounded(string answer, int expectedCalls)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } })) }));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<FormatException>(() => new AiClient(http).AskAsync(Config, Question, CancellationToken.None));
        Assert.Equal(expectedCalls, handler.Calls);
    }

    [Fact]
    public async Task Authentication_failure_is_not_retried()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new AiClient(http).AskAsync(Config, Question, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("fake-key", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Request_uses_the_original_script_sampling_parameters_without_leaking_the_key()
    {
        using var handler = new Handler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            using var parsed = JsonDocument.Parse(body);
            Assert.Equal(0.1, parsed.RootElement.GetProperty("temperature").GetDouble(), 3);
            Assert.InRange(parsed.RootElement.GetProperty("max_tokens").GetInt32(), 200, 1024);
            Assert.DoesNotContain("fake-key", body);
            return new(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"choices\\\":[1],\\\"blanks\\\":[]}\"}}]}") };
        });
        using var http = new HttpClient(handler);
        var answer = await new AiClient(http).AskAsync(Config, Question, CancellationToken.None);
        Assert.Equal([1], answer.Choices);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Token_limit_grows_with_blank_count()
    {
        var observed = new List<int>();
        using var handler = new Handler(async (request, token) =>
        {
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            observed.Add(parsed.RootElement.GetProperty("max_tokens").GetInt32());
            return new(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"blanks\\\":[\\\"甲\\\",\\\"乙\\\"]}\"}}]}") };
        });
        using var http = new HttpClient(handler);
        var blanks = new StudyQuestion("b", "blank", "两个空", [], 2);
        var answer = await new AiClient(http).AskAsync(Config, blanks, CancellationToken.None);
        Assert.Equal(["甲", "乙"], answer.Blanks);
        Assert.Equal(200 + 2 * 64, Assert.Single(observed));
    }

    [Fact]
    public async Task Stop_cancels_a_format_repair_request()
    {
        var repairStarted = new TaskCompletionSource();
        var count = 0;
        using var handler = new Handler(async (_, token) =>
        {
            if (++count == 1)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"choices\\\":[9]}\"}}]}") };
            repairStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var request = new AiClient(http).AskAsync(Config, Question, cancellation.Token);
        await repairStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Stop_cancels_inflight_request()
    {
        var started = new TaskCompletionSource();
        using var handler = new Handler(async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK); });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var request = new AiClient(http).AskAsync(Config, Question, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}
