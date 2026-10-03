Set-StrictMode -Version Latest

$script:SafePasteRegexCache = @{}

function New-SafePasteMatch {
    param(
        [Parameter(Mandatory)][int]$Start,
        [Parameter(Mandatory)][int]$Length,
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][string]$Confidence,
        [Parameter(Mandatory)][int]$Priority,
        [string]$Source = 'Regex',
        [bool]$Locked = $false,
        [bool]$Manual = $false
    )
    if ($Length -le 0) { return $null }
    return [pscustomobject]@{
        Start = $Start; Length = $Length; Value = $Value; Type = $Type; Confidence = $Confidence
        Priority = $Priority; Source = $Source; Locked = $Locked; Manual = $Manual; Enabled = $true; Placeholder = ''
    }
}

function Add-SafePasteMatch {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)]$Match)
    if ($null -ne $Match -and $State.Keys.Add("$($Match.Start):$($Match.Length):$($Match.Type)")) { [void]$State.Items.Add($Match) }
}

function Get-SafePasteRegexMatches {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Pattern)
    try {
        if (-not $script:SafePasteRegexCache.ContainsKey($Pattern)) {
            $script:SafePasteRegexCache[$Pattern] = [regex]::new($Pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromMilliseconds(350))
        }
        $regex = $script:SafePasteRegexCache[$Pattern]
        return $regex.Matches($Text)
    } catch [Text.RegularExpressions.RegexMatchTimeoutException] {
        throw 'Один из детекторов превысил лимит времени. Вставка отменена.'
    }
}

function Add-SafePasteRegexCandidates {
    param(
        [Parameter(Mandatory)]$State, [Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Type, [Parameter(Mandatory)][string]$Confidence, [Parameter(Mandatory)][int]$Priority,
        [string]$Group = '', [bool]$Locked = $false, [string]$Source = 'Regex'
    )
    foreach ($hit in (Get-SafePasteRegexMatches $Text $Pattern)) {
        $part = if ($Group) { $hit.Groups[$Group] } else { $hit }
        if ($part.Success -and $part.Length -gt 0) {
            Add-SafePasteMatch $State (New-SafePasteMatch -Start $part.Index -Length $part.Length -Value $part.Value -Type $Type -Confidence $Confidence -Priority $Priority -Locked $Locked -Source $Source)
        }
    }
}

function Add-SafePasteIPv6Candidates {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)][string]$Text)
    $pattern = '(?<![A-Z0-9:])(?=[0-9A-F:]*[0-9A-F])(?<value>(?:[0-9A-F]{0,4}:){2,7}[0-9A-F]{0,4})(?![A-Z0-9:])'
    foreach ($hit in (Get-SafePasteRegexMatches $Text $pattern)) {
        $part = $hit.Groups['value']
        # MAC распознаётся отдельной категорией, а не как IPv6.
        if ($part.Value -match '^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$') { continue }
        $address = $null
        if ([Net.IPAddress]::TryParse($part.Value, [ref]$address) -and $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
            Add-SafePasteMatch $State (New-SafePasteMatch -Start $part.Index -Length $part.Length -Value $part.Value -Type 'IPV6' -Confidence 'High' -Priority 70)
        }
    }
}

