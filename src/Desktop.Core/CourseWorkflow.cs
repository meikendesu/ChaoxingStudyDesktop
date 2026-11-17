namespace Desktop.Core;

public sealed record CourseTaskPoint(int Index, string Label, bool Completed);
public sealed record CourseLink(string Label, string Href);

// 页面快照只描述「课程页面」（带各个任务点的章节目录）或「学习页面」（章节内容）的结构，
// 不含题干、答案、Cookie 或密钥。pageKind 由网页代理给出，四类互斥。
// 课程页面由用户预先在当前窗口打开；流程只读取当前页面及其框架，不获取、不进入、不跳转。
public sealed class CourseOverview
{
    public string DocumentId { get; set; } = "";
    public string Url { get; set; } = "";
    public string PageKind { get; set; } = "";
    public string CourseKey { get; set; } = "";
    public string CourseName { get; set; } = "";
    public int TaskTotal { get; set; }
    public int TaskCompleted { get; set; }
    public int FirstPendingIndex { get; set; } = -1;
    public CourseTaskPoint[] Tasks { get; set; } = [];
    public CourseLink[] CourseLinks { get; set; } = [];
    public int StudyMarkers { get; set; }

    // 课程页面必须同时具备目录树与至少一个任务点，避免加载中的空目录被误判为「全部完成」。
    public bool IsCoursePage => PageKind == "course" && TaskTotal > 0;
    public bool AllCompleted => IsCoursePage && TaskCompleted >= TaskTotal;
    // 课程入口仅用于识别「当前不在课程页面」，不作为导航依据。
    public bool HasCourseLink => PageKind == "catalog" && CourseLinks.Length > 0;

    // 弹窗必须能明确对应课程：课程名优先，缺失时退回课程标识或页面地址。
    public string CourseLabel
    {
        get
        {
            var name = (CourseName ?? "").Trim();
            var key = (CourseKey ?? "").Trim();
            if (name.Length > 0) return key.Length > 0 ? $"{name}（{key}）" : name;
            if (key.Length > 0) return $"课程 {key}";
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri)) return $"课程 {uri.Host}{uri.AbsolutePath}";
            return "当前课程";
        }
    }
}

public enum CourseWorkflowPhase { Idle, Locating, Running, Finished }
// 不设导航动作：课程页面（带各个任务点的界面）已打开，流程只等待用户把当前窗口切到该页面。
public enum CourseWorkflowAction { None, WaitForCoursePage, NotifyAlreadyCompleted, OpenPendingTask, NotifyFinished }

public sealed record CourseWorkflowDecision(
    CourseWorkflowAction Action, int PendingIndex = -1,
    string Title = "", string Message = "");

// 纯状态机：不访问浏览器、不弹窗、不读写文件。所有动作由宿主层执行。
// 完成判定要求连续两次观察到「全部任务点已完成」，避免目录重绘期间的瞬时标记误报。
public sealed class CourseWorkflow
{
    private const int ConfirmationsRequired = 2;
    private int completionStreak;

    public CourseWorkflowPhase Phase { get; private set; } = CourseWorkflowPhase.Idle;
    public string CourseKey { get; private set; } = "";
    public string CourseLabel { get; private set; } = "";
    public int TaskTotal { get; private set; }
    public int TaskCompleted { get; private set; }
    public int CompletionStreak => completionStreak;
    public bool IsActive => Phase is CourseWorkflowPhase.Locating or CourseWorkflowPhase.Running;

    public static string AlreadyCompletedMessage(string label, int total)
        => $"课程 {label} 的全部 {total} 个任务点已完成，自动流程无需启动。";
    public static string FinishedMessage(string label, int total)
        => $"课程 {label} 自动流程完成：{total}/{total} 个任务点已完成。";

    public void Start()
    {
        Phase = CourseWorkflowPhase.Locating;
        CourseKey = "";
        CourseLabel = "";
        TaskTotal = 0;
        TaskCompleted = 0;
        completionStreak = 0;
    }

    public void Stop()
    {
        Phase = CourseWorkflowPhase.Idle;
        completionStreak = 0;
    }

    public CourseWorkflowDecision Observe(IReadOnlyList<CourseOverview> pages)
    {
        if (!IsActive)
            return new(CourseWorkflowAction.None);

        // 已锁定的课程优先匹配，避免同一页面的其它框架或另一门课程串入。
        var courses = pages.Where(page => page.IsCoursePage).ToArray();
        var course = CourseKey.Length > 0
            ? courses.FirstOrDefault(page => page.CourseKey == CourseKey)
            : null;
        course ??= courses.FirstOrDefault();

        if (course is null)
            // 课程列表页、学习页面都不是流程起点；只提示，不点击进入课程入口、不导航、不重载。
            return new(CourseWorkflowAction.WaitForCoursePage);

        if (Phase == CourseWorkflowPhase.Running && CourseKey.Length > 0 && course.CourseKey != CourseKey)
        {
            // 课程已切换：重新定位，不给原课程弹「完成」。
            Start();
            return new(CourseWorkflowAction.WaitForCoursePage);
        }

        CourseKey = course.CourseKey;
        CourseLabel = course.CourseLabel;
        TaskTotal = course.TaskTotal;
        TaskCompleted = course.TaskCompleted;

        if (course.AllCompleted)
        {
            completionStreak++;
            // 已打开课程页面的首次全量检查：全部完成就直接提示，无需等待确认周期。
            if (Phase == CourseWorkflowPhase.Locating)
            {
                Phase = CourseWorkflowPhase.Finished;
                return new(CourseWorkflowAction.NotifyAlreadyCompleted,
                    Title: "课程已完成自动流程", Message: AlreadyCompletedMessage(CourseLabel, TaskTotal));
            }
            // 自动流程进行中：连续确认，避免目录重绘或平台标记抖动造成提前完成。
            if (completionStreak < ConfirmationsRequired)
                return new(CourseWorkflowAction.None);
            Phase = CourseWorkflowPhase.Finished;
            return new(CourseWorkflowAction.NotifyFinished,
                Title: "自动流程完成", Message: FinishedMessage(CourseLabel, TaskTotal));
        }

        completionStreak = 0;
        if (Phase == CourseWorkflowPhase.Locating)
        {
            if (course.FirstPendingIndex < 0 || course.FirstPendingIndex >= course.TaskTotal)
                return new(CourseWorkflowAction.WaitForCoursePage);
            Phase = CourseWorkflowPhase.Running;
            return new(CourseWorkflowAction.OpenPendingTask, PendingIndex: course.FirstPendingIndex);
        }
        return new(CourseWorkflowAction.None);
    }
}
