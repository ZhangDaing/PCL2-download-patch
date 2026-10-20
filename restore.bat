@echo off
setlocal enabledelayedexpansion
chcp 936 >nul

set "TARGET=%~1"

if not "%TARGET%"=="" goto :have_target
if exist "%~dp0..\Plain Craft Launcher 2.exe" set "TARGET=%~dp0..\Plain Craft Launcher 2.exe"
if not "%TARGET%"=="" goto :have_target
if exist "D:\minecraft\Plain Craft Launcher 2.exe" set "TARGET=D:\minecraft\Plain Craft Launcher 2.exe"
if not "%TARGET%"=="" goto :have_target
if exist "C:\minecraft\Plain Craft Launcher 2.exe" set "TARGET=C:\minecraft\Plain Craft Launcher 2.exe"

if "%TARGET%"=="" (
    echo [错误] 找不到 Plain Craft Launcher 2.exe
    echo        请传入路径，例如: restore.bat "D:\minecraft\Plain Craft Launcher 2.exe"
    pause
    exit /b 2
)

:have_target
set "BAK=%TARGET%.orig"
if not exist "%BAK%" (
    echo [错误] 找不到备份文件: "%BAK%"
    echo        无法还原。请确认补丁前是否生成过备份。
    pause
    exit /b 3
)

echo.
echo 即将用备份还原: %TARGET%
echo 备份来源:       %BAK%
echo.
set /p CONFIRM=确认还原? (Y/N):
if /i not "%CONFIRM%"=="Y" (
    echo 已取消。
    pause
    exit /b 0
)

copy /y "%BAK%" "%TARGET%" >nul
if errorlevel 1 (
    echo [错误] 还原失败，请确认 PCL 已完全关闭后重试。
    pause
    exit /b 4
)

echo.
echo 还原成功: %TARGET%
echo.
if not "%NOPAUSE%"=="1" pause
exit /b 0