@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
set PATH=C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin;%PATH%
nvcc -O2 -Xcompiler /utf-8 -o bscan_cuda512.exe bscan_cuda512.cu -lcufft
echo Build exit code: %ERRORLEVEL%
