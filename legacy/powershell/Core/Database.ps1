Set-StrictMode -Version Latest

function Get-SafePasteDataDirectory {
    $path = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'SafePaste'
    if (-not (Test-Path -LiteralPath $path)) {
        [void](New-Item -ItemType Directory -Path $path -Force)
    }
    return $path
}

function Get-SafePasteSettings {
    $path = Join-Path (Get-SafePasteDataDirectory) 'settings.json'
    $defaults = [ordered]@{ ClipboardClearDelayMs = 800; MaxClipboardChars = 2097152; ShowClipboardWarning = $true }
    if (-not (Test-Path -LiteralPath $path)) { return [pscustomobject]$defaults }
    try {
        $loaded = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($key in $defaults.Keys) {
            if ($null -eq $loaded.$key) { $loaded | Add-Member -NotePropertyName $key -NotePropertyValue $defaults[$key] }
        }
        return $loaded
    } catch { return [pscustomobject]$defaults }
}

function Get-EmptySafePasteDatabase {
    return [pscustomobject]@{ Sensitive = @(); Allow = @() }
}

function Get-SafePasteDatabase {
    $path = Join-Path (Get-SafePasteDataDirectory) 'database.dat'
    if (-not (Test-Path -LiteralPath $path)) { return Get-EmptySafePasteDatabase }
    try {
        $cipher = [IO.File]::ReadAllBytes($path)
        $plain = [Security.Cryptography.ProtectedData]::Unprotect($cipher, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $json = [Text.Encoding]::UTF8.GetString($plain)
        $db = $json | ConvertFrom-Json
        if ($null -eq $db.Sensitive) { $db | Add-Member -NotePropertyName Sensitive -NotePropertyValue @() }
        if ($null -eq $db.Allow) { $db | Add-Member -NotePropertyName Allow -NotePropertyValue @() }
        return $db
    } catch {
        throw 'Не удалось открыть локальную базу SafePaste. Вставка отменена.'
    }
}

function Save-SafePasteDatabase {
    param([Parameter(Mandatory)]$Database)
    $json = $Database | ConvertTo-Json -Depth 4 -Compress
    $plain = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        $cipher = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes((Join-Path (Get-SafePasteDataDirectory) 'database.dat'), $cipher)
    } finally {
        if ($plain) { [Array]::Clear($plain, 0, $plain.Length) }
    }
}

function Add-SafePasteSensitiveValue {
    param([Parameter(Mandatory)][string]$Value, [Parameter(Mandatory)][string]$Type)
    if ($Type -in @('SECRET','TOKEN','PRIVATE_KEY','PASSWORD')) { return }
    $db = Get-SafePasteDatabase
    $exists = @($db.Sensitive | Where-Object { $_.Value -ceq $Value -and $_.Type -eq $Type }).Count -gt 0
    if (-not $exists) {
        $db.Sensitive = @($db.Sensitive) + [pscustomobject]@{ Value = $Value; Type = $Type }
        Save-SafePasteDatabase $db
    }
}

function Add-SafePasteAllowValue {
    param([Parameter(Mandatory)][string]$Value)
    $db = Get-SafePasteDatabase
    if (@($db.Allow | Where-Object { $_ -ceq $Value }).Count -eq 0) {
        $db.Allow = @($db.Allow) + $Value
        Save-SafePasteDatabase $db
    }
}
