#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$HiddenHost,
    [switch]$Console
)

# Обычный запуск .ps1 сам перезапускается без консольного окна.
# -Console оставлен для диагностики ошибок при разработке.
if (-not $HiddenHost -and -not $Console) {
    $launchArguments = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$PSCommandPath`" -HiddenHost"
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList $launchArguments
    return
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:SafePasteReviewInProgress = $false
$script:SafePasteClearTimers = @()
$script:SafePasteHotkeySource = $null
$script:SafePasteHotkeyHook = $null
$script:SafePasteMutex = $null
$script:SafePasteTray = $null
$script:SafePasteTrayStatusItem = $null

if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw 'SafePaste требуется запустить в STA: powershell.exe -NoProfile -STA -File .\SafePaste.ps1'
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing, System.Security

. (Join-Path $PSScriptRoot 'Core\Database.ps1')
. (Join-Path $PSScriptRoot 'Core\Clipboard.ps1')
. (Join-Path $PSScriptRoot 'Core\Replacement.ps1')
. (Join-Path $PSScriptRoot 'Core\Detection.ps1')
. (Join-Path $PSScriptRoot 'Core\Hotkey.ps1')

function Show-SafePasteError {
    param([string]$Message)
    [void][System.Windows.MessageBox]::Show($Message, 'SafePaste', 'OK', 'Error')
}

function Set-SafePasteDarkTitleBar {
    param([Parameter(Mandatory)][System.Windows.Window]$Window)
    try {
        $source = [System.Windows.Interop.HwndSource]([System.Windows.PresentationSource]::FromVisual($Window))
        if ($null -eq $source) { return }
        $enabled = 1
        # sizeof(int) всегда 4. Оформление необязательно и не должно мешать работе SafePaste.
        $result = [SafePasteNative]::DwmSetWindowAttribute($source.Handle, 20, [ref]$enabled, 4)
        if ($result -ne 0) { [void][SafePasteNative]::DwmSetWindowAttribute($source.Handle, 19, [ref]$enabled, 4) }
    } catch {
        # На старых Windows или при отключённом DWM оставляем системную шапку без ошибки.
    }
}

function Show-SafePasteAbout {
    $xaml = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UI\AboutWindow.xaml') -Raw -Encoding UTF8
    $xmlReader = New-Object System.Xml.XmlNodeReader ([xml]$xaml)
    $window = [System.Windows.Markup.XamlReader]::Load($xmlReader)
    $about = $window.FindName('AboutText')
    $about.Text = $about.Text.Replace('{USER}', [Environment]::UserName)
    $close = $window.FindName('CloseButton')
    $close.Add_Click({ $window.Close() })
    $window.Add_SourceInitialized({ Set-SafePasteDarkTitleBar -Window $window })
    [void]$window.ShowDialog()
}

function Get-SafePasteDisplayValue {
    param([string]$Value, [string]$Type)
    if ($Type -in @('SECRET','TOKEN','PRIVATE_KEY')) { return "<скрыто: $($Value.Length) симв.>" }
    if ($Value.Length -le 120) { return $Value }
    return $Value.Substring(0, 117) + '…'
}

function Get-SafePasteRichTextSelection {
    param([Parameter(Mandatory)][System.Windows.Controls.RichTextBox]$Box, [Parameter(Mandatory)][string]$Text)
    $selectedText = $Box.Selection.Text
    if ([string]::IsNullOrEmpty($selectedText)) { return $null }
    $prefix = [System.Windows.Documents.TextRange]::new($Box.Document.ContentStart, $Box.Selection.Start)
    $start = $prefix.Text.Length
    if ($start -lt 0 -or ($start + $selectedText.Length) -gt $Text.Length -or $Text.Substring($start, $selectedText.Length) -cne $selectedText) {
        throw 'Не удалось сопоставить выделение с исходным текстом. Выделите значение ещё раз, не включая перевод строки.'
    }
    return [pscustomobject]@{ Start = $start; Length = $selectedText.Length; Value = $selectedText }
}

function Get-SafePasteScrollViewer {
    param([Parameter(Mandatory)][System.Windows.DependencyObject]$Control)
    if ($Control -is [System.Windows.Controls.ScrollViewer]) { return $Control }
    $children = [System.Windows.Media.VisualTreeHelper]::GetChildrenCount($Control)
    for ($index = 0; $index -lt $children; $index++) {
        $found = Get-SafePasteScrollViewer -Control ([System.Windows.Media.VisualTreeHelper]::GetChild($Control, $index))
        if ($found) { return $found }
    }
    return $null
}

function Set-SafePasteTrayStatus {
    param([Parameter(Mandatory)][string]$Message, [switch]$HasFindings)
    if ($script:SafePasteTray) {
        # NotifyIcon ограничивает длину текста; здесь нет исходного текста или найденных значений.
        $script:SafePasteTray.Text = ('SafePaste — ' + $Message).Substring(0, [Math]::Min(63, ('SafePaste — ' + $Message).Length))
        $script:SafePasteTray.Icon = if ($HasFindings) { [System.Drawing.SystemIcons]::Warning } else { [System.Drawing.SystemIcons]::Shield }
    }
    if ($script:SafePasteTrayStatusItem) { $script:SafePasteTrayStatusItem.Text = $Message }
}

function Update-SafePasteClipboardStatus {
    if ($script:SafePasteReviewInProgress) { return }
    # Слушатель узнаёт только о факте изменения Clipboard — текст здесь не читается.
    Set-SafePasteTrayStatus 'Clipboard обновлён: нажмите Ctrl+Shift+V для Safe Paste'
}

function Show-SafePasteReview {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Matches)
    $xaml = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UI\MainWindow.xaml') -Raw -Encoding UTF8
    $xmlReader = New-Object System.Xml.XmlNodeReader ([xml]$xaml)
    $window = [System.Windows.Markup.XamlReader]::Load($xmlReader)
    $window.Add_SourceInitialized({ Set-SafePasteDarkTitleBar -Window $window })
    $summary = $window.FindName('SummaryText'); $sourceText = $window.FindName('SourceText')
    $list = $window.FindName('DetectionList'); $status = $window.FindName('StatusText')
    $cancel = $window.FindName('CancelButton'); $refreshButton = $window.FindName('RefreshButton'); $copy = $window.FindName('CopyButton'); $paste = $window.FindName('SafePasteButton')
    $window.Tag = [pscustomobject]@{ Action = 'Cancel'; Text = $Text; Matches = $Matches }

    $refresh = {
        $current = @(Resolve-SafePasteOverlaps -Candidates @($window.Tag.Matches))
        $window.Tag.Matches = $current
        $previousScroll = Get-SafePasteScrollViewer -Control $sourceText
        $previousVerticalOffset = if ($previousScroll) { $previousScroll.VerticalOffset } else { 0 }
        $previousHorizontalOffset = if ($previousScroll) { $previousScroll.HorizontalOffset } else { 0 }
        $document = New-Object System.Windows.Documents.FlowDocument
        $document.PagePadding = '0'
        $paragraph = New-Object System.Windows.Documents.Paragraph
        $paragraph.Margin = '0'
        $position = 0
        foreach ($highlight in @($current | Where-Object Enabled | Sort-Object Start)) {
            if ($highlight.Start -gt $position) {
                [void]$paragraph.Inlines.Add([System.Windows.Documents.Run]::new($window.Tag.Text.Substring($position, $highlight.Start - $position)))
            }
            $run = [System.Windows.Documents.Run]::new($window.Tag.Text.Substring($highlight.Start, $highlight.Length))
            $run.Foreground = [System.Windows.Media.Brushes]::IndianRed
            $run.FontWeight = [System.Windows.FontWeights]::SemiBold
            [void]$paragraph.Inlines.Add($run)
            $position = $highlight.Start + $highlight.Length
        }
        if ($position -lt $window.Tag.Text.Length) {
            [void]$paragraph.Inlines.Add([System.Windows.Documents.Run]::new($window.Tag.Text.Substring($position)))
        }
        [void]$document.Blocks.Add($paragraph)
        $sourceText.Document = $document
        $sourceText.UpdateLayout()
        $restoredScroll = Get-SafePasteScrollViewer -Control $sourceText
        if ($restoredScroll) {
            $restoredScroll.ScrollToVerticalOffset($previousVerticalOffset)
            $restoredScroll.ScrollToHorizontalOffset($previousHorizontalOffset)
        }
        [void]$list.Children.Clear()
        $activeCount = @($current | Where-Object Enabled).Count
        $summary.Text = "Найдено: $($current.Count) · Скрыто: $activeCount"
        foreach ($item in $current) {
            $row = New-Object System.Windows.Controls.Grid
            $row.Margin = '2'
            $row.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = '30' }))
            $row.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = '88' }))
            $row.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = '80' }))
            $row.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = '*' }))
            $row.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = '125' }))
            $check = New-Object System.Windows.Controls.CheckBox
            $check.IsChecked = $item.Enabled; $check.IsEnabled = -not $item.Locked
            $check.Tag = $item
            $check.ToolTip = if ($item.Locked) { 'Секреты защищены от случайного раскрытия.' } else { 'Включить или выключить скрытие.' }
            [System.Windows.Controls.Grid]::SetColumn($check, 0); [void]$row.Children.Add($check)
            $itemColor = if ($item.Enabled) { '#FF7B72' } else { '#98A2B3' }
            $typeText = New-Object System.Windows.Controls.TextBlock -Property @{ Text = $item.Type; FontWeight = 'SemiBold'; Foreground = $itemColor; VerticalAlignment = 'Center' }
            [System.Windows.Controls.Grid]::SetColumn($typeText, 1); [void]$row.Children.Add($typeText)
            $confidence = New-Object System.Windows.Controls.TextBlock -Property @{ Text = $item.Confidence; Foreground = '#B6C2D0'; VerticalAlignment = 'Center' }
            [System.Windows.Controls.Grid]::SetColumn($confidence, 2); [void]$row.Children.Add($confidence)
            $value = New-Object System.Windows.Controls.TextBlock -Property @{ Text = (Get-SafePasteDisplayValue $item.Value $item.Type); Foreground = $itemColor; TextTrimming = 'CharacterEllipsis'; VerticalAlignment = 'Center'; ToolTip = (Get-SafePasteDisplayValue $item.Value $item.Type) }
            [System.Windows.Controls.Grid]::SetColumn($value, 3); [void]$row.Children.Add($value)
            $placeholder = New-Object System.Windows.Controls.TextBlock -Property @{ Text = "→ [$($item.Type)_…]"; Foreground = '#146C43'; VerticalAlignment = 'Center' }
            [System.Windows.Controls.Grid]::SetColumn($placeholder, 4); [void]$row.Children.Add($placeholder)
            $check.Add_Checked({ param($sender, $eventArgs); $sender.Tag.Enabled = $true; & $refresh })
            $check.Add_Unchecked({ param($sender, $eventArgs); $sender.Tag.Enabled = $false; & $refresh })
            [void]$list.Children.Add($row)
        }
        $status.Text = 'Секреты, токены и приватные ключи защищены от случайного исключения.'
    }

    $markSelection = {
        param([Parameter(Mandatory)][string]$Type)
        if ($sourceText.Selection.Text.Length -eq 0) { $status.Text = 'Сначала выделите значение в исходном тексте.'; return }
        try { $selection = Get-SafePasteRichTextSelection -Box $sourceText -Text $window.Tag.Text }
        catch { $status.Text = $_.Exception.Message; return }
        $start = $selection.Start; $length = $selection.Length; $value = $selection.Value
        if ([string]::IsNullOrWhiteSpace($value)) { $status.Text = 'Выделенное значение не должно состоять только из пробелов.'; return }
        $end = $start + $length
        if (@($window.Tag.Matches | Where-Object { $_.Locked -and $start -lt ($_.Start + $_.Length) -and $_.Start -lt $end }).Count -gt 0) {
            $status.Text = 'Нельзя вручную переопределить диапазон обязательного секрета.'; return
        }
        $remaining = @($window.Tag.Matches | Where-Object { -not ($start -lt ($_.Start + $_.Length) -and $_.Start -lt $end) })
        $manual = New-SafePasteMatch -Start $start -Length $length -Value $value -Type $Type -Confidence 'Manual' -Priority 95 -Source 'Manual' -Manual $true
        $window.Tag.Matches = @($remaining) + $manual
        $savedForNextSessions = $false
        if ($Type -notin @('SECRET','TOKEN','PRIVATE_KEY','PASSWORD')) {
            try {
                Add-SafePasteSensitiveValue -Value $value -Type $Type
                $savedForNextSessions = $true
            } catch {
                $status.Text = "Пометка применена только к текущей вставке: $($_.Exception.Message)"
            }
        }
        & $refresh
        $status.Text = if ($savedForNextSessions) { "Помечено как $Type и запомнено для следующих сессий." } else { "Помечено как $Type только для текущей вставки." }
    }
    $neverHideSelection = {
        if ($sourceText.Selection.Text.Length -eq 0) { $status.Text = 'Выделите значение, которое не нужно скрывать.'; return }
        try { $selection = Get-SafePasteRichTextSelection -Box $sourceText -Text $window.Tag.Text }
        catch { $status.Text = $_.Exception.Message; return }
        $value = $selection.Value
        if (@($window.Tag.Matches | Where-Object { $_.Locked -and $_.Value -ceq $value }).Count -gt 0) { $status.Text = 'Секреты нельзя добавлять в allowlist.'; return }
        Add-SafePasteAllowValue $value
        $window.Tag.Matches = @($window.Tag.Matches | Where-Object { $_.Value -cne $value })
        & $refresh
        $status.Text = 'Значение добавлено в allowlist для следующих проверок.'
    }
    $unmarkSelection = {
        if ($sourceText.Selection.Text.Length -eq 0) { $status.Text = 'Выделите значение, скрытие которого нужно отменить.'; return }
        try { $selection = Get-SafePasteRichTextSelection -Box $sourceText -Text $window.Tag.Text }
        catch { $status.Text = $_.Exception.Message; return }
        $start = $selection.Start; $end = $start + $selection.Length
        $overlapping = @($window.Tag.Matches | Where-Object { $start -lt ($_.Start + $_.Length) -and $_.Start -lt $end })
        if ($overlapping.Count -eq 0) { $status.Text = 'В выделенном фрагменте нет активной метки.'; return }
        $editable = @($overlapping | Where-Object { -not $_.Locked })
        if ($editable.Count -eq 0) { $status.Text = 'Обязательный секрет нельзя исключить из скрытия.'; return }
        foreach ($item in $editable) { $item.Enabled = $false }
        & $refresh
        $status.Text = 'Скрытие выделенного фрагмента отменено для этой вставки.'
    }
    $contextMenu = New-Object System.Windows.Controls.ContextMenu
    $contextMenu.Style = $window.FindResource('DarkContextMenu')
    $contextMenu.Background = '#0A0A0A'; $contextMenu.Foreground = '#F8FAFC'
    $menuItemStyle = $window.FindResource('DarkContextMenuItem')
    $separatorStyle = $window.FindResource('DarkContextMenuSeparator')
    $menuHeader = New-Object System.Windows.Controls.MenuItem -Property @{ Header = 'Действия с выделением'; IsEnabled = $false; Style = $menuItemStyle }
    [void]$contextMenu.Items.Add($menuHeader)
    foreach ($category in @('HOST','USER','SERIAL')) {
        $categoryItem = New-Object System.Windows.Controls.MenuItem -Property @{ Header = "Пометить как: $category"; Tag = $category; Style = $menuItemStyle }
        $categoryItem.Add_Click({ param($sender, $eventArgs); & $markSelection ([string]$sender.Tag) })
        [void]$contextMenu.Items.Add($categoryItem)
    }
    $unmarkMenu = New-Object System.Windows.Controls.MenuItem -Property @{ Header = 'Отменить выделенное'; Style = $menuItemStyle }
    $unmarkMenu.Add_Click({ & $unmarkSelection })
    $neverHideMenu = New-Object System.Windows.Controls.MenuItem -Property @{ Header = 'Никогда не скрывать'; Style = $menuItemStyle }
    $neverHideMenu.Add_Click({ & $neverHideSelection })
    $separator = New-Object System.Windows.Controls.Separator
    $separator.Style = $separatorStyle
    [void]$contextMenu.Items.Add($separator)
    [void]$contextMenu.Items.Add($unmarkMenu)
    [void]$contextMenu.Items.Add($neverHideMenu)
    $sourceText.ContextMenu = $contextMenu
    $refreshButton.Add_Click({
        try {
            $newText = Get-SafePasteClipboardText
            $settings = Get-SafePasteSettings
            if ($newText.Length -gt [int]$settings.MaxClipboardChars) { throw 'Слишком большой clipboard. Сократите текст и повторите.' }
            $window.Tag.Text = $newText
            $window.Tag.Matches = @(Get-SafePasteCandidates -Text $newText -Database (Get-SafePasteDatabase))
            & $refresh
            $status.Text = 'Текущий Clipboard прочитан и проверен заново.'
        } catch { $status.Text = $_.Exception.Message }
    })
    $copy.Add_Click({
        try { $result = Get-SafePasteReplacementResult -Text $window.Tag.Text -Matches @($window.Tag.Matches); Set-SafePasteClipboardText $result.Text; $status.Text = 'Обезличенный текст скопирован. Автовставки не будет.' }
        catch { $status.Text = $_.Exception.Message }
    })
    $paste.Add_Click({ $window.Tag.Action = 'Paste'; $window.Close() })
    $cancel.Add_Click({ $window.Tag.Action = 'Cancel'; $window.Close() })
    & $refresh
    [void]$window.ShowDialog()
    return $window.Tag
}

function Invoke-SafePastePaste {
    param([Parameter(Mandatory)][string]$SanitizedText, [Parameter(Mandatory)][IntPtr]$TargetWindow)
    Set-SafePasteClipboardText $SanitizedText
    Start-Sleep -Milliseconds 120
    if ($TargetWindow -eq [IntPtr]::Zero -or -not [SafePasteNative]::SetForegroundWindow($TargetWindow)) {
        Clear-SafePasteClipboardIfUnchanged $SanitizedText
        throw 'Не удалось вернуть фокус исходному приложению. Автовставка отменена.'
    }
    Start-Sleep -Milliseconds 50
    [SafePasteNative]::SendCtrlV()
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds([int](Get-SafePasteSettings).ClipboardClearDelayMs)
    $ticket = [pscustomobject]@{ Timer = $timer; ExpectedText = $SanitizedText }
    $script:SafePasteClearTimers = @($script:SafePasteClearTimers) + $ticket
    $timer.Add_Tick({
        param($sender, $eventArgs)
        $sender.Stop()
        $currentTicket = @($script:SafePasteClearTimers | Where-Object { $_.Timer -eq $sender }) | Select-Object -First 1
        if ($currentTicket) { Clear-SafePasteClipboardIfUnchanged $currentTicket.ExpectedText }
        $script:SafePasteClearTimers = @($script:SafePasteClearTimers | Where-Object { $_.Timer -ne $sender })
    })
    $timer.Start()
}

function Start-SafePasteReview {
    param([switch]$Quick)
    if ($script:SafePasteReviewInProgress) { return }
    $script:SafePasteReviewInProgress = $true
    $original = $null
    try {
        $target = [SafePasteNative]::GetForegroundWindow()
        $original = Get-SafePasteClipboardText
        $settings = Get-SafePasteSettings
        if ($original.Length -gt [int]$settings.MaxClipboardChars) { throw 'Слишком большой clipboard. Автовставка отключена; сократите текст и повторите.' }
        $matches = @(Get-SafePasteCandidates -Text $original -Database (Get-SafePasteDatabase))
        if ($Quick) {
            $matches = @($matches | Where-Object { $_.Locked -or $_.Confidence -eq 'High' })
            $result = Get-SafePasteReplacementResult -Text $original -Matches $matches
            Invoke-SafePastePaste $result.Text $target
            return
        }
        $review = Show-SafePasteReview -Text $original -Matches $matches
        if ($review.Action -eq 'Paste') {
            $result = Get-SafePasteReplacementResult -Text $review.Text -Matches @($review.Matches)
            Invoke-SafePastePaste $result.Text $target
        }
    } catch { Show-SafePasteError $_.Exception.Message }
    finally { $original = $null; $script:SafePasteReviewInProgress = $false }
}

$createdNew = $false
$script:SafePasteMutex = New-Object System.Threading.Mutex($true, 'Global\SafePaste', [ref]$createdNew)
if (-not $createdNew) { return }

$app = New-Object System.Windows.Application
$hostWindow = New-Object System.Windows.Window
$hostWindow.Width = 1; $hostWindow.Height = 1; $hostWindow.Opacity = 0; $hostWindow.ShowInTaskbar = $false; $hostWindow.WindowStyle = 'None'
$hostWindow.Add_SourceInitialized({ Register-SafePasteHotkeys $hostWindow })
$hostWindow.Add_Closed({ Unregister-SafePasteHotkeys })

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Shield; $tray.Text = 'SafePaste'; $tray.Visible = $true
$script:SafePasteTray = $tray
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$menu.Renderer = New-Object SafePasteDarkMenuRenderer
$menu.BackColor = [System.Drawing.Color]::FromArgb(10, 10, 10)
$menu.ForeColor = [System.Drawing.Color]::FromArgb(248, 250, 252)
$menu.ShowImageMargin = $false
$menu.ShowCheckMargin = $false
$safeItem = $menu.Items.Add('Safe Paste'); $safeItem.Add_Click({ Start-SafePasteReview })
$trayStatusItem = $menu.Items.Add('Clipboard: ожидание')
$trayStatusItem.Enabled = $false
$script:SafePasteTrayStatusItem = $trayStatusItem
$menu.Items.Add('База хранится локально (DPAPI)') | Out-Null
$aboutItem = $menu.Items.Add('О приложении'); $aboutItem.Add_Click({ Show-SafePasteAbout })
$exitItem = $menu.Items.Add('Выход'); $exitItem.Add_Click({ $tray.Visible = $false; $tray.Dispose(); $hostWindow.Close(); $app.Shutdown() })
$tray.ContextMenuStrip = $menu
$tray.Add_DoubleClick({ Start-SafePasteReview })

$risk = @(Test-SafePasteClipboardRisk)
if ($risk.Count -gt 0) { [void][System.Windows.MessageBox]::Show("Внимание: $($risk -join '; ').`nИсходный Ctrl+C мог уже попасть в историю до SafePaste. Приложение не меняет настройки Windows.", 'SafePaste — Clipboard', 'OK', 'Warning') }

try {
    $hostWindow.Show()
    Update-SafePasteClipboardStatus
    [void]$app.Run()
}
finally {
    if ($tray) { $tray.Visible = $false; $tray.Dispose() }
    $script:SafePasteTray = $null
    $script:SafePasteTrayStatusItem = $null
    Unregister-SafePasteHotkeys
    if ($script:SafePasteMutex) { $script:SafePasteMutex.ReleaseMutex(); $script:SafePasteMutex.Dispose() }
}
