param(
    [ValidateSet('restore', 'build', 'test', 'run', 'publish', 'package', 'smoke')]
    [string]$Action = 'build',
    [switch]$Published,
    [string]$OutputDirectory = 'artifacts/portable/current'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    $dotnetCommand = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget/packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $projectRoot '.nuget/http-cache'
    # 某些开发环境禁止写入系统 TEMP，MSBuild 和 NuGet 需要可写的临时目录。
    # 仅构建进程使用项目目录；发布包验收仍在下方恢复用户默认 TEMP/TMP。
    $buildTemp = Join-Path $projectRoot 'artifacts/build-temp'
    New-Item -ItemType Directory -Path $buildTemp -Force | Out-Null
    $env:TEMP = $buildTemp
    $env:TMP = $buildTemp
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:MSBuildEnableWorkloadResolver = 'false'
    $appProject = 'src/Desktop.App/Desktop.App.csproj'
    function Publish-PortableApp {
        $destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
        if (-not $destination.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish destination must stay inside the workspace.' }
        $running = @(Get-Process -Name Desktop.App,学习通助手 -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $destination '学习通助手.exe') })
        if ($running.Count -gt 0) { throw 'Close the client normally before replacing its published files.' }
        if (Test-Path -LiteralPath $destination) {
            if (((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or @(Get-ChildItem -LiteralPath $destination -Recurse -Force -Attributes ReparsePoint).Count -gt 0) { throw 'Publish destination contains links; replacement stopped.' }
        }
        & (Join-Path $PSScriptRoot 'setup-webview2.ps1')
        # 在全新暂存目录生成自包含 .NET，并完整复制微软固定版 WebView2。
        $stage = Join-Path $projectRoot ('artifacts/publish-staging/' + [Guid]::NewGuid().ToString('N'))
        & $dotnetCommand publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:AllowedReferenceRelatedFileExtensions=.pdb -p:DebugType=None -p:DebugSymbols=false -o $stage
        if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
        Copy-Item -LiteralPath (Join-Path $projectRoot '.tools/WebView2Runtime') -Destination (Join-Path $stage 'WebView2Runtime') -Recurse
        & (Join-Path $PSScriptRoot 'verify-portable.ps1') -Path $stage
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        # 仅清理已知生成物，保留 .env、data 和其他用户文件。所有删除均为单文件操作。
        $oldFiles = @(Get-ChildItem -LiteralPath $destination -Recurse -File -Force)
        foreach ($oldFile in $oldFiles) {
            $relative = $oldFile.FullName.Substring($destination.Length + 1)
            if ($relative -match '^(data|BrowserProfile|secrets)[\\/]') { continue }
            $generated = ($relative -notmatch '[\\/]' -and $oldFile.Name -match '\.(dll|pdb)$|^(Desktop\.App|学习通助手)\.(deps|runtimeconfig)\.json$|^Desktop\.App\.exe$|^createdump\.exe$|^Microsoft\.Web\.WebView2\..*\.xml$') -or
                ($relative -match '^(runtimes|cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)[\\/]' -and $oldFile.Extension -eq '.dll') -or
                ($relative -match '^Assets[\\/][^\\/]*(fixture|study-frame)[^\\/]*$') -or
                ($relative -match '^Assets[\\/](page-agent\.js|start\.html|catalog\.html|catalog-reader\.js)$')
            if ($generated -and -not (Test-Path -LiteralPath (Join-Path $stage $relative))) {
                if (-not $oldFile.FullName.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup file outside publish destination.' }
                Remove-Item -LiteralPath $oldFile.FullName -Force
            }
        }
        # 空的运行库/语言目录逐层删除，不递归删除任何目录。
        Get-ChildItem -LiteralPath $destination -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
            $relative = $_.FullName.Substring($destination.Length + 1)
            if ($relative -match '^(runtimes|cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)([\\/]|$)' -and @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $_.FullName }
        }
        Copy-Item -Path (Join-Path $stage '*') -Destination $destination -Recurse -Force
        Copy-Item -LiteralPath (Join-Path $stage '.env.example') -Destination (Join-Path $destination '.env.example') -Force
        & (Join-Path $PSScriptRoot 'verify-portable.ps1') -Path $destination -AllowUserData
    }
    switch ($Action) {
        'restore' { & $dotnetCommand restore ChaoxingStudyDesktop.slnx }
        'build' { & $dotnetCommand build ChaoxingStudyDesktop.slnx -c Release }
        'test' { & $dotnetCommand test tests/Desktop.Core.Tests/Desktop.Core.Tests.csproj -c Release --logger 'console;verbosity=normal' }
        'run' { & $dotnetCommand run --project $appProject -c Release }
        'publish' { Publish-PortableApp }
        'package' {
            Publish-PortableApp
            $zipPath = Join-Path $projectRoot 'artifacts/publish/学习通助手-完整运行时-win-x64.zip'
            New-Item -ItemType Directory -Path (Split-Path $zipPath -Parent) -Force | Out-Null
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
            $packageFiles = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object { $_.Name -notin @('.env','data','BrowserProfile','secrets','session-cookies.bin','settings.json','task.json','startup-error.log','browser-startup.log') } | Select-Object -ExpandProperty FullName)
            Compress-Archive -LiteralPath $packageFiles -DestinationPath $zipPath -CompressionLevel Optimal
            Write-Output "Package: $zipPath"
        }
        'smoke' {
            if ($Published) {
                $smokeExecutable = Join-Path $projectRoot (Join-Path $OutputDirectory '学习通助手.exe')
                if (-not (Test-Path -LiteralPath $smokeExecutable)) { throw 'Publish the application first.' }
                $smokePrefix = ''
            } else {
                & $dotnetCommand build $appProject -c Release
                if ($LASTEXITCODE -ne 0) { throw 'Smoke build failed.' }
                $appDll = Join-Path $projectRoot 'src/Desktop.App/bin/Release/net10.0-windows/学习通助手.dll'
                $smokeExecutable = $dotnetCommand
                $smokePrefix = '"' + $appDll + '" '
            }
            $smokeRoot = Join-Path $projectRoot ('artifacts/smoke/' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
            if ($Published) {
                # 在隔离副本注入测试资源，交付目录不再携带离线测试页面。
                # 工作区可能给生成的 EXE 继承低完整性标签，验收副本放到正常用户程序目录。
                $runner = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('ChaoxingStudyDesktop/Smoke/' + (Split-Path $smokeRoot -Leaf) + '/runner')
                New-Item -ItemType Directory -Path $runner -Force | Out-Null
                & (Join-Path $PSScriptRoot 'verify-portable.ps1') -Path $OutputDirectory -AllowUserData
                Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object { $_.Name -notin @('.env','data','BrowserProfile','secrets','session-cookies.bin','settings.json','task.json','startup-error.log','browser-startup.log') } | Copy-Item -Destination $runner -Recurse -Force
                Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src/Desktop.App/Assets') -File | Where-Object { $_.Name -match 'fixture|^study-frame\.html$|^start\.html$' } | Copy-Item -Destination (Join-Path $runner 'Assets') -Force
                $smokeExecutable = Join-Path $runner '学习通助手.exe'
            }
            foreach ($phase in @('seed', 'verify')) {
                $argumentLine = $smokePrefix + '--smoke-test "' + $smokeRoot + '" ' + $phase
                if ($Published) {
                    # 构建可使用隔离 TEMP；交付验收必须还原双击时的默认 TEMP，避免掩盖解包失败。
                    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
                    $startInfo.FileName = $smokeExecutable
                    $startInfo.Arguments = $argumentLine
                    $startInfo.UseShellExecute = $false
                    $startInfo.CreateNoWindow = $true
                    $startInfo.RedirectStandardError = $true
                    foreach ($name in @('TEMP', 'TMP')) {
                        $defaultValue = [Environment]::GetEnvironmentVariable($name, 'User')
                        if (-not $defaultValue) { $defaultValue = [Environment]::GetEnvironmentVariable($name, 'Machine') }
                        if ($defaultValue) { $startInfo.EnvironmentVariables[$name] = [Environment]::ExpandEnvironmentVariables($defaultValue) }
                        else { $startInfo.EnvironmentVariables.Remove($name) }
                    }
                    $startInfo.EnvironmentVariables.Remove('DOTNET_BUNDLE_EXTRACT_BASE_DIR')
                    # 不使用开发机运行时覆盖，核实包内 .NET 与 WebView2 实际启动。
                    foreach ($name in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_SHARED_STORE','WEBVIEW2_BROWSER_EXECUTABLE_FOLDER','WEBVIEW2_RELEASE_CHANNEL_PREFERENCE')) { $startInfo.EnvironmentVariables.Remove($name) }
                    $startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
                    $smokeProcess = [Diagnostics.Process]::Start($startInfo)
                    $startupError = $smokeProcess.StandardError.ReadToEndAsync()
                } else {
                    $smokeProcess = Start-Process -FilePath $smokeExecutable -ArgumentList $argumentLine -WindowStyle Hidden -PassThru
                }
                if (-not $smokeProcess.WaitForExit(60000)) {
                    Stop-Process -Id $smokeProcess.Id -Force
                    throw "Smoke timed out: $phase"
                }
                $resultPath = Join-Path $smokeRoot "$phase.json"
                if (-not (Test-Path -LiteralPath $resultPath)) {
                    $detail = if ($Published) { $startupError.Result } else { '' }
                    throw "Smoke result missing: $phase; exit=$($smokeProcess.ExitCode); root=$smokeRoot; $detail"
                }
                $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                if (-not $result.Success) { throw "Smoke failed: $($result.Error)" }
                if ($smokeProcess.ExitCode -ne 0) { throw "Smoke process failed with exit code $($smokeProcess.ExitCode)" }
                Write-Output "PASS WebView2 smoke: $phase"
            }
            Write-Output "Smoke artifacts: $smokeRoot"
        }
    }
    if ($Action -ne 'smoke' -and $LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
