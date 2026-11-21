@echo off
chcp 65001 >nul
title LiteTube 2.0 [100% Rust Nativo / LibVLC]
call "%~dp0build.bat"

if %ERRORLEVEL% EQU 0 (
    echo Iniciando LiteTube 2.0 [Rust]...
    start "" "%~dp0LiteTube.exe"
) else (
    echo [ERRO] Falha ao compilar!
    pause
)
