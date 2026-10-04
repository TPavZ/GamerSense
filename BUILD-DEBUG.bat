@echo off
setlocal
cd /d "%~dp0"
echo ========================================
echo   GamerSense - Debug Build
echo ========================================
dotnet restore src\GamerSense\GamerSense.csproj
if errorlevel 1 goto :fail
dotnet build src\GamerSense\GamerSense.csproj -c Debug
if errorlevel 1 goto :fail
echo.
echo BUILD SUCCESSFUL
pause
exit /b 0
:fail
echo.
echo BUILD FAILED
pause
exit /b 1
