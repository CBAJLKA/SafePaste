@echo off
rem SafePaste build script. Uses the C# compiler shipped with Windows (.NET Framework 4.x),
rem so no SDK and no Visual Studio are required. ASCII only: cmd.exe misparses UTF-8 text.
rem   build.cmd        - build bin\SafePaste.exe with MCP modes
rem   build.cmd test   - build and run the test suite
rem   build.cmd uitest - run isolated UI checks and render previews
rem   build.cmd preview - build the interactive UI demo
rem   build.cmd mcp   - build the same application with MCP modes
rem   build.cmd check - build everything and run both test suites
setlocal
set ROOT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" goto :nocsc

set BIN=%ROOT%bin
if not exist "%BIN%" mkdir "%BIN%"

set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll /r:System.Web.Extensions.dll
rem Windows text recognition (Windows.Media.Ocr) for images: WinMD from Windows and two facades from the GAC, no SDK.
set WINMD=%WINDIR%\System32\WinMetadata
set GACMSIL=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
if not exist "%WINMD%\Windows.Media.winmd" goto :nowinmd
set REFS=%REFS% /r:"%WINMD%\Windows.Foundation.winmd" /r:"%WINMD%\Windows.Media.winmd" /r:"%WINMD%\Windows.Graphics.winmd" /r:"%WINMD%\Windows.Globalization.winmd" /r:"%WINMD%\Windows.Storage.winmd" /r:"%WINMD%\Windows.Security.winmd"
set REFS=%REFS% /r:"%GACMSIL%\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll" /r:"%GACMSIL%\System.Runtime.WindowsRuntime\v4.0_4.0.0.0__b77a5c561934e089\System.Runtime.WindowsRuntime.dll"
set FLAGS=/nologo /platform:anycpu /optimize+ /warn:4 /utf8output
set SMA=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Management.Automation\v4.0_3.0.0.0__31bf3856ad364e35\System.Management.Automation.dll
if not exist "%SMA%" goto :nosma

"%CSC%" %FLAGS% /target:exe /out:"%BIN%\BuildIcon.exe" /r:System.Drawing.dll "%ROOT%tools\BuildIcon.cs" "%ROOT%src\SafePaste\Ui\AppIcon.cs"
if errorlevel 1 exit /b 1
"%BIN%\BuildIcon.exe" "%ROOT%src\SafePaste\Assets"
if errorlevel 1 exit /b 1
set ICON=/win32icon:"%ROOT%src\SafePaste\Assets\SafePaste.ico" /resource:"%ROOT%src\SafePaste\Assets\SafePaste.ico",SafePaste.ico

if /i "%~1"=="test" goto :tests
if /i "%~1"=="corpus" goto :corpusbuild
if /i "%~1"=="uitest" goto :uitests
if /i "%~1"=="preview" goto :preview
if /i "%~1"=="mcp" goto :mainbuild
if /i "%~1"=="check" goto :check

:mainbuild
"%CSC%" %FLAGS% %ICON% /target:winexe /win32manifest:"%ROOT%src\SafePaste\app.manifest" /out:"%BIN%\SafePaste.exe" %REFS% /r:"%SMA%" /recurse:"%ROOT%src\SafePaste\*.cs" /recurse:"%ROOT%src\SafePaste.Mcp\*.cs"
if errorlevel 1 exit /b 1
echo Built: %BIN%\SafePaste.exe
exit /b 0

:corpusbuild
call "%ROOT%build.cmd"
if errorlevel 1 exit /b 1

:tests
"%CSC%" %FLAGS% %ICON% /target:exe /main:SafePaste.Tests.TestProgram /out:"%BIN%\SafePaste.Tests.exe" %REFS% /r:"%SMA%" /recurse:"%ROOT%src\SafePaste\*.cs" /recurse:"%ROOT%src\SafePaste.Mcp\*.cs" "%ROOT%tests\*.cs"
if errorlevel 1 exit /b 1
if /i "%~1"=="corpus" goto :runcorpus
"%BIN%\SafePaste.Tests.exe"
exit /b %errorlevel%

:runcorpus
"%BIN%\SafePaste.Tests.exe" corpus
exit /b %errorlevel%

:nocsc
echo C# compiler from .NET Framework 4.x was not found.
exit /b 1

:uitests
"%CSC%" %FLAGS% %ICON% /win32manifest:"%ROOT%src\SafePaste\app.manifest" /target:exe /main:SafePaste.Tests.UiTestProgram /out:"%BIN%\SafePaste.UiTests.exe" %REFS% /r:"%SMA%" /recurse:"%ROOT%src\SafePaste\*.cs" /recurse:"%ROOT%src\SafePaste.Mcp\*.cs" "%ROOT%tests\UiTests.cs"
if errorlevel 1 exit /b 1
"%BIN%\SafePaste.UiTests.exe"
exit /b %errorlevel%

:preview
"%CSC%" %FLAGS% %ICON% /win32manifest:"%ROOT%src\SafePaste\app.manifest" /target:winexe /main:PreviewUi /out:"%BIN%\SafePaste.Preview.exe" %REFS% /r:"%SMA%" /recurse:"%ROOT%src\SafePaste\*.cs" /recurse:"%ROOT%src\SafePaste.Mcp\*.cs" "%ROOT%tools\PreviewUi.cs"
exit /b %errorlevel%

:check
call "%ROOT%build.cmd"
if errorlevel 1 exit /b 1
call "%ROOT%build.cmd" test
if errorlevel 1 exit /b 1
call "%ROOT%build.cmd" uitest
exit /b %errorlevel%

:nosma
echo System.Management.Automation.dll from Windows PowerShell 5.1 was not found.
exit /b 1

:nowinmd
echo Windows metadata (System32\WinMetadata) was not found: Windows 10 or newer is required.
exit /b 1
