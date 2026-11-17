using System.IO;
using System.Windows;
using Desktop.Core;

namespace Desktop.App;

// 首次启动展示的用户协议对话框。只有点击"同意协议"才会返回 Accepted = true。
public partial class AgreementWindow : Window
{
    private readonly string root;

    public bool Accepted { get; private set; }

    public AgreementWindow(string root)
    {
        InitializeComponent();
        this.root = root;
        AgreementText.Text = LoadAgreementText();
    }

    private static string LoadAgreementText()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "用户协议.txt");
        if (File.Exists(path))
        {
            try { return File.ReadAllText(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return "用户协议文本未能加载。本软件仅限个人复习用途，不得用于正式生产、商业运营或任何超出复习范围的场景。";
    }

    private void OnAgree(object sender, RoutedEventArgs e)
    {
        try
        {
            new AgreementStore(root).Accept();
            Accepted = true;
            Close();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show("无法保存协议确认状态，请检查数据目录权限后重试。", "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDecline(object sender, RoutedEventArgs e) => Close();
}
