using System.Windows;
using System.Windows.Controls;
using Desktop.Core;

namespace Desktop.App;

// 标签栏的数据项；IsActive 决定标签高亮。
internal sealed record ProfileTab(string Id, string Name, bool IsActive);

// 配置管理对话框沿用 App.xaml 的全局控件样式，纯代码构建以免新增 XAML 资源。
internal static class ProfileDialogs
{
    public static string? PromptName(Window owner, string title, string initial = "")
    {
        var input = new TextBox { Text = initial, MinWidth = 260, Margin = new Thickness(0, 0, 0, 16) };
        var confirm = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        if (owner.TryFindResource("PrimaryButton") is Style primary) confirm.Style = primary;
        var cancel = new Button { Content = "取消", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(confirm); actions.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "配置名称", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(input); panel.Children.Add(actions);
        var dialog = new Window
        {
            Title = title, Owner = owner, Content = panel, Width = 340,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize
        };
        string? result = null;
        confirm.Click += (_, _) => { result = input.Text.Trim(); dialog.DialogResult = true; };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? result : null;
    }

    public static void Manage(Window owner, ProfileRegistry registry,
        Func<string, Task> switchAsync, Func<string, Task> createAsync,
        Func<string, Task> deleteAsync, Func<string, string, bool> rename)
    {
        var list = new ListBox { MinHeight = 160, Margin = new Thickness(0, 0, 0, 12) };
        var hint = new TextBlock
        {
            Text = "每个配置拥有独立的设置、课程记录、Cookie 与专属 WebView 实例；后台保留各自页面，切换时直接复用，主页均为 i.chaoxing.com。",
            Foreground = owner.TryFindResource("Muted") as System.Windows.Media.Brush,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var switchButton = new Button { Content = "切换到所选" };
        var createButton = new Button { Content = "新建" };
        var renameButton = new Button { Content = "重命名" };
        var deleteButton = new Button { Content = "删除" };
        var closeButton = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 0, 0, 0) };
        foreach (var button in new[] { switchButton, createButton, renameButton, deleteButton, closeButton })
            actions.Children.Add(button);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "账号配置", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(hint); panel.Children.Add(list); panel.Children.Add(actions);
        var dialog = new Window
        {
            Title = "管理配置", Owner = owner, Content = panel, Width = 520,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };

        void Refresh()
        {
            var selectedId = (list.SelectedItem as ListBoxItem)?.Tag as string;
            list.Items.Clear();
            foreach (var entry in registry.Profiles)
            {
                var label = entry.Name + (entry.Id == registry.CurrentId ? "  · 当前" : "") + (entry.IsDefault ? "  · 默认" : "");
                var item = new ListBoxItem { Content = label, Tag = entry.Id, ToolTip = registry.DirectoryOf(entry.Id) };
                list.Items.Add(item);
                if (entry.Id == selectedId) list.SelectedItem = item;
            }
            if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
        }

        string? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as string;

        async void Run(Func<Task> action)
        {
            try { await action(); Refresh(); }
            catch (InvalidOperationException error) { MessageBox.Show(dialog, error.Message, "配置管理", MessageBoxButton.OK, MessageBoxImage.Warning); }
            catch (Exception error) { MessageBox.Show(dialog, error.Message, "配置管理", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        switchButton.Click += (_, _) => { if (Selected() is { } id) Run(() => switchAsync(id)); };
        createButton.Click += (_, _) =>
        {
            var name = PromptName(dialog, "新建配置");
            if (!string.IsNullOrEmpty(name)) Run(() => createAsync(name));
        };
        renameButton.Click += (_, _) =>
        {
            if (Selected() is not { } id) return;
            var current = registry.Profiles.First(entry => entry.Id == id).Name;
            var name = PromptName(dialog, "重命名配置", current);
            if (!string.IsNullOrEmpty(name) && rename(id, name)) Refresh();
        };
        deleteButton.Click += (_, _) =>
        {
            if (Selected() is not { } id) return;
            var entry = registry.Profiles.First(item => item.Id == id);
            if (MessageBox.Show(dialog, $"删除配置「{entry.Name}」及其独立资料？该操作不可撤销。", "删除配置",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            Run(() => deleteAsync(id));
        };
        list.MouseDoubleClick += (_, _) => { if (Selected() is { } id) Run(() => switchAsync(id)); };
        closeButton.Click += (_, _) => dialog.Close();
        Refresh();
        dialog.ShowDialog();
    }
}
