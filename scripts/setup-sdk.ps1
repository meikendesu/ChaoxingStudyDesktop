# 将官方 SDK 解压到项目目录；不安装系统服务、不修改系统 PATH。
param([string]$Version = '10.0.402')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^10\.0\.\d+$') { throw 'Only a stable .NET 10 SDK version is accepted.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$toolsRoot = Join-Path $projectRoot '.tools'
$sdkRoot = Join-Path $toolsRoot 'dotnet'
$dotnetPath = Join-Path $sdkRoot 'dotnet.exe'
if ((Test-Path -LiteralPath $dotnetPath) -and (& $dotnetPath --version) -eq $Version) {
    Write-Output "SDK $Version is already available."
    exit 0
}
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $Version } | Select-Object -First 1
$sdkFile = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $sdkFile -or ([Uri]$sdkFile.url).Host -ne 'builds.dotnet.microsoft.com') { throw 'Official SDK download was not found.' }
$archivePath = Join-Path $toolsRoot "dotnet-sdk-$Version-win-x64.zip"
if (-not (Test-Path -LiteralPath $archivePath)) { Invoke-WebRequest $sdkFile.url -OutFile $archivePath }
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ne $sdkFile.hash) {
    throw 'SDK checksum mismatch. Remove the downloaded ZIP and retry.'
}
Expand-Archive -LiteralPath $archivePath -DestinationPath $sdkRoot -Force
& $dotnetPath --version
if ($LASTEXITCODE -ne 0) { throw 'SDK verification failed.' }
