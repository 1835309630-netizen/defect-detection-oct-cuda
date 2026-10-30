% export_calib_bins.m
% ---------------------------------------------------------------------------
% Export the dispersion-calibration variables that bscan_cuda512.cu reads
% (MAmean.bin, xi.bin, CArray_re.bin, CArray_im.bin) from the MATLAB
% calibration file e3_imfirst.mat.
%
% Run this whenever e3_imfirst.mat changes, then re-run bscan_cuda512.exe.
%
% The CUDA program auto-detects the element type by file size:
%   2048*8 bytes -> float64   (recommended, matches the .mat precision)
%   2048*4 bytes -> float32   (supported for backward compatibility)
%
% Measured effect on the 512 benchmark frames (max |diff| stays 1 either way):
%   float32 calibration -> 94858 / 67108864 pixels differ by 1  (0.1413%)
%   float64 calibration ->  1732 / 67108864 pixels differ by 1  (0.0026%)
% ---------------------------------------------------------------------------
clear;

here    = fileparts(mfilename('fullpath'));
matFile = fullfile(here, '..', 'e3_imfirst.mat');
outDir  = here;

if ~exist(matFile, 'file')
    error('calibration file not found: %s', matFile);
end

c_ = load(matFile);
if isfield(c_, 'CalStru')
    c_ = c_.CalStru;
elseif isfield(c_, 'Cal')
    c_ = c_.Cal;
end
if ~isfield(c_, 'MAmean') || ~isfield(c_, 'xi') || ~isfield(c_, 'CArray')
    error('calibration file must contain MAmean, xi and CArray');
end
MAmean = c_.MAmean;
xi     = c_.xi;
CArray = c_.CArray;

fprintf('MAmean : %s %s\n', class(MAmean), mat2str(size(MAmean)));
fprintf('xi     : %s %s\n', class(xi),     mat2str(size(xi)));
fprintf('CArray : %s %s (complex=%d)\n', class(CArray), mat2str(size(CArray)), ~isreal(CArray));

fid = fopen(fullfile(outDir, 'MAmean.bin'),    'wb'); fwrite(fid, double(MAmean(:)), 'double'); fclose(fid);
fid = fopen(fullfile(outDir, 'xi.bin'),        'wb'); fwrite(fid, double(xi(:)),     'double'); fclose(fid);
fid = fopen(fullfile(outDir, 'CArray_re.bin'), 'wb'); fwrite(fid, real(double(CArray(:))), 'double'); fclose(fid);
fid = fopen(fullfile(outDir, 'CArray_im.bin'), 'wb'); fwrite(fid, imag(double(CArray(:))), 'double'); fclose(fid);

fprintf('written to %s : MAmean.bin xi.bin CArray_re.bin CArray_im.bin (float64)\n', outDir);
