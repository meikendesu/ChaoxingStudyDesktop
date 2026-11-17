param([switch]$Execute, [switch]$IncludeCurrent)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactRoot = Join-Path $workspace 'artifacts'
if (@(Get-Process -Name Desktop.App,学习通助手 -ErrorAction SilentlyContinue).Count) { throw 'Close clients normally before cleanup.' }
$protected = @('client-data','preserved-user-data','verification-20261101') | ForEach-Object { Join-Path $artifactRoot $_ }
$candidates = @('publish-staging','smoke','controls-investigation','page-investigation','installed-app','installer-build') |
    ForEach-Object { Join-Path $artifactRoot $_ }
if (Test-Path -LiteralPath (Join-Path $artifactRoot 'portable')) {
    $candidates += @(Get-ChildItem -LiteralPath (Join-Path $artifactRoot 'portable') -Directory | Where-Object { $IncludeCurrent -or $_.Name -ne 'current' } | Select-Object -ExpandProperty FullName)
}
if (Test-Path -LiteralPath (Join-Path $artifactRoot 'publish')) {
    $candidates += @(Get-ChildItem -LiteralPath (Join-Path $artifactRoot 'publish') -Force | Where-Object {
        $IncludeCurrent -or $_.Name -ne '学习通助手-完整运行时-win-x64.zip'
    } | Select-Object -ExpandProperty FullName)
}
$plan = @()
foreach ($candidate in $candidates) {
    if (-not (Test-Path -LiteralPath $candidate)) { continue }
    $target = (Resolve-Path -LiteralPath $candidate).Path
    if (-not $target.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Outside artifacts: $target" }
    foreach ($keep in $protected) {
        if ($target -eq $keep -or $target.StartsWith($keep + '\', [StringComparison]::OrdinalIgnoreCase) -or $keep.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Protected path: $target" }
    }
    $item = Get-Item -LiteralPath $target -Force
    $children = if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $target -Recurse -Force) } else { @() }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or @($children | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) { throw "Links found: $target" }
    $files = if ($item.PSIsContainer) { @($children | Where-Object { -not $_.PSIsContainer }) } else { @($item) }
    $plan += [pscustomobject]@{ Path=$target; Files=$files.Count; Bytes=($files | Measure-Object Length -Sum).Sum }
}
$backup = Join-Path $artifactRoot ('preserved-user-data/deployment-cleanup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if ($Execute) {
    # 除显式离线 smoke 假资料外，先逐文件复制并核对所有 Cookie、配置和旧密钥。
    $preserved = @()
    foreach ($entry in $plan) {
        if ($entry.Path -eq (Join-Path $artifactRoot 'smoke')) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $entry.Path -Recurse -File -Force)) {
            $relative = $file.FullName.Substring($artifactRoot.Length + 1)
            if ($file.Name -notin @('.env','session-cookies.bin','settings.json','task.json') -and $relative -notmatch '(^|[\\/])(BrowserProfile|secrets)([\\/]|$)') { continue }
            $destination = Join-Path $backup $relative
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Backup mismatch: $relative" }
            $preserved += [pscustomobject]@{ Path=$relative; SHA256=$hash }
        }
    }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $preserved | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backup 'preservation-manifest.json') -Encoding utf8
    foreach ($entry in $plan) {
        # 上方已验证绝对路径边界、受保护目录、链接及备份哈希。
        Remove-Item -LiteralPath $entry.Path -Recurse -Force
    }
}
[pscustomobject]@{ Executed=[bool]$Execute; IncludeCurrent=[bool]$IncludeCurrent; Backup=$backup; Targets=$plan } | ConvertTo-Json -Depth 5
