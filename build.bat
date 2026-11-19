@echo off
setlocal enabledelayedexpansion
title Build Snoty

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "OUT=%ROOT%Snoty.exe"

echo.
echo   Building Snoty
echo   ---------------------
echo.

rem ----- .NET Framework reference assemblies ----------------------------
set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\System.Windows.Forms.dll" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"

if not exist "%FW%\System.Windows.Forms.dll" (
    echo   ERROR: .NET Framework 4.x was not found.
    echo.
    pause
    exit /b 1
)

rem ----- System.Speech.dll ----------------------------------------------
set "SPEECH=%FW%\WPF\System.Speech.dll"
if not exist "%SPEECH%" set "SPEECH=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll"
if not exist "%SPEECH%" set "SPEECH=%WINDIR%\Microsoft.NET\assembly\GAC_32\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll"

if not exist "%SPEECH%" (
    echo   ERROR: System.Speech.dll was not found.
    echo.
    pause
    exit /b 1
)

rem ----- C# compiler: prefer Roslyn, fall back to the in-box compiler -----
set "CSC="
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
call :try_csc "%ProgramFiles(x86)%\MSBuild\14.0\Bin\Roslyn\csc.exe"
call :try_csc "%FW%\csc.exe"

if not defined CSC (
    echo   ERROR: No C# compiler was found.
    echo.
    pause
    exit /b 1
)

echo   compiler  : !CSC!
echo   framework : %FW%
echo   output    : %OUT%
echo.

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /debug- /langversion:5 ^
    /out:"%OUT%" ^
    /win32manifest:"%SRC%\app.manifest" ^
    /win32icon:"%SRC%\snoty.ico" ^
    /r:"%FW%\System.dll" ^
    /r:"%FW%\System.Core.dll" ^
    /r:"%FW%\System.Drawing.dll" ^
    /r:"%FW%\System.Windows.Forms.dll" ^
    /r:"%SPEECH%" ^
    "%SRC%\*.cs"

if errorlevel 1 (
    echo.
    echo   BUILD FAILED
    echo.
    pause
    exit /b 1
)

echo.
echo   Build OK
for %%F in ("%OUT%") do echo   size   :  %%~zF bytes
echo.
exit /b 0

:try_csc
if not defined CSC if exist "%~1" set "CSC=%~1"
goto :eof
