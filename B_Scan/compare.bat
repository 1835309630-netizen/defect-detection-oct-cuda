@echo off
rem ===== Compare the Python reference and the CUDA output =====
rem ASCII only: cmd.exe reads .bat files in the OEM codepage, and non-ASCII bytes
rem in a comment can get executed as commands (that produced a spurious
rem "'xxx' is not recognized as an internal or external command" here).
setlocal
cd /d "%~dp0"
rem render the script's UTF-8 output correctly in the console
chcp 65001 >nul
set "PYTHONIOENCODING=utf-8"

"C:\Users\lenovo\AppData\Local\Programs\Python\Python314\python.exe" compare_images.py python\bscan_image_python.bmp cuda\bscan_image_cuda.bmp
set RC=%ERRORLEVEL%

echo.
if "%RC%"=="0" echo RESULT: PASS - every pixel differs by ^<= 1
if not "%RC%"=="0" echo RESULT: FAIL - exit code %RC%
pause
exit /b %RC%
