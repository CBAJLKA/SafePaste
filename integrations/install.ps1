[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$Workspace,
    [string[]]$Roots,
    [switch]$Codex,
    [switch]$Claude,
    [string]$SkillsHome,
    [string]$DataDir
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $project 'bin/SafePaste.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build SafePaste.exe first: build.cmd' }
function Get-FullPath($path) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
$workspacePath = Get-FullPath $Workspace
$dataPath = if ($DataDir) { Get-FullPath $DataDir } else { Join-Path $env:LOCALAPPDATA 'SafePaste' }
$useCodex = $Codex -or -not $Claude
$useClaude = $Claude
$promptHook = '"' + $exe + '" --hook prompt'
$toolHook = '"' + $exe + '" --hook tool'
function Write-Utf8($path, $content) {
    $parent = Split-Path $path -Parent
    if ($PSCmdlet.ShouldProcess($path, 'Create configuration')) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
    }
}
function Quote-Toml($value) { return "'" + $value.Replace("'", "''") + "'" }
function Install-Skill($destination) {
    $target = Join-Path $destination 'safepaste-bridge'
    if ($PSCmdlet.ShouldProcess($target, 'Copy skill')) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item -Path (Join-Path $PSScriptRoot 'skills/safepaste-bridge/*') -Destination $target -Recurse -Force
    }
}
# The bridge runs inside the SafePaste tray and reads its folders from bridge.json, the file the
# bridge window edits. Folders are added to the existing list, other settings stay as they are.
function Add-BridgeRoots($folders) {
    $file = Join-Path $dataPath 'bridge.json'
    $settings = [ordered]@{}
    if (Test-Path -LiteralPath $file) {
        $current = [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8) | ConvertFrom-Json
        foreach ($property in $current.PSObject.Properties) { $settings[$property.Name] = $property.Value }
    }
    $list = New-Object 'System.Collections.Generic.List[string]'
    if ($settings.Contains('Roots') -and $settings['Roots']) { foreach ($item in @($settings['Roots'])) { $list.Add([string]$item) } }
    foreach ($folder in $folders) {
        $full = Get-FullPath $folder
        if ($full -match '[;"]') { throw "Folder path must not contain ; or quotes: $full" }
        if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
        if (-not ($list | Where-Object { $_ -ieq $full })) { $list.Add($full) }
    }
    $settings['Roots'] = $list.ToArray()
    if ($PSCmdlet.ShouldProcess($file, 'Add bridge folders')) {
        New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
        [IO.File]::WriteAllText($file, (($settings | ConvertTo-Json -Depth 5) + "`n"), [Text.UTF8Encoding]::new($false))
    }
}
if ($useCodex) {
    # Checked against Codex 0.155 (codex features list): view_image reads local pictures, the browser
    # and computer use see the screen and pages. unified_exec (exec_command) stays on in this version
    # whatever the config says, so the PreToolUse hook below denies every terminal command.
    $config = @(
        '[features]'
        'shell_tool = false'
        'unified_exec = false'
        'view_image = false'
        'browser_use = false'
        'browser_use_external = false'
        'in_app_browser = false'
        'computer_use = false'
        ''
        '# SafePaste.exe --mcp connects to the bridge inside the SafePaste tray and starts SafePaste if needed.'
        '[mcp_servers.safepaste]'
        ('command = ' + (Quote-Toml $exe))
        'args = ["--mcp"]'
        'startup_timeout_sec = 20'
        'tool_timeout_sec = 660'
        'default_tools_approval_mode = "auto"'
    )
    foreach ($server in @('local_qwen', 'node_repl', 'unityMCP', 'blender')) {
        $config += ''
        $config += '[mcp_servers.' + $server + ']'
        $config += 'enabled = false'
    }
    Write-Utf8 (Join-Path $workspacePath '.codex/config.toml') (($config -join "`n") + "`n")
    $hooks = @{ hooks = @{
        UserPromptSubmit = @(@{ hooks = @(@{ type = 'command'; command = $promptHook; timeout = 10 }) })
        PreToolUse = @(@{ hooks = @(@{ type = 'command'; command = $toolHook; timeout = 10 }) })
    } } | ConvertTo-Json -Depth 10
    Write-Utf8 (Join-Path $workspacePath '.codex/hooks.json') ($hooks + "`n")
    Write-Utf8 (Join-Path $workspacePath 'AGENTS.md') ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AGENTS.safepaste.md'), [Text.Encoding]::UTF8))
    $skills = if ($SkillsHome) { $SkillsHome } else { Join-Path $HOME '.codex/skills' }
    Install-Skill $skills
}
if ($useClaude) {
    $mcp = @{ mcpServers = @{ safepaste = @{ command = $exe; args = @('--mcp'); timeout = 660000 } } } | ConvertTo-Json -Depth 10
    Write-Utf8 (Join-Path $workspacePath '.mcp.json') ($mcp + "`n")
    $deny = @('Bash', 'PowerShell', 'Read', 'Edit', 'Write', 'Glob', 'Grep', 'NotebookEdit', 'WebFetch',
        'mcp__local-qwen', 'mcp__unityMCP', 'mcp__terminal', 'mcp__Claude_Browser', 'mcp__claude-in-chrome')
    $settings = @{ permissions = @{ deny = $deny; allow = @('mcp__safepaste') }; hooks = @{
        UserPromptSubmit = @(@{ hooks = @(@{ type = 'command'; command = $promptHook; timeout = 10 }) })
        PreToolUse = @(@{ hooks = @(@{ type = 'command'; command = $toolHook; timeout = 10 }) })
    } } | ConvertTo-Json -Depth 10
    Write-Utf8 (Join-Path $workspacePath '.claude/settings.json') ($settings + "`n")
    $skills = if ($SkillsHome) { $SkillsHome } else { Join-Path $HOME '.claude/skills' }
    Install-Skill $skills
}
if ($Roots) { Add-BridgeRoots $Roots }
Write-Output 'SafePaste bridge workspace configured.'
Write-Output 'Bridge folders, permissions and status are managed from the SafePaste tray menu.'
