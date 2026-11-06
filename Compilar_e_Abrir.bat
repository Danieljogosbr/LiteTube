@echo off
chcp 65001 >nul
title LiteTube [.NET 8 LTS / FlyleafLib / Direct3D 11]
echo Compilando LiteTube [.NET 8 LTS / FlyleafLib / Direct3D 11]...
call "%~dp0build.bat"

if %ERRORLEVEL% EQU 0 (
    echo Iniciando LiteTube [.NET 8 LTS / FlyleafLib / Direct3D 11]...
    set DOTNET_ROOT=C:\Users\azeve\.gemini\antigravity\scratch\.dotnet
    if exist "%DOTNET_ROOT%" set "PATH=%DOTNET_ROOT%;%PATH%"
    start "" "%~dp0LiteTube.exe"
) else (
    echo [ERRO] Falha ao compilar!
    pause
)
