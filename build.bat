@echo off
rem ============================================================
rem  构建补丁器与校验器
rem  需要 .NET Framework 4.x（Windows 自带 csc.exe）
rem  用法: build.bat
rem ============================================================
setlocal

set "HERE=%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [错误] 找不到 csc.exe，请确认已安装 .NET Framework 4.x
    pause
    exit /b 1
)

if not exist "%HERE%bin" mkdir "%HERE%bin"

echo [1/3] 下载 Mono.Cecil...
if exist "%HERE%bin\Mono.Cecil.dll" (
    echo      已存在，跳过
) else (
    echo      [警告] 请手动下载 Mono.Cecil 并放到 bin\Mono.Cecil.dll
    echo      下载地址: https://www.nuget.org/packages/Mono.Cecil
    echo      或运行: nuget install Mono.Cecil
    pause
    exit /b 2
)

echo [2/3] 编译 Patcher.exe...
"%CSC%" -nologo -target:exe -optimize+ -out:"%HERE%bin\Patcher.exe" ^
    -r:"%HERE%bin\Mono.Cecil.dll" "%HERE%src\Patcher.cs"
if errorlevel 1 goto :fail

echo [3/3] 编译 Verify.exe...
"%CSC%" -nologo -target:exe -optimize+ -out:"%HERE%bin\Verify.exe" ^
    -r:"%HERE%bin\Mono.Cecil.dll" "%HERE%src\Verify.cs"
if errorlevel 1 goto :fail

echo.
echo 构建成功！产物在 bin\ 目录
pause
exit /b 0

:fail
echo.
echo 构建失败，请检查上面的错误信息
pause
exit /b 3
