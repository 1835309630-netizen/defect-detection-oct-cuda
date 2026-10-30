@echo off
rem ===== Build the CUDA B-scan program =====
rem NOTE 1: must build from this script's folder.
rem NOTE 2: the .cu has Chinese comments, so -Xcompiler /utf-8 is REQUIRED.
rem         Without it MSVC parses them as GBK and swallows code lines.
rem NOTE 3: -arch=native emits SASS for this machine's GPU. Without it nvcc
rem         defaults to sm_52 and the driver must PTX-JIT every run (~45 ms slower).
rem NOTE 4: keep this file ASCII-only; cmd.exe reads .bat in the OEM codepage and
rem         non-ASCII bytes in comments get executed as commands.
setlocal
cd /d "%~dp0"

set "CUDA_BIN=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin"
if exist "%CUDA_BIN%\nvcc.exe" set "PATH=%CUDA_BIN%;%PATH%"

call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul

nvcc -O2 -Xcompiler /utf-8 -arch=native -o bscan_cuda.exe bscan_cuda.cu
set RC=%ERRORLEVEL%

if "%RC%"=="0" echo Build OK
if not "%RC%"=="0" echo [ERROR] build failed, exit code %RC%
exit /b %RC%
