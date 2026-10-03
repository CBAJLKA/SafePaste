Set-StrictMode -Version Latest

function Resolve-SafePasteOverlaps {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Candidates)
    $kept = New-Object System.Collections.Generic.List[object]
    foreach ($item in ($Candidates | Sort-Object @{Expression='Priority';Descending=$true}, @{Expression='Length';Descending=$true}, @{Expression='Start';Ascending=$true})) {
        $low = 0; $high = $kept.Count
        while ($low -lt $high) {
            $middle = [int][Math]::Floor(($low + $high) / 2)
            if ($kept[$middle].Start -lt $item.Start) { $low = $middle + 1 } else { $high = $middle }
        }
        $previous = $low - 1
        $overlap = ($previous -ge 0 -and $item.Start -lt ($kept[$previous].Start + $kept[$previous].Length)) -or ($low -lt $kept.Count -and $kept[$low].Start -lt ($item.Start + $item.Length))
        if (-not $overlap) { $kept.Insert($low, $item) }
    }
    return $kept.ToArray()
}

function Get-SafePasteReplacementResult {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Matches)
    $active = @($Matches | Where-Object Enabled)
    $map = @{}
    $counter = @{}
    foreach ($match in $active) {
        $identity = $match.Type + [char]0x1f + $match.Value
        if (-not $map.ContainsKey($identity)) {
            if (-not $counter.ContainsKey($match.Type)) { $counter[$match.Type] = 0 }
            $counter[$match.Type]++
            $map[$identity] = "[$($match.Type)_$($counter[$match.Type])]"
        }
        $match.Placeholder = $map[$identity]
    }
    $output = $Text
    foreach ($match in ($active | Sort-Object Start -Descending)) {
        $output = $output.Remove($match.Start, $match.Length).Insert($match.Start, $match.Placeholder)
    }
    return [pscustomobject]@{ Text = $output; Mapping = $map }
}
