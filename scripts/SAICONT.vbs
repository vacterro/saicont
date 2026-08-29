Option Explicit

Dim fileSystem, shell, scriptDirectory, projectRoot
Dim executablePath, configurationPath, pidFilePath, stopFilePath, stateFilePath, instanceFilePath, mode, command, exitCode

Set fileSystem = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
projectRoot = fileSystem.GetParentFolderName(scriptDirectory)
executablePath = fileSystem.BuildPath(projectRoot, "bin\SAICONT.exe")
configurationPath = fileSystem.BuildPath(projectRoot, "SAICONT.config.xml")
pidFilePath = fileSystem.BuildPath(projectRoot, "run\SAICONT.pid")
stopFilePath = fileSystem.BuildPath(projectRoot, "run\SAICONT.stop")
stateFilePath = fileSystem.BuildPath(projectRoot, "run\SAICONT.state.xml")
instanceFilePath = fileSystem.BuildPath(projectRoot, "run\SAICONT.instance.xml")

mode = "--watch"
If WScript.Arguments.Count = 1 Then
    If WScript.Arguments(0) = "--dry-run" Then
        mode = "--dry-run"
    Else
        WScript.Quit 2
    End If
ElseIf WScript.Arguments.Count > 1 Then
    WScript.Quit 2
End If

command = Quote(executablePath) & " " & mode & " --config " & Quote(configurationPath) & _
    " --pid-file " & Quote(pidFilePath) & " --stop-file " & Quote(stopFilePath) & _
    " --state-file " & Quote(stateFilePath) & " --instance-file " & Quote(instanceFilePath)
On Error Resume Next
exitCode = shell.Run(command, 0, True)
If Err.Number <> 0 Then
    WScript.Echo "ERR: shell.Run failed (" & Err.Number & ") -- " & Err.Description
    WScript.Quit 1
End If
On Error GoTo 0
WScript.Quit exitCode

Function Quote(value)
    Quote = Chr(34) & Replace(value, Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function
