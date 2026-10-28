#!/bin/bash
set -euo pipefail
PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$PROJECT_ROOT"
MAC_ARCH="${ARCH:-$(uname -m)}"
WIN_RID="${RID:-win-x64}"
case "$MAC_ARCH" in arm64|x86_64) ;; *) echo "Unsupported Mac architecture" >&2; exit 1 ;; esac
case "$WIN_RID" in win-x64|win-arm64) ;; *) echo "Unsupported Windows runtime" >&2; exit 1 ;; esac
PRODUCT_NAME="$(python3 scripts/product.py get ProductName)"
VERSION="$(python3 scripts/product.py get Version)"
RELEASE_BUILD="$(python3 scripts/product.py get ReleaseBuild)"
APP="dist/$PRODUCT_NAME.app"
WINDOWS_FOLDER="dist/$PRODUCT_NAME-$VERSION-$WIN_RID"
test -d "$APP"
test -f "$WINDOWS_FOLDER/$PRODUCT_NAME.exe"
python3 scripts/product.py verify macos "$APP/Contents/MacOS/$PRODUCT_NAME" ".build/macos-build-info.json"
python3 scripts/product.py verify windows "$WINDOWS_FOLDER/$PRODUCT_NAME.exe" "$WINDOWS_FOLDER/build-info.json"
test "$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")" = "$VERSION"
test "$(plutil -extract CFBundleVersion raw "$APP/Contents/Info.plist")" = "$RELEASE_BUILD"
lipo -verify_arch "$MAC_ARCH" "$APP/Contents/MacOS/$PRODUCT_NAME"
MAC_FOLDER="dist/$PRODUCT_NAME-$VERSION-macos-$MAC_ARCH"
# A fresh staged folder prevents remnants of a differently named app in a release.
STAGING="$(mktemp -d "$PROJECT_ROOT/.build/package.XXXXXX")"
mkdir -p "$STAGING/$(basename "$MAC_FOLDER")"
ditto "$APP" "$STAGING/$(basename "$MAC_FOLDER")/$PRODUCT_NAME.app"
if [[ -e "$MAC_FOLDER" ]]; then mv "$MAC_FOLDER" "$STAGING/previous-release-folder"; fi
mv "$STAGING/$(basename "$MAC_FOLDER")" "$MAC_FOLDER"
cp .build/macos-build-info.json "$MAC_FOLDER/build-info.json"
cat > "$MAC_FOLDER/macOS试用指南.txt" <<GUIDE
${PRODUCT_NAME} ${VERSION} alpha（构建 ${RELEASE_BUILD}）· macOS

1. 退出当前运行的旧工具，再打开本包中的「${PRODUCT_NAME}.app」。
2. 菜单首行和“关于与数据”应显示 ${VERSION}（构建 ${RELEASE_BUILD}）。解压不会更新已启动的旧进程。
   内部 Bundle ID 延续历史值；权限若失效，在系统设置重新关联辅助功能和输入监控。
3. 支持 UU 远程与微软 Windows App。先验证客户端本身可以本地复制、远端粘贴。
4. Windows App：编辑连接的 Devices & Audio / 设备与音频，允许本地到远端或双向剪贴板。
5. 首次使用按引导完成来源、连接、权限与校准。远端记事本放好光标，再从菜单「设置…」选择客户端，点击「验证投送（不录音）」。
   固定文字核对后轻按右 Ctrl 投送，再从「核对测试」返回界面，人工确认远端结果。
6. 语音使用方式不变：右 Ctrl 开始，说话，再按右 Ctrl 结束；定稿后自动投送。
7. 两个客户端分别保存按键和速度。Windows App 默认 ⌘V + 兼容（1.2 秒）；UU 保留旧设置。
8. 失败从菜单「最近文字…」恢复。「快速恢复」保留子菜单；已发出不代表远端已收到，重发前需勾选核对确认。
9. 排查固定文字失败、语音成功时，可分别复现后点击「复制投送诊断（不含正文）」。报告仅含本机阶段和配置元数据。核对浮条已统一使用右 Ctrl 投送，Return 仅换行。

