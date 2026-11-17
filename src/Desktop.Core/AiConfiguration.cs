namespace Desktop.Core;

public sealed class AiConfiguration
{
    public string Provider { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string ConfigPath { get; init; } = "";
    public static AiConfiguration Load(string directory)
        => LoadFile(ResolvePath(directory));

    public static string ResolvePath(string directory)
    {
        var local = Path.Combine(Path.GetFullPath(directory), ".env");
        if (File.Exists(local)) return local;
        var cursor = new DirectoryInfo(Path.GetFullPath(directory));
        string? path = null;
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "ChaoxingStudyDesktop.slnx")))
            { path = Path.Combine(cursor.FullName, ".env"); break; }
            cursor = cursor.Parent;
        }
        path ??= Path.Combine(directory, ".env");
        return Path.GetFullPath(path);
    }

    public static AiConfiguration LoadFile(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('#') || line.Length == 0) continue;
                var split = line.IndexOf('=');
                if (split < 1) continue;
                var key = line[..split].Trim();
                var value = line[(split + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))) value = value[1..^1];
                values[key] = value;
            }
        string Get(string key) => values.GetValueOrDefault(key, "");
        return new() { Provider = Get("CX_AI_PROVIDER"), ApiKey = Get("CX_AI_API_KEY"),
            Model = Get("CX_AI_MODEL"), Endpoint = Get("CX_AI_ENDPOINT"), ConfigPath = path };
    }

    // 保留注释及其他环境变量，去除同名重复项；完整写入临时文件后再替换。
    public void Save(string path, bool allowEmptyKey = false)
    {
        var values = new Dictionary<string, string>
        {
            ["CX_AI_PROVIDER"] = Provider, ["CX_AI_API_KEY"] = ApiKey,
            ["CX_AI_MODEL"] = Model, ["CX_AI_ENDPOINT"] = Endpoint
        };
        if (values.Values.Any(v => v.Any(char.IsControl)))
            throw new InvalidOperationException("配置内容不能包含换行或控制字符。");
        if (!allowEmptyKey) Validate();
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        lines.RemoveAll(line => { var split = line.IndexOf('='); return split > 0 && values.ContainsKey(line[..split].Trim()); });
        lines.AddRange(values.Select(pair => $"{pair.Key}=\"{pair.Value}\""));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines, new System.Text.UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Validate()
    {
        var source = string.IsNullOrWhiteSpace(ConfigPath) ? ".env" : ConfigPath;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add("CX_AI_API_KEY");
        if (string.IsNullOrWhiteSpace(Model)) missing.Add("CX_AI_MODEL");
        if (string.IsNullOrWhiteSpace(Endpoint)) missing.Add("CX_AI_ENDPOINT");
        if (missing.Count > 0)
            throw new InvalidOperationException($"AI 配置不完整，缺少 {string.Join("、", missing)}。实际配置文件：{source}。");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException($"CX_AI_ENDPOINT 必须是完整的 HTTPS 接口地址，且不能包含用户信息、查询参数或片段。实际配置文件：{source}。");
    }
}
