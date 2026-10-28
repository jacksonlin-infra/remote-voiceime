# Remote VoiceIME 应用图标

蓝色圆角方块内，声波通过右向箭头进入屏幕，表达“本地语音文字投送到远端”。图标不依赖文字，在 macOS 和 Windows 共用同一主体。外缘透明；菜单栏仍使用适合系统单色模板的状态符号，Windows 托盘使用缩小的应用图标。

- `AppIcon-source.png`：ImageGen 生成的 1254 × 1254 RGBA 原始输出。
- `AppIcon.icns`：macOS 应用图标，16–1024 像素。
- `AppIcon.ico`：Windows EXE、窗口与托盘图标，16 / 20 / 24 / 32 / 40 / 48 / 64 / 128 / 256 像素。

在 macOS 执行 `./scripts/build-icons.sh`，使用 sips 等比缩放、iconutil 生成 ICNS；Python 仅封装已有 PNG 字节为 ICO，不进行图像编辑。原生构建直接读取已提交的 ICNS / ICO，Windows 不需额外图像依赖。

生成工具：内置 ImageGen，透明背景开启。生成提示词：

> Use case: logo-brand. Create a single production desktop app icon for Remote VoiceIME, a quiet native utility that transfers local voice dictated text into a remote desktop. A precise cobalt blue rounded-square tile with subtle restrained tonal depth, centered white rounded monitor outline containing three bold vertical rounded waveform bars on the left that resolve into a simple rightward arrow entering the screen on the right. One integrated very simple silhouette, legible at 16 to 32 pixels, generous negative space, consistent thick strokes, calm polished macOS/Windows utility aesthetic, front-facing orthographic flat icon, no perspective, no typography, no letters, no numbers, no watermark, no surrounding scene, no sheet of variants. Blue #1265E8 with near-white #F8FBFF. Square 1024x1024 canvas; tile occupies about 84% of width, centered; genuinely transparent outer margins and rounded corners. Very modest highlight on blue tile only, no glass blur, no complicated details, no extra symbols.
