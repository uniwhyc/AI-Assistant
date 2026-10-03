param([switch]$Test, [switch]$Package)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '找不到 Windows .NET Framework 编译器。请确认已启用 .NET Framework 4.8。' }
$outputRoot = Join-Path $projectRoot 'bin'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$iconPath = Join-Path $projectRoot 'src/app.ico'
if (-not (Test-Path -LiteralPath $iconPath)) {
    # 应用图标缺失时自动用 tools/make-icon.cs 重新生成（深绿圆角底 + 暖橙四角星，与侧栏标识同造型）。
    $iconToolPath = Join-Path $outputRoot 'make-icon.exe'
    & $compiler /nologo /target:exe /utf8output "/out:$iconToolPath" ('/reference:' + (Join-Path $frameworkRoot 'System.Drawing.dll')) (Join-Path $projectRoot 'tools/make-icon.cs')
    if ($LASTEXITCODE -ne 0) { throw '图标生成工具编译失败。' }
    & $iconToolPath $iconPath
    if ($LASTEXITCODE -ne 0) { throw '图标生成失败。' }
}
$references = @('System.dll', 'System.Core.dll', 'System.Net.Http.dll', 'System.Xaml.dll', 'System.Web.Extensions.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot ('WPF/' + $_)) }
$sourceFiles = @('Usage.cs', 'CsvExport.cs', 'App.cs', 'Chart.cs', 'ProviderConfigStore.cs', 'ClaudeProviders.cs', 'TomlSyntax.cs', 'CodexProviders.cs', 'ClaudeProvidersWindow.cs', 'ClaudeModels.cs', 'ClaudeModelsPanel.cs', 'ClaudeModelPresets.cs') | ForEach-Object { Join-Path $projectRoot ('src/' + $_) }
$programPath = Join-Path $outputRoot 'AI Assistant.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 "/out:$programPath" "/resource:$projectRoot/src/MainWindow.xaml,MainWindow.xaml" "/win32manifest:$projectRoot/src/app.manifest" "/win32icon:$iconPath" @references @sourceFiles
if ($LASTEXITCODE -ne 0) { throw '桌面程序编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/AI Assistant.exe.config') -Destination (Join-Path $outputRoot 'AI Assistant.exe.config')
Write-Output "已生成桌面程序：$programPath"
if ($Test) {
    $testPath = Join-Path $outputRoot 'AI Assistant.Tests.exe'
    & $compiler /nologo /target:exe /utf8output /codepage:65001 "/out:$testPath" /reference:System.Web.Extensions.dll (Join-Path $projectRoot 'src/Usage.cs') (Join-Path $projectRoot 'src/CsvExport.cs') (Join-Path $projectRoot 'tests/UsageTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败。' }
    & $testPath
    if ($LASTEXITCODE -ne 0) { throw '统计测试未通过。' }
    $providerTestPath = Join-Path $outputRoot 'AI Assistant.ClaudeProviderTests.exe'
    & $compiler /nologo /target:exe /utf8output /codepage:65001 "/out:$providerTestPath" "/reference:$programPath" @references (Join-Path $projectRoot 'tests/ClaudeProviderTests.cs') (Join-Path $projectRoot 'tests/ClaudeModelsTests.cs') (Join-Path $projectRoot 'tests/CodexProviderTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Claude 配置检查程序编译失败。' }
    Push-Location $projectRoot
    try { & $providerTestPath; if ($LASTEXITCODE -ne 0) { throw 'Claude 配置检查未通过。' } }
    finally { Pop-Location }
    $desktopTestPath = Join-Path $outputRoot 'AI Assistant.DesktopTests.exe'
    & $compiler /nologo /target:exe /utf8output /codepage:65001 "/out:$desktopTestPath" "/reference:$programPath" @references (Join-Path $projectRoot 'tests/DesktopTests.cs')
    if ($LASTEXITCODE -ne 0) { throw '界面检查程序编译失败。' }
    New-Item -ItemType Directory -Path (Join-Path $projectRoot 'artifacts') -Force | Out-Null
    Push-Location $projectRoot
    try { & $desktopTestPath; if ($LASTEXITCODE -ne 0) { throw '界面检查未通过。' } }
    finally { Pop-Location }
}
if ($Package) {
    # 便携版：exe + config + 使用说明，压缩为一个 zip，小白用户解压即可使用，无需源码。
    $releaseDir = Join-Path $projectRoot 'release'
    $packageDir = Join-Path $releaseDir ('Ai小助手-' + (Get-Date -Format 'yyyyMMdd'))
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    Copy-Item -LiteralPath $programPath -Destination (Join-Path $packageDir 'AI Assistant.exe') -Force
    Copy-Item -LiteralPath (Join-Path $outputRoot 'AI Assistant.exe.config') -Destination (Join-Path $packageDir 'AI Assistant.exe.config') -Force
    $guide = @'
Ai 小助手 · 陪你用好每一个 AI

一、使用
1. 双击 AI Assistant.exe 即可使用，无需安装（需要 Windows 10 / 11）。
2. 请保持 AI Assistant.exe 与同目录的 AI Assistant.exe.config 在一起，不要单独移动 exe。
3. 首次打开自动读取本机的 Codex / Claude Code 用量日志；日志不在默认位置时，在侧栏“数据源”中指定目录。
4. 侧栏入口：用量概览（统计与趋势）、会话明细（搜索与导出）、项目统计（按项目聚合排行）、数据源（日志目录）、配置修改（编辑 Codex / Claude Code 配置）。

二、隐私
程序只读用量日志，不联网、不上传数据、不需要 API Key；只有你主动操作时才会写入（数据源设置、保存配置、CSV 导出）。

三、卸载
删除整个文件夹即可，不留系统痕迹。
'@
    [System.IO.File]::WriteAllText((Join-Path $packageDir '使用说明.txt'), $guide, (New-Object System.Text.UTF8Encoding($true)))
    $zip = Join-Path $releaseDir ('AI-Assistant-' + (Get-Date -Format 'yyyyMMdd') + '.zip')
    # 只打包这三个文件：避免把用户运行后生成的 claude-configs、codex-configs
    # 等运行时目录（可能含真实密钥）卷进 zip。
    Compress-Archive -Path (Join-Path $packageDir 'AI Assistant.exe'), (Join-Path $packageDir 'AI Assistant.exe.config'), (Join-Path $packageDir '使用说明.txt') -DestinationPath $zip -Force
    Write-Output "已生成便携版：$zip"
}
