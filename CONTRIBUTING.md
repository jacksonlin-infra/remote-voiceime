# 贡献指南

感谢关注 Remote VoiceIME。macOS 端使用 Swift / AppKit，Windows 端使用 C# / .NET 10 / WinForms；两端各自原生实现，遵循同一套交互和投送规则。开始前建议先读 [README](README.md) 和[架构说明](docs/架构说明.md)。

## 问题反馈

使用问题先查看 [README 快速上手](README.md#第一次使用)、[macOS 使用与排查](docs/使用与排查.md)、[Windows 使用指南](docs/Windows试用指南.md)和[支持范围与已知限制](docs/支持范围与已知限制.md)。仍有问题时，在 [Issues](https://github.com/jacksonlin-infra/remote-voiceime/issues) 中提供：

- 本地系统与版本、应用版本与构建号（设置诊断页底部）；
- 豆包输入法与远控软件的版本、远端系统；
- 操作顺序、预期结果和实际提示原文；
- 使用直连键盘还是 Deskflow 等共享键盘，普通窗口还是全屏。

投送失败时，请在重启应用前从设置诊断页复制诊断。诊断不含听写正文，但分享截图前请检查是否包含私人内容或设备信息。请用可丢弃的远端文档复现。

安全或隐私相关问题请勿公开提交，见 [SECURITY.md](SECURITY.md)。

## 提交改进

1. 较大的改动（新语音来源、新远控软件、交互变化）请先开 Issue 讨论方案。
2. 先复现问题，再做最小修改；语音来源与远控适配分别实现，不要把某个客户端的特例写进通用捕获和投送逻辑。
3. 运行下文对应平台的检查；涉及快捷键、输入法、剪贴板或焦点的改动，需要在真实豆包 + 远控环境中实测。
4. PR 描述写明：解决的具体问题、最终行为、检查结果，以及未验证的环境。

本仓库由维护者统一整合后发布：PR 通过评审后，可能会与其他改动一起出现在下一次同步提交中，作者署名会保留（`Co-authored-by`）。

提交代码即表示同意以 [MIT 许可证](LICENSE)授权你的贡献。

## 开发环境与检查

| 平台 | 环境 | 检查 |
| --- | --- | --- |
| macOS | Xcode 命令行工具（Swift 5），无第三方包 | `./scripts/test.sh`；界面改动加 `./scripts/preview.sh`；剪贴板后端改动加 `./scripts/test.sh --native-clipboard` |
| Windows | .NET 10 SDK；安装程序需 NSIS 3 | `./scripts/build-windows.ps1`（先核心检查再发布） |
| 在 Mac 上交叉构建 Windows | .NET 10 SDK | `./scripts/build-windows.sh`；`./scripts/test-windows.sh` 可编译核心测试，但部分检查调用 Windows API，完整检查需在 Windows 运行 |

Windows 实机自检：

- `& ".\Remote VoiceIME.exe" --preview-ui <输出目录> [light|dark]`：渲染并检查原生界面，不创建键盘钩子、语音来源或真实投送。
- `& ".\Remote VoiceIME.exe" --verify-clipboard <输出目录>`：真实剪贴板备份 / 写入 / 恢复自检，会短暂替换剪贴板。
- `& ".\Remote VoiceIME.exe" --verify-ime <输出目录>`：浮条切换到豆包输入法自检。
- 投送问题先看 `%LOCALAPPDATA%\RemoteVoiceInput\diagnostics.log`（仅元数据）。

## 源码约定

- 产品名、版本与构建号统一维护在 `resources/Product.props`；两端构建、界面版本与打包路径从此读取。
- macOS Bundle ID、Windows 单实例标识和 `%LOCALAPPDATA%\RemoteVoiceInput` 设置目录沿用旧名，避免丢失旧配置或重复监听，请勿修改。
- 诊断与日志只记录元数据，**不得记录听写正文、剪贴板内容或窗口标题**。
- `scripts/*.ps1` 需能在 Windows PowerShell 5.1 运行：保持纯 ASCII（中文文件名用码位拼接，见 `windows-release.ps1`）。
- 应用文件名含空格：shell 中加引号，PowerShell 调用路径前加 `&`。
- 图标源文件在 `resources/icons/`，在 macOS 执行 `./scripts/build-icons.sh` 重新生成 ICNS / ICO；原生构建直接使用已提交的图标。
- 不提交 `.build/`、`dist/`、`bin/`、`obj/`、系统文件、个人设置或听写正文。.NET 运行时许可证声明保留在 `resources/windows/` 并随 Windows 包发布。
- 截图由 `./scripts/preview.sh`（macOS）和 `--preview-ui`（Windows）从原生组件生成，使用示例文字，步骤见[截图说明](docs/assets/README.md)。

## 打包与发布（维护者）

- 仅发布 macOS：先 `./scripts/build.sh` 或 `./scripts/test.sh`，再 `./scripts/package-macos.sh`，生成 ZIP、`SHA256SUMS.macos-<架构>.txt` 与 `LATEST.macos.txt`。
- 仅发布 Windows：在 Windows 执行 `./scripts/package-windows.ps1`（默认先构建），生成 ZIP、当前用户安装程序和 `SHA256SUMS.windows-win-x64.txt`；可用 `DOTNET_BIN`、`NSIS_BIN` 指定工具路径。
- 两端都已构建时，可在 Mac 执行 `./scripts/package.sh` 联合打包。
- 打包脚本核对源码摘要、二进制摘要和版本，拒绝把旧构建包装成新版；产物在 `dist/`，不要提交到仓库。

两端独立发布，tag 带平台后缀（如 `v0.3.0-macos`、`v0.3.1-windows`），alpha 版本使用 GitHub Pre-release。发布说明维护在 `docs/releases/v<版本>-<平台>.md`；上传 ZIP（Windows 另含安装程序）和对应 SHA-256 清单，并核对上传后的资产摘要与本地一致。版本变更时同步 Info.plist 模板、Windows `app.manifest` 与使用指南，历史发布说明中的版本号不改写。
