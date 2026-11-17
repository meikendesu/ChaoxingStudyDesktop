using System.Text.Json;
using System.Net.Http;
using Desktop.Core;

namespace Desktop.App.Browser;

public sealed partial class StudyBrowser
{
    public string OcrDiagnostic { get; private set; } = "尚未识别";
    private sealed record OcrTask(string Id, string Source, string Data, string Url);

    public async Task PrepareOcrAsync(OcrClient client, CancellationToken cancellation)
    {
        var candidates = await ReadAsync(cancellation);
        var version = revision;
        // 原脚本在题干仍为混淆私用区字符时也会走 OCR 回退；此前只处理 font/image，
        // 会让 private-use 题目永久停在 unsupported，不再尝试识别。
        foreach (var frame in candidates.Where(frame => frame.Snapshot.Questions.Any(q => q.Issue is "font" or "image" or "private-use")))
        {
            var attempted = new HashSet<string>();
            // 每轮限制工作量，未识别的图片继续保持阻塞，下次轮询补齐。
            for (var count = 0; count < 16; count++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (revision != version) throw new OperationCanceledException();
                var document = JsonSerializer.Serialize(frame.Snapshot.DocumentId);
                var skip = JsonSerializer.Serialize(attempted);
                var raw = await Execute(frame.Frame, $"window.__studyDesktop?.snapshot().documentId === {document} ? window.__studyDesktop.ocrTasks(1,{skip}) : []").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                if (raw.Length > 12 * 1024 * 1024) { OcrDiagnostic = "图片过大，暂不识别"; break; }
                var task = JsonSerializer.Deserialize<OcrTask[]>(raw, json)?.FirstOrDefault();
                if (task is null) break;
                attempted.Add(task.Id);
                try
                {
                    byte[] bytes;
                    if (task.Data.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
                        bytes = Convert.FromBase64String(task.Data[22..]);
                    else if (Uri.TryCreate(task.Url, UriKind.Absolute, out var url) && FontFileClient.IsAllowed(url))
                        bytes = await new FontFileClient(fontHttp).DownloadAsync(url, cancellation);
                    else { OcrDiagnostic = "图片不可读取，已保留待处理状态"; continue; }
                    var result = await client.RecognizeAsync(bytes, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (revision != version) throw new OperationCanceledException();
                    if (!result.Usable) { OcrDiagnostic = "OCR 无文字或置信度不足，未作为已完成题目"; continue; }
                    var args = string.Join(',', document, JsonSerializer.Serialize(task.Id), JsonSerializer.Serialize(task.Source), JsonSerializer.Serialize(result.Text));
                    var applied = await Execute(frame.Frame, $"window.__studyDesktop?.applyOcr({args}) === true").WaitAsync(TimeSpan.FromSeconds(5), cancellation);
                    OcrDiagnostic = applied == "true" ? $"OCR 文字已绑定，平均置信度 {result.Confidence:0.#}%" : "题目图片已经变化，已丢弃旧 OCR 结果";
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && revision == version)
                { OcrDiagnostic = "OCR 超时，已保留待处理状态"; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or HttpRequestException or FormatException)
                {
                    OcrDiagnostic = error is InvalidOperationException ? error.Message : "OCR 引擎或图片不可用，请检查安装及语言包";
                    if (!System.IO.File.Exists(OcrClient.ResolveExecutable(AiConfiguration.ResolvePath(AppContext.BaseDirectory)))) break;
                }
            }
        }
    }
}
