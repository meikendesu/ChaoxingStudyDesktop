using System.Windows;
using Desktop.Core;

namespace Desktop.App;

public partial class MainWindow
{
    private readonly CourseWorkflow courseWorkflow = new();
    private readonly List<string> courseNotices = [];
    private bool courseFlowBusy;

    // 供离线回归替换弹窗实现，避免无人值守时 MessageBox 阻塞；正式运行始终使用系统弹窗。
    internal Action<string, string>? CourseNoticeHandler { get; set; }
    internal IReadOnlyList<string> CourseNoticesForTest => courseNotices;
    internal string CourseFlowPhaseForTest => courseWorkflow.Phase.ToString();
    internal string CourseFlowLabelForTest => courseWorkflow.CourseLabel;
    internal int CourseFlowCompletedForTest => courseWorkflow.TaskCompleted;
    internal int CourseFlowTotalForTest => courseWorkflow.TaskTotal;

    private void NotifyCourse(string title, string message)
    {
        courseNotices.Add($"{title}|{message}");
        log.Write("INFO", $"{title}：{message}");
        if (CourseNoticeHandler is not null) { CourseNoticeHandler(title, message); return; }
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void StartCourseFlowClick(object sender, RoutedEventArgs e)
    {
        if (study is null || courseWorkflow.IsActive) return;
        StartCourseFlowButton.IsEnabled = false;
        try { await StartCourseFlowAsync(); }
        finally { StartCourseFlowButton.IsEnabled = true; }
    }

    // 触发入口与业务流程一致；回归也调用此方法，不绕过状态机。
    internal Task StartCourseFlowForTestAsync() => StartCourseFlowAsync();

    private async Task StartCourseFlowAsync()
    {
        if (study is null) return;
        courseNotices.Clear();
        courseWorkflow.Start();
        // 课程页面（带各个任务点的界面）已由用户在当前窗口打开；此处只读取当前页面，不导航、不重新进入。
        CourseFlowStatusText.Text = "正在检查当前已打开课程页面的全部任务点…";
        await CourseFlowTickAsync(lifetime.Token);
    }

    internal Task CourseFlowTickForTestAsync() => CourseFlowTickAsync(lifetime.Token);
    internal void StopCourseFlowForTest() => courseWorkflow.Stop();

    // 课程流程在既有 2 秒轮询上独立推进；停用时不读取页面，不改变原有自动播放/答题逻辑。
    private async Task CourseFlowTickAsync(CancellationToken token)
    {
        if (!courseWorkflow.IsActive || courseFlowBusy || study is null) return;
        courseFlowBusy = true;
        try
        {
            // 课程页面（章节任务点目录）与学习页面（音视频/题目/任务点标签）由网页代理分别标记；
            // 这里只消费当前页面及其框架的结构，不猜测 URL、不导航，也不读取题干、答案或登录数据。
            var pages = await study.ReadCourseOverviewsAsync(token);
            var decision = courseWorkflow.Observe(pages);
            switch (decision.Action)
            {
                case CourseWorkflowAction.WaitForCoursePage:
                    CourseFlowStatusText.Text = pages.Any(p => p.PageKind == "study")
                        ? "当前为学习页面；请在本窗口返回带各个任务点的课程页面后重试（不会自动跳转）"
                        : pages.Any(p => p.PageKind == "catalog")
                            ? "当前为课程列表；请打开带各个任务点的课程页面后重试（不会自动跳转）"
                            : "当前页面没有带各个任务点的课程目录，请在已打开的界面中打开课程页面";
                    break;

                case CourseWorkflowAction.NotifyAlreadyCompleted:
                    CourseFlowStatusText.Text = decision.Message;
                    NotifyCourse($"{decision.Title}｜{courseWorkflow.CourseLabel}", decision.Message);
                    break;

                case CourseWorkflowAction.OpenPendingTask:
                {
                    var course = pages.First(p => p.IsCoursePage);
                    var result = await study.CourseCommandAsync(course.DocumentId, "openCourseTask", decision.PendingIndex, token);
                    if (result != "\"clicked\"")
                    {
                        CourseFlowStatusText.Text = "第一个未完成任务点未能打开，流程已暂停";
                        courseWorkflow.Stop();
                        break;
                    }
                    await BeginAutomaticStudyAsync(workflow.Token);
                    CourseFlowStatusText.Text = $"课程 {courseWorkflow.CourseLabel}：已进入第 {decision.PendingIndex + 1} 个未完成任务点并启动自动流程（{courseWorkflow.TaskCompleted}/{courseWorkflow.TaskTotal}）";
                    log.Write("INFO", CourseFlowStatusText.Text);
                    break;
                }

                case CourseWorkflowAction.NotifyFinished:
                    await StopStudyAsync();
                    CourseFlowStatusText.Text = decision.Message;
                    NotifyCourse($"{decision.Title}｜{courseWorkflow.CourseLabel}", decision.Message);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { log.Write("WARN", $"课程流程读取未完成：{error.GetType().Name}"); }
        finally { courseFlowBusy = false; }
    }

    // 抽取原「开始自动播放」的启动动作，供课程流程复用，行为完全一致。
    private async Task BeginAutomaticStudyAsync(CancellationToken token)
    {
        if (study is null) return;
        await study.StopAsync();
        playing = true;
        automaticAnswersEnabled = true;
        nextAutoAnswerAt = DateTime.MinValue;
        StudyStatusText.Text = "正在发现视频与章节";
        var frames = await study.ReadAsync(token);
        foreach (var frame in frames.Where(f => f.Snapshot.MediaTabCount > 0))
            await study.CommandAsync(frame, "startVideos", token);
        if (smokePhase is null) ActiveView.Focus();
    }
}
