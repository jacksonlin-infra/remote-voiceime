#!/usr/bin/env python3
"""Shared release identity and source/artifact checks for native builds."""
import hashlib
import json
from pathlib import Path
import plistlib
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
META = {node.tag: node.text for node in ET.parse(ROOT / 'resources/Product.props').find('PropertyGroup')}

def source_digest(platform):
    files = [ROOT / 'resources/Product.props', ROOT / 'scripts/product.py']
    files += [ROOT / 'resources/icons' / ('AppIcon.icns' if platform == 'macos' else 'AppIcon.ico')]
    if platform == 'macos':
        files += sorted((ROOT / 'src').glob('*.swift'))
        files += [ROOT / 'resources/Info.plist', ROOT / 'scripts/build.sh']
    else:
        files += [ROOT / 'windows/Directory.Build.props', ROOT / 'scripts/build-windows.sh', ROOT / 'scripts/build-windows.ps1',
                  ROOT / 'scripts/windows-release.ps1']
        for folder in ['Bridge.Core', 'Bridge.Windows']:
            files += sorted(p for p in (ROOT / 'windows' / folder).iterdir() if p.is_file())
        files += sorted((ROOT / 'resources/windows').glob('*.txt'))
    digest = hashlib.sha256()
    for path in sorted(set(files)):
        digest.update(str(path.relative_to(ROOT)).encode() + b'\0' + path.read_bytes() + b'\0')
    return digest.hexdigest()

def build_record(platform, binary):
    return {'product': META['ProductName'], 'version': META['Version'], 'build': META['ReleaseBuild'],
            'platform': platform, 'source_sha256': source_digest(platform),
            'binary_sha256': hashlib.sha256(binary.read_bytes()).hexdigest()}

def main():
    action, *args = sys.argv[1:]
    if action == 'get':
        print(META[args[0]])
    elif action == 'plist':
        value = plistlib.loads((ROOT / 'resources/Info.plist').read_bytes())
        value.update(CFBundleName=META['ProductName'], CFBundleDisplayName=META['ProductName'],
                     CFBundleExecutable=META['ProductName'], CFBundleIdentifier=META['MacBundleIdentifier'],
                     CFBundleShortVersionString=META['Version'], CFBundleVersion=META['ReleaseBuild'])
        Path(args[0]).write_bytes(plistlib.dumps(value))
    elif action in ('stamp', 'verify'):
        platform, binary, record = args
        expected = build_record(platform, Path(binary))
        if action == 'stamp':
            Path(record).write_text(json.dumps(expected, ensure_ascii=False, indent=2) + '\n')
        elif json.loads(Path(record).read_text(encoding='utf-8-sig')) != expected:
            raise SystemExit(f'{platform}: 构建与当前源码或版本不一致，请重新构建后再打包。')
        else:
            print(f'{platform}: source, product, version and binary verified')
    else:
        raise SystemExit('Usage: product.py get FIELD | plist PATH | stamp/verify PLATFORM BINARY RECORD')

if __name__ == '__main__':
    main()
