# 字体解析资源

仅在用户请求 AI 答案、且页面使用混淆字体时加载。课程字体直接从当前平台页面声明的 data/超星 HTTPS 地址读取；不访问第三方题库，不修改网页文字。

| 文件 | 固定来源 | SHA-256 |
| --- | --- | --- |
| Typr.min.js | photopea/Typr.js 提交 `15aa12ffa6cf39e8788562ea4af65b42317375fb`，jsDelivr minified | `9F697390B0C605B227974FAD85DC5C66833EBCFBAC72DFFBC776F5888F73334A` |
| Typr.U.min.js | photopea/Typr.js 提交 `f4fcdeb8014edc75ab7296bd85ac9cde8cb30489`，jsDelivr minified | `5D33C1C0A81D69A635806A3650871E6C6EAA34E9BF5DD5EA9828ACD7F47B15D6` |
| Table.json | 原脚本指定 `https://chizhu-dl.github.io/font-decrypt/Table.json`，2026-10-21 固定快照 | `A7F0D85F12BC21094A976381E3E10C06C06AF4C97BB26CA70DFEFB8C0E10891F` |

Typr 使用 MIT 许可，保留 `Typr-LICENSE.txt`。字形映射表上游未声明独立许可证，当前用于用户已有脚本的本地兼容验证；对外分发前需核对其使用条件。

资源已经纳入源码，无须 npm/NuGet 安装。更新时须重新核验来源、哈希和字形序列化兼容性，不能直接替换为最新 Typr 版本。
