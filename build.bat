@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title muzika - build
echo.
echo    m u z i k a   -   build
echo    -----------------------
echo.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if exist "lib\Microsoft.Web.WebView2.Core.dll" if exist "lib\Microsoft.Web.WebView2.WinForms.dll" if exist "lib\WebView2Loader.dll" goto havesdk
echo  [1/2] downloading WebView2 SDK from nuget.org - one time only...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\get_webview2.ps1"
if errorlevel 1 goto nosdk
goto compile
:havesdk
echo  [1/2] WebView2 SDK already in lib\

:compile
echo  [2/2] compiling muzika.exe...
if exist muzika.exe del /q muzika.exe 2>nul
if exist muzika.exe goto running
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:muzika.exe /win32icon:assets\icon.ico /r:lib\Microsoft.Web.WebView2.Core.dll /r:lib\Microsoft.Web.WebView2.WinForms.dll /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Security.dll /resource:lib\Microsoft.Web.WebView2.Core.dll,wv2core /resource:lib\Microsoft.Web.WebView2.WinForms.dll,wv2wf /resource:lib\WebView2Loader.dll,wv2loader /resource:ui\index.html,uihtml /resource:assets\intro.webm,intro.webm /resource:assets\about.webm,about.webm /resource:assets\ring.webm,ring.webm /resource:assets\disc.jpg,disc.jpg src\*.cs
if errorlevel 1 goto failed

echo.
echo    done:  muzika.exe
echo    the exe is standalone - copy it anywhere you like.
echo.
if defined CI exit /b 0
start "" "%~dp0muzika.exe"
timeout /t 4 >nul
exit /b 0

:nocsc
echo  [x] csc.exe not found. It ships with .NET Framework 4 - built into Windows 10/11.
echo      Expected here: %CSC%
if not defined CI pause
exit /b 1

:nosdk
echo.
echo  [x] could not download the WebView2 SDK - check the internet and run build.bat again.
if not defined CI pause
exit /b 1

:running
echo  [x] muzika.exe is running - right-click the tray icon, exit, and run build.bat again.
if not defined CI pause
exit /b 1

:failed
echo.
echo  [x] compile errors above - screenshot this window and send it to Claude.
if not defined CI pause
exit /b 1
