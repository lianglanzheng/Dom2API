<#
.SYNOPSIS
将 Dom2API 发布为包含运行时的 Windows x64、x86 和 ARM64 单文件应用。
.DESCRIPTION
使用 Release 配置发布到仓库根目录下的 publish 目录，目标计算机无需安装 .NET 运行时。
文件名为 Dom2API-<标签版本号>-<架构>.exe，不包含提交哈希。
版本号取当前提交历史中最近的 v* 标签，去掉 v 后拼接 + 和当前提交的完整哈希。
托管程序集在单文件中压缩；原生运行库包含在 EXE 中，运行时由 .NET 自动解压。
通过 PathMap 将仓库内源码路径映射到 /_/src，保留调试符号中的文件名和行号。
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'arm64')]
    [string[]]$Architecture = @('x64', 'x86', 'arm64')
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'Dom2API/Dom2API.csproj'
$versionTag = & git -C $PSScriptRoot describe --tags --match 'v*' --abbrev=0 HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    throw '无法获取版本标签：请确保当前提交历史中存在 v 开头的版本标签（例如 v1.2.3），并已拉取相关标签和历史。'
}

$commitHash = & git -C $PSScriptRoot rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) {
    throw '无法获取当前 Git 提交哈希。'
}

$tagVersion = ($versionTag.Trim()).Substring(1)
$version = "$tagVersion+$($commitHash.Trim())"
$publishPath = Join-Path $PSScriptRoot 'publish'
Write-Host "发布版本：$version"

foreach ($targetArchitecture in ($Architecture | Select-Object -Unique)) {
    $targetArchitecture = $targetArchitecture.ToLowerInvariant()
    $runtime = "win-$targetArchitecture"
    $releasePath = Join-Path $publishPath "Dom2API-$tagVersion-$targetArchitecture.exe"
    $publishArguments = @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', $runtime,
        '--self-contained', 'true',
        '--output', $publishPath,
        "-p:Version=$version",
        "-p:InformationalVersion=$version",
        '-p:IncludeSourceRevisionInInformationalVersion=false',
        '-p:PublishAot=false',
        '-p:PublishTrimmed=false',
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded',
        "-p:PathMap=$PSScriptRoot=/_/src",
        '-p:GenerateDocumentationFile=false'
    )

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "发布 $runtime 失败，dotnet 退出代码：$LASTEXITCODE"
    }

    Move-Item -LiteralPath (Join-Path $publishPath 'Dom2API.exe') -Destination $releasePath -Force
    Write-Host "发布完成：$releasePath"
}
