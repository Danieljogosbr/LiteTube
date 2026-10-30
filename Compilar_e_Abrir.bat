@echo off
chcp 65001 >nul
title LiteTube [Versao VLC]
echo Compilando LiteTube [Versao VLC]...
call "%~dp0build.bat"

if %ERRORLEVEL% EQU 0 (
    echo Iniciando LiteTube [Versao VLC]...
    start "" "%~dp0LiteTube.exe"
) else (
    echo [ERRO] Falha ao compilar!
    pause
)
