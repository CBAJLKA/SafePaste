$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ('SafePasteBridge-' + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data'
$files = Join-Path $root 'files'
$emptyData = Join-Path $root 'no-bridge'
New-Item -ItemType Directory -Path $data, $files, $emptyData -Force | Out-Null
$sample = Join-Path $files 'note.txt'
[IO.File]::WriteAllText($sample, (("server=fs01.corp.example`naddress=10.44.7.219`n") * 12), [Text.UTF8Encoding]::new($false))
$sub = Join-Path $files 'sub'
New-Item -ItemType Directory -Path $sub -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $sub 'inside.txt'), 'server=fs01.corp.example', [Text.UTF8Encoding]::new($false))
# Bridge settings live in bridge.json next to the other SafePaste data, as the bridge window writes them.
$bridgeSettings = [ordered]@{ Enabled = $true; Roots = @($files); PageChars = 200 } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $data 'bridge.json'), $bridgeSettings, [Text.UTF8Encoding]::new($false))
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin/SafePaste.exe'
function Start-SafePaste($arguments, $environment) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.CreateNoWindow = $true
    if ($environment) { foreach ($key in $environment.Keys) { $info.EnvironmentVariables[$key] = $environment[$key] } }
    return [Diagnostics.Process]::Start($info)
}
function Read-Line($process, $what) {
    $pending = $process.StandardOutput.ReadLineAsync()
    if (-not $pending.Wait(15000)) { throw "Timeout: $what" }
    return $pending.Result
}
function Stop-SafePaste($process) {
    if (-not $process) { return }
    try { $process.StandardInput.Close() } catch { }
    if (-not $process.WaitForExit(10000)) { $process.Kill() }
    $process.Dispose()
}
# One initialize through a connector that is expected to refuse; returns the error text.
function Get-Refusal($arguments, $environment) {
    $connector = Start-SafePaste $arguments $environment
    try {
        $connector.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}')
        $connector.StandardInput.Flush()
        $answer = Read-Line $connector "refusal for $arguments" | ConvertFrom-Json
        if (-not $answer.error) { throw "Connector did not refuse: $arguments" }
        return $answer.error.message
    } finally { Stop-SafePaste $connector }
}
$bridgeHost = $null
$process = $null
try {
    # In everyday use the bridge lives in the SafePaste tray; --bridge-host runs the same host without the tray.
    $bridgeHost = Start-SafePaste ('--bridge-host --data-dir "' + $data + '"')
    if ((Read-Line $bridgeHost 'bridge host start') -ne 'ready') { throw 'Bridge host did not start' }
    $process = Start-SafePaste ('--mcp --data-dir "' + $data + '"')
    $script:id = 0
    function Call($method, $parameters) {
        $script:id++
        $request = @{ jsonrpc = '2.0'; id = $script:id; method = $method; params = $parameters } | ConvertTo-Json -Depth 10 -Compress
        $process.StandardInput.WriteLine($request)
        $process.StandardInput.Flush()
        $line = Read-Line $process "MCP $method id=$script:id"
        if (-not $line) { throw 'No MCP response' }
        return $line | ConvertFrom-Json
    }
    function Tool($name, $arguments) {
        $result = Call 'tools/call' @{ name = $name; arguments = $arguments }
        if ($result.error -or $result.result.isError) { throw "Tool $name failed: $($result | ConvertTo-Json -Depth 10)" }
        $text = $result.result.content[0].text
        if ($text -match 'fs01\.corp\.example|10\.44\.7\.219|private marker alpha') { throw "Raw value in $name response" }
        return $text
    }
    function Hook($mode, $request) {
        $hookProcess = Start-SafePaste ('--data-dir "' + $data + '" --hook ' + $mode)
        try {
            $hookProcess.StandardInput.Write($request)
            $hookProcess.StandardInput.Close()
            $pending = $hookProcess.StandardOutput.ReadToEndAsync()
            if (-not $pending.Wait(10000)) { throw "Hook timeout: $mode" }
            if (-not $hookProcess.WaitForExit(5000)) { throw "Hook did not exit: $mode" }
            if ($hookProcess.ExitCode -ne 0) { throw "Hook failed: $mode" }
            return $pending.Result
        } finally {
            if (-not $hookProcess.HasExited) { $hookProcess.Kill() }
            $hookProcess.Dispose()
        }
    }
    $null = Call 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'smoke'; version = '1' } }
    $tools = Call 'tools/list' @{}
    if ($tools.result.tools.Count -ne 10) { throw 'Wrong tool count' }
    # PageChars = 200 from bridge.json splits even the status into pages.
    $status = Tool 'sp_status' @{}
    $more = [regex]::Match($status, 'sp_next id=(\d+)')
    if (-not $more.Success) { throw 'sp_status ignored the page size from bridge.json' }
    while ($more.Success) {
        $page = Tool 'sp_next' @{ id = [int]$more.Groups[1].Value }
        $status += $page
        $more = [regex]::Match($page, 'sp_next id=(\d+)')
    }
    if ($status -notmatch ': 200 ') { throw 'sp_status does not show the page size from bridge.json' }
    $null = Tool 'sp_list' @{ path = $files }
    $read = Tool 'sp_read' @{ path = $sample }
    $continuation = [regex]::Match($read, 'sp_next id=(\d+)')
    if (-not $continuation.Success) { throw 'No read continuation' }
    $next = Tool 'sp_next' @{ id = [int]$continuation.Groups[1].Value }
    $read += $next
    if ($read -match 'fs01.corp.example|10\.44\.7\.219') { throw 'Raw value escaped in sp_read' }
    $label = [regex]::Match($read, '\[FQDN_\d+\]').Value
    if (-not $label) { throw 'No FQDN label' }
    $find = Tool 'sp_find' @{ path = $files; pattern = $label }
    if ($find -notmatch 'note.txt') { throw 'sp_find did not resolve label' }
    $run = Tool 'sp_run' @{ command = "Write-Output '$label'"; wait_sec = 10 }
    if ($run -notmatch [regex]::Escape($label) -or $run -match 'fs01.corp.example') { throw 'sp_run did not preserve label' }
    $bare = Tool 'sp_run' @{ command = "Write-Output 'fs01.corp.example'"; wait_sec = 10 }
    if ($bare -notmatch [regex]::Escape($label)) { throw 'Learned value was not hidden' }
    $hidden = Tool 'sp_hide' @{ value = 'private marker alpha'; type = 'TEXT' }
    if ($hidden -notmatch '\[TEXT_\d+\]') { throw 'sp_hide failed' }
    $afterHide = Tool 'sp_run' @{ command = "Write-Output 'private marker alpha'"; wait_sec = 10 }
    if ($afterHide -notmatch [regex]::Escape($hidden)) { throw 'sp_hide did not affect later output' }
    $null = Tool 'sp_run' @{ command = "Set-Location '$sub'"; wait_sec = 10 }
    $relative = Tool 'sp_read' @{ path = 'inside.txt' }
    if ($relative -notmatch [regex]::Escape($label)) { throw 'Working directory was not preserved' }
    $denied = Call 'tools/call' @{ name = 'sp_run'; arguments = @{ command = 'Get-ChildItem Env:' } }
    if (-not $denied.result.isError) { throw 'Env: was allowed' }
    $outside = Call 'tools/call' @{ name = 'sp_read'; arguments = @{ path = (Join-Path $data 'bridge.json') } }
    if (-not $outside.result.isError) { throw 'A file outside the bridge folders was read' }
    # Settings changed in the bridge window apply to connected agents at once.
    $settings = Get-Content -LiteralPath (Join-Path $data 'bridge.json') -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName AllowFull -NotePropertyValue $false -Force
    [IO.File]::WriteAllText((Join-Path $data 'bridge.json'), ($settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $full = Call 'tools/call' @{ name = 'sp_run'; arguments = @{ command = 'Write-Output 1'; profile = 'full' } }
    if (-not $full.result.isError -or $full.result.content[0].text -notmatch 'PowerShell.+SafePaste') { throw 'Full profile ignored bridge.json' }
    $prompt = Hook 'prompt' '{"prompt":"server=10.44.7.219"}' | ConvertFrom-Json
    if ($prompt.decision -ne 'block' -or $prompt.reason -match '10\.44\.7\.219') { throw 'Prompt hook failed' }
    $rawPrompt = Hook 'prompt' '{"prompt":"!raw server=10.44.7.219"}'
    if ($rawPrompt) { throw '!raw did not pass' }
    $toolHook = Hook 'tool' '{"tool_name":"Bash"}' | ConvertFrom-Json
    if ($toolHook.hookSpecificOutput.permissionDecision -ne 'deny') { throw 'Tool hook failed' }
    $allowedTool = Hook 'tool' '{"tool_name":"mcp__safepaste__sp_run"}'
    if ($allowedTool) { throw 'SafePaste tool was denied' }
    # Codex 0.155 cannot switch exec_command off, so the hook has to deny every terminal command.
    $terminal = Hook 'tool' '{"tool_name":"exec_command"}' | ConvertFrom-Json
    if ($terminal.hookSpecificOutput.permissionDecision -ne 'deny') { throw 'Codex terminal was allowed' }
    $filterProcess = Start-SafePaste ('--filter --data-dir "' + $data + '"')
    try {
        $filterProcess.StandardInput.Write('server fs01.corp.example')
        $filterProcess.StandardInput.Close()
        $filtered = $filterProcess.StandardOutput.ReadToEnd()
        if (-not $filterProcess.WaitForExit(10000) -or $filterProcess.ExitCode -ne 0) { throw 'Filter failed' }
        if ($filtered -match 'fs01\.corp\.example' -or $filtered -notmatch '\[FQDN_\d+\]') { throw 'Filter leaked value' }
    } finally {
        if (-not $filterProcess.HasExited) { $filterProcess.Kill() }
        $filterProcess.Dispose()
    }
    # Old connector settings are refused instead of silently widening access.
    $legacy = Get-Refusal ('--mcp --data-dir "' + $data + '" --roots "' + $files + '"') $null
    if ($legacy -notmatch '--roots') { throw 'Legacy --roots was accepted' }
    $legacyEnv = Get-Refusal ('--mcp --data-dir "' + $data + '"') @{ SAFEPASTE_ROOTS = $files }
    if ($legacyEnv -notmatch 'SAFEPASTE_ROOTS') { throw 'Legacy SAFEPASTE_ROOTS was accepted' }
    $missing = Get-Refusal ('--mcp --data-dir "' + $emptyData + '"') $null
    if ($missing -notmatch 'SafePaste') { throw 'Connector without a bridge did not explain the problem' }
} finally {
    Stop-SafePaste $process
    Stop-SafePaste $bridgeHost
    $safeRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $safeRoot.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($safeRoot) -match '^SafePasteBridge-[0-9a-f]{32}$')) { throw 'Unsafe cleanup path' }
    Remove-Item -LiteralPath $safeRoot -Recurse -Force
}
Write-Output 'Bridge smoke passed.'
