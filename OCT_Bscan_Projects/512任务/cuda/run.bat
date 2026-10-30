@echo off
setlocal
rem ===== always run from this script's folder (program uses ../e3.dat and image_e3/) =====
cd /d "%~dp0"

rem ===== CUDA runtime path =====
set "CUDA_BIN=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"
if exist "%CUDA_BIN%\cufft64_11.dll" set "PATH=%CUDA_BIN%;%PATH%"

rem ===== check data file (../e3.dat) =====
if not exist "..\e3.dat" (
    echo [ERROR] Cannot find ..\e3.dat
    echo Please make sure e3.dat is in the parent folder.
    pause
    exit /b 1
)

echo Running bscan_cuda512.exe ...
bscan_cuda512.exe
echo.
echo Done. Exit code: %ERRORLEVEL%
echo Output images are in image_e3\ folder.
pause