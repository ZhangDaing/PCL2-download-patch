@echo off
setlocal enabledelayedexpansion
chcp 936 >nul

rem ============================================================
rem  PCL2 原版 MC 下载恢复补丁
rem  依赖在运行时从目标 exe 自身解出，本工具不附带任何 PCL 二进制
rem ============================================================

set "HERE=%~dp0"
set "TARGET=%~1"

if not "%TARGET%"=="" goto :have_target

echo 未指定路径，正在自动查找...
if exist "%HERE%..\Plain Craft Launcher 2.exe" set "TARGET=%HERE%..\Plain Craft Launcher 2.exe"
if not "%TARGET%"=="" goto :have_target
if exist "D:\minecraft\Plain Craft Launcher 2.exe" set "TARGET=D:\minecraft\Plain Craft Launcher 2.exe"
if not "%TARGET%"=="" goto :have_target
if exist "C:\minecraft\Plain Craft Launcher 2.exe" set "TARGET=C:\minecraft\Plain Craft Launcher 2.exe"
if not "%TARGET%"=="" goto :have_target

echo.
echo [错误] 找不到 Plain Craft Launcher 2.exe
echo        请把 exe 路径作为参数传入，例如：
echo        apply.bat "D:\minecraft\Plain Craft Launcher 2.exe"
echo.
pause
exit /b 2

:have_target
if not exist "%TARGET%" (
    echo [错误] 文件不存在: %TARGET%
    pause
    exit /b 2
)

echo.
echo ============================================================
echo   PCL2 原版 MC 下载恢复补丁
echo ============================================================
echo   目标: %TARGET%
echo.

set "BAK=%TARGET%.orig"
if exist "%BAK%" (
    echo [1/4] 备份已存在，跳过
) else (
    echo [1/4] 备份原文件
    copy /y "%TARGET%" "%BAK%" >nul
    if errorlevel 1 (
        echo [错误] 备份失败，已中止。请确认 PCL 已完全关闭。
        pause
        exit /b 3
    )
)

set "WORK=%HERE%.work"
if exist "%WORK%" rd /s /q "%WORK%"
mkdir "%WORK%"
copy /y "%TARGET%" "%WORK%\input.exe" >nul

echo [2/4] 正在打补丁...
"%HERE%bin\Patcher.exe" "%WORK%\input.exe" "%WORK%\output.exe"
if errorlevel 1 (
    echo.
    echo [错误] 补丁失败，原文件未改动。
    rd /s /q "%WORK%" 2>nul
    pause
    exit /b 4
)

echo [3/4] 正在校验...
"%HERE%bin\Verify.exe" "%WORK%\output.exe"
if errorlevel 1 (
    echo.
    echo [错误] 校验未通过，原文件未改动。
    rd /s /q "%WORK%" 2>nul
    pause
    exit /b 5
)

echo [4/4] 正在替换文件...
copy /y "%WORK%\output.exe" "%TARGET%" >nul
if errorlevel 1 (
    echo.
    echo [错误] 替换失败，请确认 PCL 已完全关闭。
    rd /s /q "%WORK%" 2>nul
    pause
    exit /b 6
)
rd /s /q "%WORK%" 2>nul

echo.
echo ============================================================
echo   补丁应用成功！
echo   原文件备份: %BAK%
echo   如需还原，运行 restore.bat
echo ============================================================
echo.
if not "%NOPAUSE%"=="1" pause
exit /b 0