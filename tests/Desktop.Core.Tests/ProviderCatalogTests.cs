using Desktop.Core;
using Xunit;

namespace Desktop.Core.Tests;

public sealed class ProviderCatalogTests
{
    // 与原脚本 AI_PROVIDERS 的 15 个配置标识逐一对齐，顺序也保持一致。
    private static readonly string[] OriginalIds =
    [
        "zhipu", "deepseek", "deepseek_v4", "qwen", "doubao", "spark", "kimi", "gpt",
        "gemini", "groq", "siliconflow", "yi", "minimax", "stepfun", "baichuan"
    ];

    [Fact]
    public void Catalog_matches_original_provider_ids_and_order()
        => Assert.Equal(OriginalIds, ProviderCatalog.All.Select(provider => provider.Id).ToArray());

    [Fact]
    public void Each_provider_has_original_default_model_endpoint_and_key_hint()
    {
        foreach (var provider in ProviderCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Model), $"{provider.Id} 缺少默认模型");
            Assert.False(string.IsNullOrWhiteSpace(provider.KeyHint), $"{provider.Id} 缺少取 Key 引导");
            Assert.StartsWith("https://", provider.Endpoint, StringComparison.Ordinal);
            // 默认值必须能直接通过 .env 的接口校验，不能带查询参数、片段或用户信息。
            new AiConfiguration { Provider = provider.Id, ApiKey = "fake-only", Model = provider.Model,
                Endpoint = provider.Endpoint, ConfigPath = ".env" }.Validate();
        }
    }

    [Fact]
    public void Zhipu_defaults_match_the_original_script()
    {
        var zhipu = ProviderCatalog.Find("zhipu");
        Assert.NotNull(zhipu);
        Assert.Equal("glm-4-flash", zhipu!.Model);
        Assert.Equal("https://open.bigmodel.cn/api/paas/v4/chat/completions", zhipu.Endpoint);
    }

    [Fact]
    public void Find_and_contains_agree_for_known_and_unknown_ids()
    {
        foreach (var id in OriginalIds)
        {
            Assert.True(ProviderCatalog.Contains(id));
            Assert.Equal(id, ProviderCatalog.Find(id)?.Id);
        }
        Assert.False(ProviderCatalog.Contains("unknown-provider"));
        Assert.Null(ProviderCatalog.Find("unknown-provider"));
        Assert.Null(ProviderCatalog.Find(null));
    }
}
