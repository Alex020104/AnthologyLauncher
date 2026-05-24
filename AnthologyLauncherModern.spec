# -*- mode: python ; coding: utf-8 -*-

import sys
from pathlib import Path


project_root = Path.cwd()
python_root = Path(sys.base_prefix)
runtime_binaries = []
for dll_name in ("python312.dll", "vcruntime140.dll", "vcruntime140_1.dll"):
    dll_path = python_root / dll_name
    if dll_path.exists():
        runtime_binaries.append((str(dll_path), "."))


a = Analysis(
    [str(project_root / 'anthology_launcher.py')],
    pathex=[],
    binaries=runtime_binaries,
    datas=[(str(project_root / 'assets'), 'assets')],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='AnomalyLauncher',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir='.',
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=[str(project_root / 'assets' / 'a.ico')],
)
