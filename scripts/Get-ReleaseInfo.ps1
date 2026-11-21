# Copyright (c) McpSharp contributors
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Resolves which MCP servers to pack and release, and with which versions.

.DESCRIPTION
    Each server under src/ that packs as a .NET tool is a release component named by its
    ToolCommandName (e.g. msbuild-mcp), versioned independently by the <Version> in its
    project file, with release notes in src/<Project>/CHANGELOG.md.

    -Tag <component>-v<version>   Release mode. Fails unless the component exists, the tag
                                  version equals the project's Version, and the project's
                                  CHANGELOG.md has a "## [<version>]" section. Writes that
                                  section to -NotesPath when given.
    -Component <name>|all          Lists one or all components with their project Version,
                                  optionally with -PrereleaseSuffix appended (e.g. ci.42).

    Outputs one object per component: Component, Project, PackageId, Version.

.EXAMPLE
    ./scripts/Get-ReleaseInfo.ps1 -Tag msbuild-mcp-v0.2.0 -NotesPath artifacts/release-notes.md

.EXAMPLE
    ./scripts/Get-ReleaseInfo.ps1 -Component all -PrereleaseSuffix ci.42 | ConvertTo-Json
#>
[CmdletBinding(DefaultParameterSetName = 'Component')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Tag')]
    [string] $Tag,

    [Parameter(ParameterSetName = 'Tag')]
    [string] $NotesPath,

    [Parameter(ParameterSetName = 'Component')]
    [string] $Component = 'all',

    [Parameter(ParameterSetName = 'Component')]
    [string] $PrereleaseSuffix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-Components {
    foreach ($project in Get-ChildItem -Path (Join-Path $repoRoot 'src') -Filter '*.csproj' -Recurse) {
        $json = dotnet msbuild $project.FullName -nologo `
            -getProperty:PackAsTool -getProperty:ToolCommandName -getProperty:PackageId -getProperty:Version
        if ($LASTEXITCODE -ne 0) { throw "Failed to evaluate $($project.FullName)." }
        $props = ($json | Out-String | ConvertFrom-Json).Properties
        if ($props.PackAsTool -ne 'true') { continue }
        [pscustomobject]@{
            Component = $props.ToolCommandName
            Project   = [System.IO.Path]::GetRelativePath($repoRoot, $project.Directory.FullName).Replace('\', '/')
            PackageId = $props.PackageId
            Version   = $props.Version
        }
    }
}

function Get-ChangelogSection([string] $changelogPath, [string] $version) {
    if (-not (Test-Path -LiteralPath $changelogPath)) { throw "Changelog not found: $changelogPath" }
    $lines = Get-Content -LiteralPath $changelogPath
    $heading = '^## \[' + [regex]::Escape($version) + '\]'
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $heading) { $start = $i + 1; break }
    }
    if ($start -lt 0) { throw "No '## [$version]' section in $changelogPath." }
    $end = $lines.Count
    for ($i = $start; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## ' -or $lines[$i] -match '^\[[^\]]+\]:\s') { $end = $i; break }
    }
    # Guard before slicing: PowerShell ranges count down when $end - 1 < $start.
    if ($end -le $start) { throw "The '## [$version]' section in $changelogPath is empty." }
    $section = ($lines[$start..($end - 1)] -join "`n").Trim()
    if (-not $section) { throw "The '## [$version]' section in $changelogPath is empty." }
    $section
}

$components = @(Get-Components | Sort-Object Component)
if ($components.Count -eq 0) { throw 'No packable MCP servers found under src/.' }

if ($PSCmdlet.ParameterSetName -eq 'Tag') {
    if ($Tag -notmatch '^(?<component>[a-z0-9][a-z0-9-]*)-v(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$') {
        throw "Tag '$Tag' does not match <component>-v<major>.<minor>.<patch>[-<prerelease>]."
    }
    $name = $Matches.component
    $version = $Matches.version
    $match = @($components | Where-Object Component -EQ $name)
    if ($match.Count -ne 1) {
        throw "Unknown component '$name' in tag '$Tag'. Components: $($components.Component -join ', ')."
    }
    $server = $match[0]
    if ($server.Version -ne $version) {
        throw "Tag '$Tag' does not match $($server.Project) Version $($server.Version)."
    }
    $notes = Get-ChangelogSection (Join-Path $repoRoot "$($server.Project)/CHANGELOG.md") $version
    if ($NotesPath) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($NotesPath))) | Out-Null
        Set-Content -LiteralPath $NotesPath -Value $notes -Encoding utf8NoBOM
    }
    return $server
}

$selected = if ($Component -eq 'all') { $components } else { @($components | Where-Object Component -EQ $Component) }
if (@($selected).Count -eq 0) {
    throw "Unknown component '$Component'. Components: $($components.Component -join ', ')."
}
foreach ($server in $selected) {
    if ($PrereleaseSuffix) { $server.Version = "$($server.Version)-$PrereleaseSuffix" }
    $server
}
