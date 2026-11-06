@echo off
chcp 65001 >nul
title LiteTube [.NET 8 LTS / FlyleafLib / Direct3D 11]

set DOTNET_ROOT=C:\Users\azeve\.gemini\antigravity\scratch\.dotnet
if exist "%DOTNET_ROOT%\dotnet.exe" (
    set "DOTNET=%DOTNET_ROOT%\dotnet.exe"
    set "PATH=%DOTNET_ROOT%;%PATH%"
) else (
    set "DOTNET=dotnet"
)

echo Compilando LiteTube [.NET 8 LTS (net8.0-windows) - FlyleafLib / Direct3D 11]...
if exist "%~dp0LiteTube.old.exe" del /f /q "%~dp0LiteTube.old.exe" >nul 2>&1
if exist "%~dp0LiteTube.exe" move /y "%~dp0LiteTube.exe" "%~dp0LiteTube.old.exe" >nul 2>&1

"%DOTNET%" build "%~dp0LiteTube.csproj" -c Release -p:Platform=x64 --nologo

if %ERRORLEVEL% EQU 0 (
    if exist "%~dp0LiteTube.old.exe" del /f /q "%~dp0LiteTube.old.exe" >nul 2>&1
    echo [SUCESSO] LiteTube [.NET 8 LTS] compilado com 0 erros!
    echo Sincronizando binarios para a raiz do aplicativo...
    xcopy /y /e /q "%~dp0bin\x64\Release\net8.0-windows7.0\*.*" "%~dp0" >nul 2>&1
    echo [PRONTO] LiteTube.exe e dependencias .NET 8 atualizados na raiz com sucesso!
) else (
    echo [ERRO] Falha na compilacao do LiteTube [.NET 8 LTS]!
    if exist "%~dp0LiteTube.old.exe" move /y "%~dp0LiteTube.old.exe" "%~dp0LiteTube.exe" >nul 2>&1
)
