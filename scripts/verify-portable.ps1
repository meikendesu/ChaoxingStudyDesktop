param([string]$Path = 'artifacts/portable/current', [switch]$AllowUserData)
$ErrorActionPreference = 'Stop'
$releaseRoot = (Resolve-Path -LiteralPath $Path).Path
$files = @(Get-ChildItem -LiteralPath $releaseRoot -Recurse -File -Force)
if ($AllowUserData) {
    # 已部署目录允许用户自己的配置/登录资料；暂存目录和分发包仍使用默认严格检查。
    $files = @($files | Where-Object {
        $relative = $_.FullName.Substring($releaseRoot.Length + 1)
        $relative -notin @('.env','session-cookies.bin','settings.json','task.json','startup-error.log','browser-startup.log') -and $relative -notmatch '^(data|BrowserProfile|secrets)[\\/]'
    })
}
$unwanted = @($files | Where-Object {
    $relative = $_.FullName.Substring($releaseRoot.Length + 1)
    $_.Name -match '^\.env$|^Cookies$|^session-cookies\.bin$|^startup-error\.log$|^browser-startup\.log$' -or
    ($relative -notmatch '^WebView2Runtime[\\/]' -and ($_.Extension -in @('.pdb','.xml','.msi') -or $_.Name -match 'fixture|^study-frame\.html$|^page-agent\.js$|^start\.html$|^catalog(?:-reader)?\.(html|js)$'))
})
if ($unwanted.Count -gt 0) { throw "Release contains private, obsolete or test files: $($unwanted.Name -join ', ')" }
foreach ($required in @('学习通助手.exe', '学习通助手.dll', '学习通助手.runtimeconfig.json', '学习通助手.deps.json', 'Desktop.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Web.WebView2.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'wpfgfx_cor3.dll', 'WebView2Runtime/msedgewebview2.exe', 'WebView2Runtime/runtime-manifest.json', 'Assets/study-agent.js', 'Assets/vendor/Table.json', 'Assets/vendor/Typr.min.js', 'Assets/vendor/Typr.U.min.js', 'Assets/vendor/Typr-LICENSE.txt', 'Assets/vendor/NOTICE.md', '.env.example', '使用说明.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $required) -PathType Leaf)) { throw "Missing portable asset: $required" }
}
$config = Get-Content -LiteralPath (Join-Path $releaseRoot '学习通助手.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
    if (-not ($config.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq $framework -and $_.version -like '10.*' })) { throw "Missing bundled framework: $framework" }
}
if ($config.runtimeOptions.frameworks -or $config.runtimeOptions.framework) { throw 'Release still requires external .NET.' }
if (-not ($files | Where-Object Name -eq 'WebView2Loader.dll')) { throw 'WebView2 SDK loader is missing.' }
$runtime = Join-Path $releaseRoot 'WebView2Runtime'
$manifest = Get-Content -LiteralPath (Join-Path $runtime 'runtime-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.Architecture -ne 'x64' -or $manifest.Files.Count -lt 100) { throw 'Invalid runtime manifest.' }
foreach ($file in $manifest.Files) {
    $target = [IO.Path]::GetFullPath((Join-Path $runtime $file.Path))
    if (-not $target.StartsWith($runtime + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid runtime manifest path.' }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.SHA256) { throw "Runtime file missing or changed: $($file.Path)" }
}
if (@(Get-ChildItem -LiteralPath $runtime -Recurse -File).Count -ne $manifest.Files.Count + 1) { throw 'Unexpected files in runtime folder.' }
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $runtime 'msedgewebview2.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'WebView2 Microsoft signature is invalid.' }
$template = Get-Content -LiteralPath (Join-Path $releaseRoot '.env.example') -Raw
if ($template -notmatch '(?m)^CX_AI_API_KEY=\s*$') { throw 'Configuration template must contain an empty API key.' }
[pscustomobject]@{ Success = $true; Files = $files.Count; SizeMB = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2); DotNet=$config.runtimeOptions.includedFrameworks; WebView2=$manifest.Version; Path = $releaseRoot } | ConvertTo-Json -Depth 4
