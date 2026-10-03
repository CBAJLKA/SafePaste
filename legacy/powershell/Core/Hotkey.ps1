Set-StrictMode -Version Latest

if (-not ('SafePasteNative' -as [type])) {
    Add-Type -ReferencedAssemblies @('System.Windows.Forms', 'System.Drawing') -TypeDefinition @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public static class SafePasteNative {
    [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("dwmapi.dll", PreserveSig=true)] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int InteractiveId = 0x5350;
    public const int QuickId = 0x5351;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint VK_V = 0x56;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V_BYTE = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    public static void SendCtrlV() {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V_BYTE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V_BYTE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}

public sealed class SafePasteDarkMenuRenderer : ToolStripRenderer {
    private static readonly Color Back = Color.FromArgb(10, 10, 10);
    private static readonly Color Hover = Color.FromArgb(82, 82, 82);
    private static readonly Color Border = Color.FromArgb(88, 88, 88);
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
        e.Graphics.Clear(Back);
    }
    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) {
        using (var brush = new SolidBrush(Back)) e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
        using (var brush = new SolidBrush((e.Item.Selected || e.Item.Pressed) ? Hover : Back)) e.Graphics.FillRectangle(brush, e.Item.Bounds);
    }
    protected override void OnRenderItemBackground(ToolStripItemRenderEventArgs e) {
        using (var brush = new SolidBrush((e.Item.Selected || e.Item.Pressed) ? Hover : Back)) e.Graphics.FillRectangle(brush, e.Item.Bounds);
    }
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
        var color = e.Item.Enabled ? Color.FromArgb(248, 250, 252) : Color.FromArgb(145, 145, 145);
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
        using (var pen = new Pen(Border)) {
            int y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
            e.Graphics.DrawLine(pen, e.Item.ContentRectangle.Left, y, e.Item.ContentRectangle.Right, y);
        }
    }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
        using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }
}
'@
}

function Register-SafePasteHotkeys {
    param([Parameter(Mandatory)][System.Windows.Window]$Window)
    $source = [System.Windows.Interop.HwndSource]([System.Windows.PresentationSource]::FromVisual($Window))
    if ($null -eq $source) { throw 'Не удалось создать окно для обработки горячих клавиш.' }
    $script:SafePasteHotkeySource = $source
    $script:SafePasteHotkeyHook = [System.Windows.Interop.HwndSourceHook]{
        param([IntPtr]$hwnd, [int]$msg, [IntPtr]$wParam, [IntPtr]$lParam, [ref]$handled)
        if ($msg -eq [SafePasteNative]::WM_HOTKEY) {
            $handled.Value = $true
            if ($wParam.ToInt32() -eq [SafePasteNative]::InteractiveId) { Start-SafePasteReview }
            elseif ($wParam.ToInt32() -eq [SafePasteNative]::QuickId) { Start-SafePasteReview -Quick }
        }
        elseif ($msg -eq [SafePasteNative]::WM_CLIPBOARDUPDATE) {
            Update-SafePasteClipboardStatus
        }
        return [IntPtr]::Zero
    }
    $source.AddHook($script:SafePasteHotkeyHook)
    $interactive = [SafePasteNative]::RegisterHotKey($source.Handle, [SafePasteNative]::InteractiveId, [SafePasteNative]::MOD_ALT -bor [SafePasteNative]::MOD_CONTROL -bor [SafePasteNative]::MOD_SHIFT, [SafePasteNative]::VK_V)
    $quick = [SafePasteNative]::RegisterHotKey($source.Handle, [SafePasteNative]::QuickId, [SafePasteNative]::MOD_CONTROL -bor [SafePasteNative]::MOD_SHIFT, [SafePasteNative]::VK_V)
    if (-not $interactive -or -not $quick) {
        if ($interactive) { [void][SafePasteNative]::UnregisterHotKey($source.Handle, [SafePasteNative]::InteractiveId) }
        if ($quick) { [void][SafePasteNative]::UnregisterHotKey($source.Handle, [SafePasteNative]::QuickId) }
        throw 'Не удалось зарегистрировать горячие клавиши. Возможно, они уже используются другой программой.'
    }
    if (-not [SafePasteNative]::AddClipboardFormatListener($source.Handle)) {
        throw 'Не удалось включить фоновое наблюдение за Clipboard.'
    }
}

function Unregister-SafePasteHotkeys {
    if ($script:SafePasteHotkeySource) {
        [void][SafePasteNative]::UnregisterHotKey($script:SafePasteHotkeySource.Handle, [SafePasteNative]::InteractiveId)
        [void][SafePasteNative]::UnregisterHotKey($script:SafePasteHotkeySource.Handle, [SafePasteNative]::QuickId)
        if ($script:SafePasteHotkeyHook) { $script:SafePasteHotkeySource.RemoveHook($script:SafePasteHotkeyHook) }
        [void][SafePasteNative]::RemoveClipboardFormatListener($script:SafePasteHotkeySource.Handle)
    }
}
