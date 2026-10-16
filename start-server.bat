@echo off
title FrostyHeadless Server - read-only
set "EXE=%~dp0FrostyHeadless\bin\Release\net48\FrostyHeadless.exe"
if not exist "%EXE%" goto NOEXE
rem No --profile = interactive game picker in this terminal - configured games first, Enter = default
rem Example with args: this.bat --profile bf1 --port 18765
"%EXE%" serve %*
echo.
echo Server stopped. You can close this window.
pause
exit /b 0

:NOEXE
echo [ERROR] Not found: %EXE%
echo Build first with: dotnet build -c Release   in folder FrostyHeadless
pause
exit /b 1
