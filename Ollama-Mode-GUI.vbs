Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("Wscript.Shell")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = scriptDir & "\Ollama-Mode-GUI.ps1"
cmd = "powershell.exe -STA -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & ps1 & """"
shell.Run cmd, 0, False