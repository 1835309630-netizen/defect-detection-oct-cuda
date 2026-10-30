B_Scan\cuda_original  —— 优化前的原版（保留用于对照）
=========================================================

这个目录是 2026-10-09 我动手优化之前 B_Scan\cuda\ 的原样内容，
一个字都没改，可以直接双击 run.bat 运行。

目录里的东西
------------
bscan_cuda.cu          原始源码（361 行，未改动）
bscan_cuda.exe         原始可执行文件（2026-10-05 10:58 构建，SHA256 098F5929...）
build.bat              原始构建脚本（注意：没有 -arch，也没有 -Xcompiler /utf-8）
run.bat                原始运行脚本（没有 cd /d "%~dp0"）
bscan_image_cuda.bmp   原始程序的输出图
e3_bscan1.dat          输入数据（单帧，2048 x 256 x int16）
MAmean.bin / xi.bin / CArray_re.bin / CArray_im.bin
                       原始 float32 标定参数（每个 8192 字节 = 2048 个 float）
CArray.bin             原目录里就有、但新旧代码都没有读取的遗留文件

运行
----
双击 run.bat 即可。它会把 C:\...\CUDA\v12.8\bin 加到 PATH，因为
bscan_cuda.exe 依赖 cufft64_11.dll（本目录里没有这个 DLL，靠 PATH 找）。

原版的已知特征（实测数据）
--------------------------
GPU 计时段约 95~100 ms，其中：
    ~52.7 ms  第一次核函数启动——exe 里只有 sm_52，驱动必须现场 PTX JIT
    ~45.6 ms  cufftPlanMany 建 plan
     ~1.4 ms  真正干活的 4 个核函数 + FFT
输出与 python\bscan_image_python.bmp 相比：最大值差 1，172 个像素差 1（0.131%）。
原因：这里 4 个 .bin 是 .mat 里 double 的 float32 截断。

优化版在 ..\cuda\，与 Python 标杆逐字节完全相同，真实墙钟快约 2 倍。

注意
----
- 本目录的 build.bat 编不出本目录的 bscan_cuda.exe（产出的二进制 SHA256 不同），
  而且这份源码不加 -Xcompiler /utf-8 会因中文注释被按 GBK 解析而编译失败。
  需要重新构建请用 ..\cuda\build.bat。
- 原版 exe 读 .bin 时按 float32 硬读；换成 float64 的 .bin 会读出乱数据。
