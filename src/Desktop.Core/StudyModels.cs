using System.Text.Json;

namespace Desktop.Core;

public static class StudyProgress
{
    public static double? Percent(int completed, int total)
        => total <= 0 || completed < 0 || completed > total ? null : Math.Round(100.0 * completed / total, 1);
}

public sealed class CompletionTracker
{
    private readonly HashSet<string> refreshed = [];
    public bool Update(string course, int completed, int total)
        => course.Length > 0 && total > 0 && completed == total && refreshed.Add(course);
}

public sealed record StudyQuestion(string Id, string Kind, string Text, string[] Options, int BlankCount, string Issue = "");
// 仅包含程序生成的校验原因，不携带模型原文；明确无法确定时不强制补答案。
public sealed class AiAnswerFormatException(string message, bool canRepair = true, Exception? inner = null) : FormatException(message, inner)
{
    public bool CanRepair { get; } = canRepair;
}

public sealed record AiAnswer(int[] Choices, string[] Blanks)
{
    public static AiAnswer Parse(string json, StudyQuestion question)
    {
        try
        {
            if (json.Length > 16000) throw new AiAnswerFormatException("答案过长。");
            json = json.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                // 只接收一个完整代码块；块前后解释、多个块和未闭合块仍拒绝。
                var block = System.Text.RegularExpressions.Regex.Match(json,
                    @"\A```(?:json)?[ \t]*\r?\n(?<answer>[\s\S]*?)\r?\n```\z",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!block.Success) throw new AiAnswerFormatException("答案代码块不完整或包含额外内容。");
                json = block.Groups["answer"].Value;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var choices = root.TryGetProperty("choices", out var c) ? c.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
            var blanks = root.TryGetProperty("blanks", out var b) ? b.EnumerateArray().Select(x => x.GetString()?.Trim() ?? "").ToArray() : [];
            if ((root.TryGetProperty("choices", out _) || root.TryGetProperty("blanks", out _)) && choices.Length == 0 && blanks.Length == 0)
                throw new AiAnswerFormatException("模型未能确定答案，返回了空数组。", canRepair: false);
            if (question.Kind == "blank")
            {
                if (choices.Length != 0) throw new AiAnswerFormatException("填空题应返回 blanks，choices 必须为空数组。");
                if (question.BlankCount < 1 || blanks.Length != question.BlankCount)
                    throw new AiAnswerFormatException($"填空数量不匹配：需要 {question.BlankCount} 空，收到 {blanks.Length} 空。");
                if (blanks.Any(string.IsNullOrWhiteSpace)) throw new AiAnswerFormatException("填空答案中存在空白项。");
            }
            else if (question.Kind is not ("single" or "multiple") || blanks.Length != 0 || choices.Length == 0 ||
                choices.Distinct().Count() != choices.Length || choices.Any(x => x < 0 || x >= question.Options.Length) ||
                (question.Kind == "single" && choices.Length != 1))
                throw new AiAnswerFormatException($"选项格式不符：{(question.Kind == "single" ? "单选/判断需要恰好 1 个索引；" : "")}索引范围 0–{question.Options.Length - 1}，不可重复，blanks 必须为空数组。");
            return new(choices, blanks);
        }
        catch (Exception error) when (error is not AiAnswerFormatException && error is (JsonException or InvalidOperationException or OverflowException or FormatException))
        { throw new AiAnswerFormatException("AI 未返回有效的答案 JSON 或字段类型不正确。", inner: error); }
    }
}

public sealed class FrameStudySnapshot
{
    public string DocumentId { get; set; } = "";
    public string Url { get; set; } = "";
    public string CourseId { get; set; } = "";
    public string ChapterId { get; set; } = "";
    public int Completed { get; set; }
    public int Total { get; set; }
    public int VideoCount { get; set; }
    public int VideoEnded { get; set; }
    public int VideoFinished { get; set; }
    public int TaskPointCount { get; set; }
    public int VideoTabCount { get; set; }
    public int AudioCount { get; set; }
    public int AudioEnded { get; set; }
    public int AudioFinished { get; set; }
    public int AudioTabCount { get; set; }
    // 播放和推进共用媒体队列；保留原视频统计字段的含义。
    public int MediaCount => VideoCount + AudioCount;
    public int MediaFinished => VideoFinished + AudioFinished;
    public int MediaTabCount => VideoTabCount + AudioTabCount;
    public int DocumentCount { get; set; }
    public bool DocumentComplete { get; set; }
    public double Duration { get; set; }
    public double Position { get; set; }
    public bool CurrentComplete { get; set; }
    public StudyQuestion[] Questions { get; set; } = [];
    public int QuestionTotal { get; set; }
    public bool QuestionOverflow { get; set; }
    public bool QuestionPagePending { get; set; }
    public string MediaStatus { get; set; } = "";
    public bool MediaPaused { get; set; }
    public double PlaybackRate { get; set; }
    public int AdvanceAtPercent { get; set; }
    public JsonElement[] MediaEvents { get; set; } = [];
}
