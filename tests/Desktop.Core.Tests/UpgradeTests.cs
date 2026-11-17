using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class UpgradeTests
{
    [Fact]
    public void Font_paths_use_the_original_md5_suffix_and_ignore_unknown_glyphs()
    {
        var decoded = FontGlyphMap.Decode(new Dictionary<int, string> { [19968] = "", [19969] = "unknown" },
            new Dictionary<string, int> { ["ecf8427e"] = 20320 });
        Assert.Equal("你", decoded[19968]);
        Assert.False(decoded.ContainsKey(19969));
    }

    [Fact]
    public void Home_is_the_official_portal() => Assert.Equal("https://i.chaoxing.com", BrowserPolicy.HomeUrl);

    [Fact]
    public void Progress_requires_a_known_nonempty_total()
    {
        Assert.Null(StudyProgress.Percent(0, 0));
        Assert.Null(StudyProgress.Percent(3, 2));
        Assert.Equal(25, StudyProgress.Percent(1, 4));
    }

    [Fact]
    public void Course_completion_refreshes_once_per_course_transition()
    {
        var tracker = new CompletionTracker();
        Assert.False(tracker.Update("a", 1, 2));
        Assert.True(tracker.Update("a", 2, 2));
        Assert.False(tracker.Update("a", 2, 2));
        Assert.True(tracker.Update("b", 1, 1));
        Assert.False(tracker.Update("", 1, 1));
    }

    [Fact]
    public void Env_is_loaded_from_project_ancestor_and_preserves_equals_in_key()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "artifacts", "publish"));
            File.WriteAllText(Path.Combine(root, "ChaoxingStudyDesktop.slnx"), "");
            File.WriteAllText(Path.Combine(root, ".env"), "CX_AI_PROVIDER=zhipu\nCX_AI_API_KEY=\"fake=key\"\nCX_AI_MODEL=test-model\nCX_AI_ENDPOINT=https://example.test/v1/chat/completions\n");
            var config = AiConfiguration.Load(Path.Combine(root, "artifacts", "publish"));
            Assert.Equal("fake=key", config.ApiKey);
            Assert.Equal("test-model", config.Model);
            Assert.Equal(Path.Combine(root, ".env"), config.ConfigPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Answers_reject_explanations_invalid_indices_and_incomplete_blanks()
    {
        var choice = new StudyQuestion("q", "single", "题干", ["甲", "乙"], 0);
        Assert.Equal([1], AiAnswer.Parse("{\"choices\":[1],\"blanks\":[]}", choice).Choices);
        Assert.ThrowsAny<FormatException>(() => AiAnswer.Parse("答案是 B", choice));
        Assert.ThrowsAny<FormatException>(() => AiAnswer.Parse("{\"choices\":[2]}", choice));
        Assert.ThrowsAny<FormatException>(() => AiAnswer.Parse("{\"choices\":[0,1]}", choice));
        var blanks = new StudyQuestion("b", "blank", "两空", [], 2);
        Assert.ThrowsAny<FormatException>(() => AiAnswer.Parse("{\"blanks\":[\"一\"]}", blanks));
    }

    [Fact]
    public void Advance_percent_defaults_to_91_and_is_persisted_and_validated()
    {
        Assert.Equal(91, new AppSettings().AdvanceAtPercent);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new SettingsStore(root);
            store.Save(new AppSettings { SelectedProvider = "zhipu", AdvanceAtPercent = 95 });
            Assert.Equal(95, new SettingsStore(root).Load().Settings.AdvanceAtPercent);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"selectedProvider\":\"zhipu\",\"advanceAtPercent\":999}");
            var loaded = new SettingsStore(root).Load();
            Assert.NotNull(loaded.Warning);
            Assert.Equal(91, loaded.Settings.AdvanceAtPercent);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Playback_rate_defaults_to_2x_and_is_validated()
    {
        Assert.Equal(2.0, new AppSettings().PlaybackRate);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new SettingsStore(root);
            store.Save(new AppSettings { SelectedProvider = "zhipu", PlaybackRate = 3.0 });
            Assert.Equal(3.0, new SettingsStore(root).Load().Settings.PlaybackRate);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"selectedProvider\":\"zhipu\",\"playbackRate\":99}");
            Assert.NotNull(new SettingsStore(root).Load().Warning);
            Assert.Equal(2.0, new SettingsStore(root).Load().Settings.PlaybackRate);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Task_state_round_trips_and_ignores_empty_or_corrupt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TaskStateStore(root);
            Assert.Null(store.Load());
            store.Save(new TaskSnapshot { CourseUrl = "https://mooc1.chaoxing.com/course", CourseId = "123", SavedAt = DateTimeOffset.Now });
            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("123", loaded.CourseId);
            store.Clear();
            Assert.Null(store.Load());
            File.WriteAllText(Path.Combine(root, "task.json"), "{broken");
            Assert.Null(store.Load());
        }
        finally { Directory.Delete(root, true); }
    }

}
