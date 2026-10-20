# Creates (or reuses) the rig-model venv, checks the GPU, prints versions.
#
#   . ./env.ps1          # dot-source to keep the venv active in this shell
#
# The venv lives OUTSIDE the repo, next to the datasets, because it is ~4 GB of
# CUDA wheels and must never be a candidate for the file-search surface.

$ErrorActionPreference = 'Stop'

$VenvRoot = "$PSScriptRoot\..\..\..\DotCharter-local\venv-rig"
$Python = Join-Path $VenvRoot 'Scripts\python.exe'

if (-not (Test-Path $Python)) {
    Write-Host "[env] creating venv at $VenvRoot"
    py -3.13 -m venv $VenvRoot
    & $Python -m pip install --upgrade pip
    # cu128 covers both sm_120 (5070 Ti) and sm_86 (3080) on torch >= 2.7.
    & $Python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
    & $Python -m pip install onnx onnxscript onnxruntime-gpu numpy soundfile transformers
}

# The owner games on the RTX 5070 Ti. Training belongs on the idle RTX 3080.
# torch enumerates CUDA devices FASTEST-FIRST, so the 3080 is torch's cuda:1
# even though nvidia-smi (PCI order) calls it 0. train.py additionally checks
# the device NAME and refuses to run on the wrong card, so this variable is a
# convenience, not the safety net.
$env:CUDA_VISIBLE_DEVICES = '1'
$env:PYTHONIOENCODING = 'utf-8'

& $Python -c @"
import sys, torch
print('python      ', sys.version.split()[0])
print('torch       ', torch.__version__, '(cuda', torch.version.cuda, ')')
try:
    import torchaudio; print('torchaudio  ', torchaudio.__version__)
except Exception as e: print('torchaudio   MISSING', e)
for m in ('numpy', 'onnx', 'onnxruntime', 'transformers', 'soundfile'):
    try:
        print(f'{m:12}', __import__(m).__version__)
    except Exception as e:
        print(f'{m:12} MISSING ({e})')
print('cuda avail  ', torch.cuda.is_available())
for i in range(torch.cuda.device_count()):
    print(f'  cuda:{i}      {torch.cuda.get_device_name(i)}')
"@

Write-Host "[env] use: & '$Python' train.py --data <dir> --frontend clap"
