using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public class CourseWorkflowTests
{
    private static CourseOverview Course(string key, string name, int total, int completed, string kind = "course") => new()
    {
        DocumentId = "doc-" + key,
        Url = $"https://mooc1.chaoxing.com/mycourse/studentstudy?courseid={key}&clazzid=777",
        PageKind = kind,
        CourseKey = key,
        CourseName = name,
        TaskTotal = total,
        TaskCompleted = completed,
        FirstPendingIndex = completed < total ? completed : -1
    };

    private static CourseOverview StudyPage(string key = "") => new()
    {
        DocumentId = "doc-study", Url = "https://mooc1.chaoxing.com/knowledge/cards?chapterId=1",
        PageKind = "study", CourseKey = key, StudyMarkers = 3
    };

    private static CourseOverview CatalogPage() => new()
    {
        DocumentId = "doc-catalog", Url = "https://i.chaoxing.com", PageKind = "catalog",
        CourseLinks = [new CourseLink("高等数学", "https://mooc1.chaoxing.com/mycourse/studentstudy?courseid=1001")]
    };

    [Fact]
    public void Course_page_and_study_page_are_mutually_exclusive()
    {
        var course = Course("1001", "高等数学", 8, 3);
        var study = StudyPage();
        Assert.True(course.IsCoursePage);
        Assert.False(study.IsCoursePage);
        Assert.Equal("course", course.PageKind);
        Assert.Equal("study", study.PageKind);
        // 学习页面不得被当成课程总览，即使它带有课程标识。
        Assert.False(Course("", "", 0, 0, "study").IsCoursePage);
        // 空目录（加载中）不能被误判为「全部完成」。
        Assert.False(Course("1001", "高等数学", 0, 0).AllCompleted);
        Assert.False(Course("1001", "高等数学", 0, 0).IsCoursePage);
    }

    [Fact]
    public void Already_completed_course_notifies_immediately_without_starting_flow()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        var decision = workflow.Observe([StudyPage(), Course("1001", "高等数学（上）", 6, 6)]);
        Assert.Equal(CourseWorkflowAction.NotifyAlreadyCompleted, decision.Action);
        Assert.Equal(CourseWorkflowPhase.Finished, workflow.Phase);
        Assert.Contains("高等数学（上）", decision.Message);
        Assert.Contains("1001", decision.Message);
        Assert.Contains("6", decision.Message);
        Assert.False(workflow.IsActive);
    }

    [Fact]
    public void Pending_course_opens_first_pending_task_only_once()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        var course = Course("1001", "线性代数", 5, 2);
        var first = workflow.Observe([course]);
        Assert.Equal(CourseWorkflowAction.OpenPendingTask, first.Action);
        Assert.Equal(2, first.PendingIndex);
        Assert.Equal(CourseWorkflowPhase.Running, workflow.Phase);

        // 任务点未变化时不得重复点击，也不得提前弹完成提示。
        var second = workflow.Observe([course]);
        Assert.Equal(CourseWorkflowAction.None, second.Action);
        Assert.Equal(CourseWorkflowPhase.Running, workflow.Phase);
    }

    [Fact]
    public void Running_course_requires_two_confirmations_before_finish_notice()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        Assert.Equal(CourseWorkflowAction.OpenPendingTask, workflow.Observe([Course("1001", "概率论", 4, 1)]).Action);

        var first = workflow.Observe([Course("1001", "概率论", 4, 4)]);
        Assert.Equal(CourseWorkflowAction.None, first.Action);
        Assert.Equal(1, workflow.CompletionStreak);

        // 单次目录抖动不能误报完成。
        var dipped = workflow.Observe([Course("1001", "概率论", 4, 3)]);
        Assert.Equal(CourseWorkflowAction.None, dipped.Action);
        Assert.Equal(0, workflow.CompletionStreak);

        Assert.Equal(CourseWorkflowAction.None, workflow.Observe([Course("1001", "概率论", 4, 4)]).Action);
        var finished = workflow.Observe([Course("1001", "概率论", 4, 4)]);
        Assert.Equal(CourseWorkflowAction.NotifyFinished, finished.Action);
        Assert.Contains("概率论", finished.Message);
        Assert.Contains("4/4", finished.Message);
        Assert.Equal(CourseWorkflowPhase.Finished, workflow.Phase);
    }

    [Fact]
    public void Already_open_course_page_is_used_directly_without_navigation()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        // 当前窗口已打开带各个任务点的课程页面：直接检查任务点，不产生任何进入/导航动作。
        var decision = workflow.Observe([CatalogPage(), StudyPage(), Course("1001", "高等数学", 6, 2)]);
        Assert.Equal(CourseWorkflowAction.OpenPendingTask, decision.Action);
        Assert.Equal(2, decision.PendingIndex);
        Assert.Equal("1001", workflow.CourseKey);
        Assert.Equal(CourseWorkflowPhase.Running, workflow.Phase);
    }

    [Fact]
    public void Non_course_pages_only_wait_and_never_navigate()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        // 课程列表页有可点击入口，但流程不得点击进入：课程页面已由用户打开。
        var catalog = workflow.Observe([CatalogPage()]);
        Assert.Equal(CourseWorkflowAction.WaitForCoursePage, catalog.Action);
        Assert.Equal(CourseWorkflowPhase.Locating, workflow.Phase);

        Assert.Equal(CourseWorkflowAction.WaitForCoursePage, workflow.Observe([StudyPage()]).Action);
        Assert.Equal(CourseWorkflowAction.WaitForCoursePage, workflow.Observe([new CourseOverview()]).Action);
        Assert.Equal(CourseWorkflowPhase.Locating, workflow.Phase);
    }

    [Fact]
    public void Course_switch_resets_flow_instead_of_reporting_wrong_course()
    {
        var workflow = new CourseWorkflow();
        workflow.Start();
        Assert.Equal(CourseWorkflowAction.OpenPendingTask, workflow.Observe([Course("A", "课程甲", 3, 1)]).Action);
        Assert.Equal(CourseWorkflowAction.None, workflow.Observe([Course("A", "课程甲", 3, 3)]).Action);
        var switched = workflow.Observe([Course("B", "课程乙", 3, 3)]);
        Assert.Equal(CourseWorkflowAction.WaitForCoursePage, switched.Action);
        Assert.Equal(CourseWorkflowPhase.Locating, workflow.Phase);
        Assert.DoesNotContain("课程甲", string.Join('|', workflow.CourseLabel));
    }

    [Fact]
    public void Course_label_always_identifies_the_course()
    {
        Assert.Contains("高等数学", Course("1001", "高等数学", 3, 0).CourseLabel);
        Assert.Contains("1001", Course("1001", "高等数学", 3, 0).CourseLabel);
        Assert.Contains("1001", Course("1001", "", 3, 0).CourseLabel);
        var noKey = Course("", "", 3, 0);
        Assert.Contains("mooc1.chaoxing.com", noKey.CourseLabel);
        Assert.NotEmpty(new CourseOverview().CourseLabel);
    }

    [Fact]
    public void Idle_and_stopped_workflows_never_emit_actions()
    {
        var workflow = new CourseWorkflow();
        Assert.Equal(CourseWorkflowAction.None, workflow.Observe([Course("1001", "课程", 3, 1)]).Action);
        workflow.Start();
        workflow.Stop();
        Assert.Equal(CourseWorkflowAction.None, workflow.Observe([Course("1001", "课程", 3, 3)]).Action);
        Assert.Equal(CourseWorkflowPhase.Idle, workflow.Phase);
    }
}
