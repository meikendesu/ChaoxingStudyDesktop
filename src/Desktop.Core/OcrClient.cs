using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace Desktop.Core;

public sealed record OcrResult(string Text, double Confidence, bool Usable);

// 独立进程，不把模型密钥或浏览器 Cookie 交给 OCR；临时图片只在当前调用中存在。
public sealed class OcrClient(string executable, string workRoot)
{
    private readonly Dictionary<string, OcrResult> cache = [];
    public static string ResolveExecutable(string envPath)
    {
        var configured = File.Exists(envPath) ? File.ReadLines(envPath).Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith("CX_OCR_PATH=", StringComparison.Ordinal))?[12..].Trim().Trim('"', '\'') : null;
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured, Path.GetDirectoryName(Path.GetFullPath(envPath))!);
        var candidates = new[] {
            Path.Combine(AppContext.BaseDirectory, "ocr", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tesseract-OCR", "tesseract.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tesseract.exe")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim('"'), "tesseract.exe")));
        return candidates.FirstOrDefault(File.Exists) ?? candidates.First();
    }

    public static OcrResult ParseTsv(string tsv)
    {
        var words = new List<(string Line, string Text, double Confidence)>();
        foreach (var row in tsv.Split('\n').Skip(1))
        {
            var cells = row.TrimEnd('\r').Split('\t', 12);
            if (cells.Length != 12 || cells[0] != "5" || string.IsNullOrWhiteSpace(cells[11])) continue;
            if (!double.TryParse(cells[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || !double.IsFinite(confidence) || confidence < 0 || confidence > 100)
                return new("", 0, false);
            words.Add((string.Join(':', cells.Skip(1).Take(4)), cells[11].Trim(), confidence));
        }
        if (words.Count == 0) return new("", 0, false);
        var text = string.Join('\n', words.GroupBy(word => word.Line).Select(line => string.Join(' ', line.Select(word => word.Text))));
        var average = words.Sum(word => word.Confidence * word.Text.Length) / words.Sum(word => word.Text.Length);
        return new(text, average, text.Length <= 12000 && average >= 75 && words.All(word => word.Confidence >= 35) && !text.Any(c => c is >= '\uE000' and <= '\uF8FF'));
    }

    public async Task<OcrResult> RecognizeAsync(byte[] image, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(executable)) throw new InvalidOperationException("OCR 引擎未安装：请安装 Tesseract 及 chi_sim、eng 语言包，或在 .env 设置 CX_OCR_PATH。");
        if (image.Length is 0 or > 8 * 1024 * 1024) throw new InvalidOperationException("OCR 图片大小不支持。");
        var hash = Convert.ToHexString(SHA256.HashData(image));
        if (cache.TryGetValue(hash, out var cached)) return cached;
        var directory = Path.Combine(Path.GetFullPath(workRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.png");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = new Process();
        try
        {
            await File.WriteAllBytesAsync(input, image, deadline.Token);
            process.StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, WorkingDirectory = directory };
            foreach (var argument in new[] { input, "stdout", "--tessdata-dir", Path.Combine(Path.GetDirectoryName(executable)!, "tessdata"), "-l", "chi_sim+eng", "--oem", "1", "--psm", "6", "tsv" })
                process.StartInfo.ArgumentList.Add(argument);
            // 子进程不继承模型环境密钥；图片只通过本地文件路径传递。
            foreach (var key in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("CX_AI_", StringComparison.OrdinalIgnoreCase)).ToArray())
                process.StartInfo.Environment.Remove(key);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            var tsv = await output;
            await error; // 不将可能含本地路径或课程文本的引擎输出写进日志。
            if (process.ExitCode != 0) throw new InvalidOperationException("OCR 引擎运行失败，请检查中文和英文语言包。");
            var result = tsv.Length <= 1024 * 1024 ? ParseTsv(tsv) : new("", 0, false);
            if (result.Usable) { if (cache.Count >= 128) cache.Clear(); cache[hash] = result; }
            return result;
        }
        finally
        {
            if (File.Exists(input)) File.Delete(input);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }
}
