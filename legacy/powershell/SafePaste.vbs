Option Explicit

Dim shell, fso, projectPath, command
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

projectPath = fso.GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File " & Chr(34) & projectPath & "\SafePaste.ps1" & Chr(34) & " -HiddenHost"

If WScript.Arguments.Named.Exists("check") Then
    WScript.Echo command
    WScript.Quit 0
End If

' 0 = скрытое окно; SafePaste остаётся доступным из системного трея.
shell.Run command, 0, False
