param([string]$Version = '156.0.4314.58')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $projectRoot '.tools/WebView2Runtime'
if (Test-Path -LiteralPath (Join-Path $destination 'runtime-manifest.json')) {
    $saved = Get-Content -LiteralPath (Join-Path $destination 'runtime-manifest.json') -Raw | ConvertFrom-Json
    if ($saved.Version -ne $Version) { throw 'Cached WebView2 version differs; use a separate cache before upgrading.' }
    Write-Output "WebView2 cache: $destination"
    return
}
if (Test-Path -LiteralPath $destination) { throw 'Incomplete WebView2 cache found; inspect it before replacing.' }
$metadata = Invoke-RestMethod -Uri 'https://developer.microsoft.com/microsoft-edge/api/webview2'
$release = $metadata | Where-Object version -eq $Version | Select-Object -First 1
$build = $release.builds | Where-Object architecture -eq 'x64' | Select-Object -First 1
if (-not $build -or ([uri]$build.url).Host -ne 'msedge.sf.dl.delivery.mp.microsoft.com') { throw 'Requested x64 runtime is unavailable from Microsoft.' }
$cache = Join-Path $projectRoot '.tools/webview2-download'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$cab = Join-Path $cache "Microsoft.WebView2.FixedVersionRuntime.$Version.x64.cab"
if (-not (Test-Path -LiteralPath $cab)) { Invoke-WebRequest -Uri $build.url -OutFile $cab }
$expanded = Join-Path $cache "Microsoft.WebView2.FixedVersionRuntime.$Version.x64"
if (-not (Test-Path -LiteralPath (Join-Path $expanded 'msedgewebview2.exe'))) {
    & expand.exe $cab '-F:*' $cache | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'WebView2 CAB extraction failed.' }
}
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $expanded 'msedgewebview2.exe')
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'Invalid Microsoft runtime signature.' }
if ((Get-Item -LiteralPath (Join-Path $expanded 'msedgewebview2.exe')).VersionInfo.FileVersion -ne $Version) { throw 'Unexpected runtime version.' }
Copy-Item -LiteralPath $expanded -Destination $destination -Recurse
$files = @(Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path=$_.FullName.Substring($destination.Length + 1); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[pscustomobject]@{ Version=$Version; Architecture='x64'; Source=$build.url; CabSHA256=(Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash; Files=$files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'runtime-manifest.json') -Encoding utf8
Write-Output "Verified Microsoft WebView2 $Version x64: $destination"
