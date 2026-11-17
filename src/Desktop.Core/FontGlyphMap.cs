using System.Security.Cryptography;
using System.Text;

namespace Desktop.Core;

public static class FontGlyphMap
{
    public static Dictionary<int, string> Decode(IReadOnlyDictionary<int, string> paths, IReadOnlyDictionary<string, int> table)
    {
        var map = new Dictionary<int, string>();
        foreach (var (code, path) in paths)
        {
            // 与原脚本 MD5(JSON.stringify(glyphPath)).slice(24) 完全一致；仅作字形指纹。
            var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(path)))[24..];
            if (table.TryGetValue(hash, out var real) && real is >= 0 and <= 0x10FFFF && real is not (>= 0xD800 and <= 0xDFFF))
                map[code] = char.ConvertFromUtf32(real);
        }
        return map;
    }
}
