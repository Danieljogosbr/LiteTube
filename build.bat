@echo off
chcp 65001 >nul
set NETFX=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
if not exist "%NETFX%\csc.exe" set NETFX=C:\Windows\Microsoft.NET\Framework\v4.0.30319
set CSC=%NETFX%\csc.exe
set MSBUILD=%NETFX%\MSBuild.exe

echo Compilando LiteTube [.NET 4.8.1 / 4.8 - Versao VLC]...
if exist LiteTube.old.exe del /f /q LiteTube.old.exe >nul 2>&1
if exist LiteTube.exe move /y LiteTube.exe LiteTube.old.exe >nul 2>&1

"%CSC%" /target:winexe /out:LiteTube.exe /platform:x64 /win32icon:app.ico /optimize+ /warn:4 /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Data.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Xaml.dll /r:System.Xml.dll /r:System.Net.Http.dll /r:"%NETFX%\WPF\PresentationCore.dll" /r:"%NETFX%\WPF\PresentationFramework.dll" /r:"%NETFX%\WPF\WindowsBase.dll" /r:"%NETFX%\WPF\WindowsFormsIntegration.dll" src\*.cs

if %ERRORLEVEL% EQU 0 (
    if exist App.config copy /y App.config LiteTube.exe.config >nul 2>&1
    echo [SUCESSO] LiteTube.exe [.NET 4.8.1 / 4.8] compilado com sucesso!
) else (
    echo [ERRO] Falha na compilacao via csc.exe. Tentando via MSBuild...
    "%MSBUILD%" LiteTube.csproj /p:Configuration=Release /p:Platform=x64
)