function Get-SafePasteCandidates {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)]$Database)
    $state = [pscustomobject]@{ Items = (New-Object 'System.Collections.Generic.List[object]'); Keys = (New-Object 'System.Collections.Generic.HashSet[string]') }

    # Обязательные секреты. Сохраняется только оперативная память текущей операции.
    Add-SafePasteRegexCandidates $state $Text '(?ms)-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----.*?-----END(?: [A-Z0-9]+)? PRIVATE KEY-----' 'PRIVATE_KEY' 'High' 100 '' $true
    Add-SafePasteRegexCandidates $state $Text '(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?![A-Za-z0-9_-])' 'TOKEN' 'High' 98 '' $true
    Add-SafePasteRegexCandidates $state $Text '\b(?:password|passwd|pwd|token|secret|api[_-]?key)\b\s*[:=]\s*(?<value>"[^"]*"|''[^'']*''|[^\s;,&]+)' 'SECRET' 'High' 100 'value' $true
    Add-SafePasteRegexCandidates $state $Text '\bAuthorization\s*:\s*(?:Bearer|Basic)\s+(?<value>[^\s,;]+)' 'TOKEN' 'High' 100 'value' $true

    # Идентификаторы с устойчивыми форматами.
    Add-SafePasteRegexCandidates $state $Text '(?<![A-Z0-9_])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![A-Z0-9_])' 'IP' 'High' 70
    Add-SafePasteIPv6Candidates -State $state -Text $Text
    Add-SafePasteRegexCandidates $state $Text '(?<![0-9A-F])(?:[0-9A-F]{2}[:-]){5}[0-9A-F]{2}(?![0-9A-F])' 'MAC' 'High' 72
    Add-SafePasteRegexCandidates $state $Text '(?<!\w)S-\d+(?:-\d+){2,15}(?!\w)' 'SID' 'High' 70
    Add-SafePasteRegexCandidates $state $Text '(?<![A-F0-9])[{(]?[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12}[)}]?(?![A-F0-9])' 'GUID' 'High' 70
    Add-SafePasteRegexCandidates $state $Text '(?<![\w.+-])[\w.+-]+@[A-Z0-9-]+(?:\.[A-Z0-9-]+)+(?![\w.-])' 'EMAIL' 'High' 82
    Add-SafePasteRegexCandidates $state $Text '\b(?:https?|ftp)://[^\s<>"'']+' 'URL' 'High' 80
    Add-SafePasteRegexCandidates $state $Text '(?<![A-Z0-9-])(?:[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\.)+(?:[A-Z]{2,63})(?![A-Z0-9-])' 'FQDN' 'High' 75

    # Базовые идентификаторы администраторского стека.
    Add-SafePasteRegexCandidates $state $Text '(?<![\w=])(?:CN|OU|DC)=[^,;\r\n]+(?:,(?:CN|OU|DC)=[^,;\r\n]+)+(?![\w=])' 'AD_DN' 'High' 86
    Add-SafePasteRegexCandidates $state $Text '\biqn\.\d{4}-\d{2}\.[A-Z0-9.-]+(?::[A-Z0-9._-]+)?\b' 'ISCSI_IQN' 'High' 76
    Add-SafePasteRegexCandidates $state $Text '\b(?:WWN|WWPN|World\s+Wide\s+Name)\s*[:=]?\s*(?<value>(?:0X)?[A-F0-9]{16})\b' 'WWN' 'High' 76 'value'
    Add-SafePasteRegexCandidates $state $Text '(?<![A-F0-9])(?:[A-F0-9]{2}:){7}[A-F0-9]{2}(?![A-F0-9])' 'WWN' 'High' 76
    Add-SafePasteRegexCandidates $state $Text '\b(?:certificate\s+)?thumbprint\s*[:=]\s*(?<value>[A-F0-9]{40}(?:[A-F0-9]{24})?)\b' 'CERT_THUMBPRINT' 'High' 76 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:vm|host|datastore|network|domain-c|group)-\d+\b' 'VMWARE_MOREF' 'Medium' 64
    Add-SafePasteRegexCandidates $state $Text '\b(?:AKIA|ASIA|AGPA|AIDA|AROA)[A-Z0-9]{16}\b' 'AWS_ACCESS_KEY' 'High' 96 '' $true
    Add-SafePasteRegexCandidates $state $Text '\btenant\s*(?:id)?\s*[:=]\s*(?<value>[{(]?[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12}[)}]?)' 'AZURE_TENANT' 'High' 78 'value'
    Add-SafePasteRegexCandidates $state $Text '\bsubscription\s*(?:id)?\s*[:=]\s*(?<value>[{(]?[A-F0-9]{8}-(?:[A-F0-9]{4}-){3}[A-F0-9]{12}[)}]?)' 'AZURE_SUBSCRIPTION' 'High' 78 'value'
    Add-SafePasteRegexCandidates $state $Text '\bssh-(?:rsa|ed25519|ecdsa(?:-[A-Z0-9-]+)?)\s+(?<value>[A-Z0-9+/=]{32,})' 'SSH_PUBLIC_KEY' 'High' 78 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:ssh\s+)?fingerprint\s*[:=]\s*(?<value>(?:SHA256:[A-Z0-9+/=]{16,}|(?:[A-F0-9]{2}:){15,31}[A-F0-9]{2}))' 'SSH_FINGERPRINT' 'High' 76 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:snmp\s+community)\s*[:=]?\s*(?<value>"[^"]*"|''[^'']*''|[^\s,;]+)' 'SECRET' 'High' 100 'value' $true
    Add-SafePasteRegexCandidates $state $Text '\b(?:product\s+key|kms\s+key|windows\s+key)\s*[:=]?\s*(?<value>[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4})\b' 'SECRET' 'High' 100 'value' $true
    Add-SafePasteRegexCandidates $state $Text '\bDSN\s*[:=]\s*(?<value>[A-Z0-9][A-Z0-9._-]{1,127})\b' 'DSN' 'Medium' 65 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:RDP\s+gateway|VPN\s+(?:remote|gateway)|gateway)\s*[:=]\s*(?<value>[A-Z0-9][A-Z0-9.-]{1,253})\b' 'HOST' 'Medium' 65 'value'

    # UNC хранит узел и ресурс раздельно: \\[HOST_n]\[SHARE_n].
    foreach ($hit in (Get-SafePasteRegexMatches $Text '(?<!\\)\\\\(?<host>[A-Z0-9][A-Z0-9-]{0,62})\\(?<share>[^\\\s]+)')) {
        $uncHost = $hit.Groups['host']; $share = $hit.Groups['share']
        Add-SafePasteMatch $state (New-SafePasteMatch $uncHost.Index $uncHost.Length $uncHost.Value 'HOST' 'High' 74 'UNC')
        Add-SafePasteMatch $state (New-SafePasteMatch $share.Index $share.Length $share.Value 'SHARE' 'High' 72 'UNC')
    }

    # Короткие имена и serial — только при явном контексте.
    Add-SafePasteRegexCandidates $state $Text '(?<!Windows\s)\b(?:hostname|host|server|computer|node|сервер|узел|компьютер|хост)\s*[:=]\s*(?<value>(?=[A-Z0-9-]*[A-Z])[A-Z0-9][A-Z0-9-]{1,62})\b' 'HOST' 'Medium' 65 'value'
    Add-SafePasteRegexCandidates $state $Text '(?<!Windows\s)\b(?:hostname|host|server|computer|node|сервер|узел|компьютер|хост)\s+(?<value>(?=[A-Z0-9-]*[A-Z])(?=[A-Z0-9-]*[0-9-])[A-Z0-9][A-Z0-9-]{1,62})\b' 'HOST' 'Medium' 65 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:(?:username|login|account|пользователь)\s*(?:[:=]\s*|\s+)|user\s*(?:[:=]\s*|(?!id\b)\s+))(?<value>[A-Z0-9][A-Z0-9._-]{1,127})\b' 'USER' 'Medium' 63 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:user\s*id|uid)\s*[:=]\s*(?<value>[A-Z0-9][A-Z0-9._-]{1,127})\b' 'USER' 'Medium' 66 'value'
    Add-SafePasteRegexCandidates $state $Text '\b(?:SN|S/N|Serial(?:\s+Number)?|Service\s+Tag|Asset(?:\s+Tag)?|IMEI|WWN|серийный(?:\s+номер)?|инвентарный(?:\s+номер)?)(?:\s+[\p{L}]+){0,2}\s*[:=#]?\s*(?<value>[A-Z0-9][A-Z0-9-]{4,31})\b' 'SERIAL' 'Medium' 60 'value'

    # Явно подтверждённые пользователем значения. Границы не дают учить подстроки слов.
    foreach ($entry in @($Database.Sensitive)) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value) -or [string]::IsNullOrWhiteSpace([string]$entry.Type)) { continue }
        $escaped = [regex]::Escape([string]$entry.Value)
        $pattern = "(?<![\\p{L}\\p{N}._-])$escaped(?![\\p{L}\\p{N}._-])"
        Add-SafePasteRegexCandidates $state $Text $pattern ([string]$entry.Type).ToUpperInvariant() 'Learned' 73 '' $false 'Learned DB'
    }

    $allow = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($Database.Allow)) { [void]$allow.Add([string]$value) }
    $filtered = @($state.Items | Where-Object { -not $allow.Contains($_.Value) })
    return Resolve-SafePasteOverlaps -Candidates $filtered
}
