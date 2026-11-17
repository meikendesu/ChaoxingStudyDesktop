param([switch]$Launch)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'artifacts/portable/current'
$localRoot = [Environment]::GetFolderPath('LocalApplicationData')
$destination = Join-Path $localRoot 'Programs/ChaoxingStudyDesktop/current'
$dataRoot = Join-Path $localRoot 'ChaoxingStudyDesktop'
& (Join-Path $PSScriptRoot 'verify-portable.ps1') -Path $source -AllowUserData
if (@(Get-Process -Name 学习通助手 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $destination '学习通助手.exe') }).Count) { throw 'Close the installed client before updating.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# 升级时移除已下线的后台页面；只删除这两个已知生成文件。
foreach ($relative in @('Assets/catalog.html','Assets/catalog-reader.js')) {
    $obsolete = [IO.Path]::GetFullPath((Join-Path $destination $relative))
    if (-not $obsolete.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Obsolete file outside deployment.' }
    if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
}
# 普通复制继承目标用户目录权限，不搬移工作区的低完整性标签。
Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -notin @('data','BrowserProfile','secrets','session-cookies.bin','settings.json','task.json','startup-error.log') } |
    Copy-Item -Destination $destination -Recurse -Force
$oldData = Join-Path $projectRoot 'artifacts/client-data'
$migratedFiles = 0
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'BrowserProfile')) -and (Test-Path -LiteralPath (Join-Path $oldData 'BrowserProfile'))) {
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $backup = Join-Path $dataRoot ('before-migration-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($name in @('settings.json','task.json','session-cookies.bin','secrets')) {
        $existing = Join-Path $dataRoot $name
        if (Test-Path -LiteralPath $existing) { New-Item -ItemType Directory -Path $backup -Force | Out-Null; Copy-Item -LiteralPath $existing -Destination $backup -Recurse }
    }
    foreach ($name in @('BrowserProfile','settings.json','task.json','session-cookies.bin','secrets')) {
        $original = Join-Path $oldData $name
        if (-not (Test-Path -LiteralPath $original)) { continue }
        Copy-Item -LiteralPath $original -Destination $dataRoot -Recurse -Force
        foreach ($file in @(Get-ChildItem -LiteralPath $original -Recurse -File -Force)) {
            $relative = $file.FullName.Substring($oldData.Length + 1)
            $copy = Join-Path $dataRoot $relative
            if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw "User data migration mismatch: $relative" }
            $migratedFiles++
        }
    }
}
& (Join-Path $PSScriptRoot 'verify-portable.ps1') -Path $destination -AllowUserData
if ($Launch) { Start-Process -FilePath (Join-Path $destination '学习通助手.exe') }
[pscustomobject]@{ Success=$true; Deployment=$destination; DataRoot=$dataRoot; MigratedFiles=$migratedFiles; OriginalDataPreserved=$true } | ConvertTo-Json
