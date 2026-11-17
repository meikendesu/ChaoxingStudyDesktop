using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class OcrTests
{
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
    [Fact]
    public void Tsv_preserves_lines_and_rejects_uncertain_or_empty_text()
    {
        var result = OcrClient.ParseTsv(Header + "5\t1\t1\t1\t1\t1\t0\t0\t30\t20\t96.5\t学习\n5\t1\t1\t1\t2\t1\t0\t30\t30\t20\t90\t助手\n");
        Assert.True(result.Usable);
        Assert.Equal("学习\n助手", result.Text);
        Assert.False(OcrClient.ParseTsv(Header + "5\t1\t1\t1\t1\t1\t0\t0\t30\t20\t12\t猜测\n").Usable);
        Assert.False(OcrClient.ParseTsv(Header).Usable);
        Assert.False(OcrClient.ParseTsv("garbage").Usable);
    }

    [Fact]
    public void Engine_configuration_is_reread_and_relative_to_env()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var env = Path.Combine(root, ".env");
        try
        {
            File.WriteAllText(env, "CX_OCR_PATH=\"ocr engine/tesseract.exe\"\n");
            Assert.Equal(Path.GetFullPath("ocr engine/tesseract.exe", root), OcrClient.ResolveExecutable(env));
            File.WriteAllText(env, "CX_OCR_PATH=another/tesseract.exe\n");
            Assert.Equal(Path.GetFullPath("another/tesseract.exe", root), OcrClient.ResolveExecutable(env));
        }
        finally { File.Delete(env); Directory.Delete(root); }
    }

    [Fact]
    public async Task Missing_engine_and_cancelled_work_leave_no_image_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "ocr-test-" + Guid.NewGuid().ToString("N"));
        var client = new OcrClient(Path.Combine(root, "missing.exe"), root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RecognizeAsync([1, 2], CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RecognizeAsync([1, 2], new CancellationToken(true)));
        Assert.False(Directory.Exists(root));
    }
}
