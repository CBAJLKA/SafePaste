Set-StrictMode -Version Latest

function Get-SafePasteClipboardText {
    try {
        if (-not [System.Windows.Clipboard]::ContainsText()) { throw 'В буфере обмена нет текста.' }
        return [System.Windows.Clipboard]::GetText()
    } catch {
        throw "Не удалось прочитать текстовый clipboard: $($_.Exception.Message)"
    }
}

function Set-SafePasteClipboardText {
    param([Parameter(Mandatory)][string]$Text)
    try { [System.Windows.Clipboard]::SetText($Text, [System.Windows.TextDataFormat]::UnicodeText) }
    catch { throw "Не удалось записать clipboard: $($_.Exception.Message)" }
}

function Clear-SafePasteClipboardIfUnchanged {
    param([Parameter(Mandatory)][string]$ExpectedText)
    try {
        if ([System.Windows.Clipboard]::ContainsText() -and [System.Windows.Clipboard]::GetText() -ceq $ExpectedText) {
            [System.Windows.Clipboard]::Clear()
        }
    } catch { }
}

function Test-SafePasteClipboardRisk {
    $risk = @()
    try {
        $clipboard = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Clipboard' -ErrorAction Stop
        if ($clipboard.EnableClipboardHistory -eq 1) { $risk += 'история Clipboard включена' }
        if ($clipboard.CloudClipboardAutomaticUpload -eq 1) { $risk += 'облачная синхронизация Clipboard включена' }
    } catch { }
    return $risk
}
