@echo off
setlocal

cd /d "%~dp0"

powershell -ExecutionPolicy Bypass -File ".\deploy.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Deployment failed. Exit code: %EXIT_CODE%
) else (
    echo Deployment completed successfully.
)

pause
exit /b %EXIT_CODE%