本包只做本地 ad-hoc 签名。此前 Windows App + 豆包语音转发已获用户实测确认；本轮 UI 更新仍需真实听写与投送复测。
原 1.3 UU + 豆包版已由用户确认基本功能可用；回退源码提交为 965faf2。
完整说明在项目 README.md 与 docs/使用与排查.md。
GUIDE
codesign --verify --deep --strict "$MAC_FOLDER/$PRODUCT_NAME.app"
ditto -c -k --sequesterRsrc --keepParent "$MAC_FOLDER" "$MAC_FOLDER.zip"
python3 - "$MAC_ARCH" "$WIN_RID" "$PRODUCT_NAME" "$VERSION" <<'PY'
import hashlib, json, struct, sys, zipfile
from pathlib import Path
arch, rid, name, version = sys.argv[1:]
dist = Path('dist')
folder = dist / f'{name}-{version}-{rid}'
required = [f'{name}.exe', 'build-info.json', 'Windows试用指南.md', 'DOTNET-LICENSE.txt',
            'DOTNET-THIRD-PARTY-NOTICES.txt', 'WINDOWS-DESKTOP-LICENSE.txt']
for member in required:
    assert (folder / member).is_file(), f'Missing {member}'
binary = (folder / f'{name}.exe').read_bytes()
assert binary[:2] == b'MZ', 'Not a Windows executable'
offset = struct.unpack_from('<I', binary, 0x3c)[0]
assert binary[offset:offset+4] == b'PE\0\0', 'Invalid PE signature'
assert struct.unpack_from('<H', binary, offset+4)[0] == {'win-x64': 0x8664, 'win-arm64': 0xaa64}[rid], 'Wrong Windows architecture'
assert struct.unpack_from('<H', binary, offset+24+68)[0] == 2, 'Expected Windows GUI subsystem'
# Modern singlefilehost statically links CoreCLR. Verify included frameworks
# and managed runtime members rather than requiring a separate coreclr.dll.
for bundled in [b'System.Private.CoreLib.dll', b'System.Windows.Forms.dll', f'{name}.dll'.encode(), b'Bridge.Core.dll']:
    assert bundled in binary, f'Missing self-contained bundle member: {bundled!r}'
config_start = binary.index(b'{\n  "runtimeOptions"')
config, _ = json.JSONDecoder().raw_decode(binary[config_start:config_start+16384].decode('utf-8', errors='replace'))
options = config['runtimeOptions']
frameworks = {f['name']: f['version'] for f in options['includedFrameworks']}
assert frameworks == {'Microsoft.NETCore.App': '10.0.13', 'Microsoft.WindowsDesktop.App': '10.0.13'}, 'Unexpected bundled runtime'
assert 'framework' not in options and 'frameworks' not in options, 'App requires external runtime'
print(f'Checked PE architecture, GUI subsystem and self-contained .NET {frameworks["Microsoft.NETCore.App"]} bundle.')
# Append the suffix: folder names contain the dotted version.
winzip = Path(str(folder) + '.zip')
with zipfile.ZipFile(winzip, 'w', zipfile.ZIP_DEFLATED) as archive:
    for member in required:
        archive.write(folder / member, f'{folder.name}/{member}')
paths = [dist / f'{name}-{version}-macos-{arch}.zip', winzip, folder / f'{name}.exe']
lines = []
for path in paths:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    lines.append(f'{digest}  {path.relative_to(dist)}')
(dist / 'SHA256SUMS.txt').write_text('\n'.join(lines) + '\n')
for path in paths[:2]:
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None, f'Corrupt archive: {path}'
    print(f'Packaged and CRC-verified: {path} ({path.stat().st_size:,} bytes)')
# Check the archive member against the exact signed executable, not just CRC.
mac_binary = (dist / f'{name}.app/Contents/MacOS/{name}').read_bytes()
with zipfile.ZipFile(paths[0]) as archive:
    members = [entry for entry in archive.namelist() if entry.endswith(f'/Contents/MacOS/{name}')]
    assert len(members) == 1 and archive.read(members[0]) == mac_binary
with zipfile.ZipFile(winzip) as archive:
    assert archive.read(f'{folder.name}/{name}.exe') == binary
(dist / 'LATEST.txt').write_text(f'{name} {version}\nmacOS: {paths[0].name}\nWindows: {paths[1].name}\n退出菜单栏 / 托盘中的旧进程后再打开新版。解压覆盖不会更新已运行的进程。\n')
print('Archive executables match the verified builds. Wrote dist/SHA256SUMS.txt and LATEST.txt')
PY
