@echo off
chcp 65001 >nul
title LiteTube 2.0 [100% Rust Nativo / LibVLC]

echo Compilando LiteTube 2.0 [100%% Rust Nativo / LibVLC]...
if exist "%~dp0LiteTube.old.exe" del /f /q "%~dp0LiteTube.old.exe" >nul 2>&1

cargo build --release --manifest-path "%~dp0Cargo.toml"

if %ERRORLEVEL% EQU 0 (
    echo [SUCESSO] LiteTube 2.0 [Rust] compilado com 0 erros!
    echo Sincronizando LiteTube.exe para a raiz do aplicativo...
    if exist "%~dp0LiteTube.exe" move /y "%~dp0LiteTube.exe" "%~dp0LiteTube.old.exe" >nul 2>&1
    copy /y "%~dp0target\release\LiteTube.exe" "%~dp0LiteTube.exe" >nul 2>&1
    if exist "%~dp0LiteTube.old.exe" del /f /q "%~dp0LiteTube.old.exe" >nul 2>&1
    echo [PRONTO] LiteTube.exe (Rust) atualizado na raiz com sucesso!
) else (
    echo [ERRO] Falha na compilacao do LiteTube 2.0 [Rust]!
    exit /b 1
)
