namespace Desktop.Core;

// 配置标识与默认模型、接口取自原脚本 AI_PROVIDERS；旧模型名称与免费额度不作承诺，
// 账号实际可用的标识仍以 .env 中用户填写的值为准。
public sealed record ProviderDefinition(string Id, string DisplayName)
{
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string KeyHint { get; init; } = "";
}

public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDefinition> All { get; } = Array.AsReadOnly<ProviderDefinition>([
        new("zhipu", "智谱") { Model = "glm-4-flash", Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions", KeyHint = "智谱开放平台控制台 → API Keys → 创建并复制" },
        new("deepseek", "DeepSeek") { Model = "deepseek-chat", Endpoint = "https://api.deepseek.com/chat/completions", KeyHint = "DeepSeek 开放平台 → API Keys → 创建并复制" },
        new("deepseek_v4", "DeepSeek V4（原脚本配置）") { Model = "deepseek-v4-pro", Endpoint = "https://api.deepseek.com/chat/completions", KeyHint = "与 V3 共用同一 Key，需确认账号已开通该模型" },
        new("qwen", "通义千问") { Model = "qwen-plus", Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", KeyHint = "阿里云百炼 → API-KEY → 创建并复制" },
        new("doubao", "豆包") { Model = "doubao-lite-32k", Endpoint = "https://ark.cn-beijing.volces.com/api/v3/chat/completions", KeyHint = "火山引擎 Ark → API Key 管理 → 创建并开通模型" },
        new("spark", "讯飞星火") { Model = "spark-lite", Endpoint = "https://spark-api-open.xf-yun.com/v1/chat/completions", KeyHint = "讯飞开放平台 → 星火大模型 → 创建应用获取 APIKey" },
        new("kimi", "Kimi") { Model = "moonshot-v1-8k", Endpoint = "https://api.moonshot.cn/v1/chat/completions", KeyHint = "Moonshot 开放平台 → API Key 管理 → 新建并复制" },
        new("gpt", "OpenAI") { Model = "gpt-4o-mini", Endpoint = "https://api.openai.com/v1/chat/completions", KeyHint = "OpenAI 平台 → API Keys → Create new secret key" },
        new("gemini", "Google Gemini") { Model = "gemini-2.0-flash", Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", KeyHint = "Google AI Studio → Get API Key → Create" },
        new("groq", "Groq") { Model = "llama-3.3-70b-versatile", Endpoint = "https://api.groq.com/openai/v1/chat/completions", KeyHint = "Groq 控制台 → API Keys → Create API Key" },
        new("siliconflow", "硅基流动") { Model = "Qwen/Qwen3-8B", Endpoint = "https://api.siliconflow.cn/v1/chat/completions", KeyHint = "硅基流动 → API 密钥 → 创建并复制" },
        new("yi", "零一万物") { Model = "yi-lightning", Endpoint = "https://api.lingyiwanwu.com/v1/chat/completions", KeyHint = "零一万物开放平台 → API Key 管理 → 创建并复制" },
        new("minimax", "MiniMax") { Model = "abab6.5s-chat", Endpoint = "https://api.minimax.chat/v1/text/chatcompletion_v2", KeyHint = "MiniMax 开放平台 → 接口密钥 → 创建并复制" },
        new("stepfun", "阶跃星辰") { Model = "step-1-flash", Endpoint = "https://api.stepfun.com/v1/chat/completions", KeyHint = "阶跃星辰开放平台 → API Key → 创建并复制" },
        new("baichuan", "百川智能") { Model = "Baichuan4", Endpoint = "https://api.baichuan-ai.com/v1/chat/completions", KeyHint = "百川智能开放平台 → API Keys → 创建并复制" }
    ]);

    public static bool Contains(string? id) => All.Any(provider => provider.Id == id);

    public static ProviderDefinition? Find(string? id) => All.FirstOrDefault(provider => provider.Id == id);
}
