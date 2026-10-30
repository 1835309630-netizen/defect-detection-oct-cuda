@echo off
rem ===== Run the CUDA B-scan program =====
rem It uses relative paths (e3_bscan1.dat / *.bin / bscan_image_cuda.bmp),
rem so always run from this script's folder.
rem Keep this file ASCII-only (see build.bat NOTE 4).
setlocal
cd /d "%~dp0"

set "CUDA_BIN=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"
if exist "%CUDA_BIN%\cudart64_12.dll" set "PATH=%CUDA_BIN%;%PATH%"

bscan_cuda.exe
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" echo [ERROR] bscan_cuda.exe failed, exit code %RC%
echo Exit code: %RC%
pause
exit /b %RC%
