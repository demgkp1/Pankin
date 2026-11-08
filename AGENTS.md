# Pankin 架构边界与开发规范 (AGENTS.md)

## 1. 项目概述与双目录工作区
- **目标**：将 Windows 效率工具 `LBEILC/Pane` 原生重构并移植到 Ubuntu 24.04 (Linux)，构建集成应用启动器、剪贴板历史、鼠标手势、划词超级面板与 AI 对话的桌面效率工具。
- **只读参考区 (`_reference/Pane-Win`)**：存放原版 Windows WPF 源码，**仅供查阅业务逻辑、数据结构与 UI 布局，严禁对其进行任何修改**。
- **三层架构 (`Pankin.sln`)**：
  - `src/Pankin.Core` (`classlib`)：跨平台核心业务层（4 大跨平台抽象接口、AI 多轮流式对话、翻译服务、算式计算、拼音匹配、SQLite 剪贴板存储与 JSON 配置管理）。不依赖任何特定 OS 桌面环境或 UI 框架。
  - `src/Pankin.Platform.Linux` (`classlib`)：Ubuntu 24.04 系统底层实现（引用 `Pankin.Core`，实现 `.desktop` 扫描、`plocate` 文件检索、Wayland/X11 剪贴板及 Primary Selection 划词、`SharpHook` 全局输入钩子）。
  - `src/Pankin.Desktop` (`avalonia.mvvm`)：Avalonia UI 跨平台桌面前端（引用 `Pankin.Core` 与 `Pankin.Platform.Linux`，实现磨砂玻璃启动器、剪贴板窗口、跟随鼠标超级面板与系统托盘）。

## 2. 架构红线规则
1. **严禁引入 Windows 专属 API**：
   - 在 `src/` 下的所有新工程中，**绝对禁止**出现 `System.Windows.*`、`Microsoft.Win32` 命名空间。
   - **绝对禁止**出现 `user32.dll`、`kernel32.dll`、`gdi32.dll`、`shell32.dll`、`dwmapi.dll` 等 Windows 原生 DLL 的 P/Invoke (`[DllImport]` / `[LibraryImport]`)。
   - 任何平台相关能力必须通过 `Pankin.Core` 中的抽象接口 (`IAppScanner`、`IFileSearcher`、`IClipboardService`、`IGlobalInputHook`) 解耦，并由 `Pankin.Platform.Linux` 实现。
2. **自检闭环**：
   - 每次修改或新增代码文件后，必须执行 `dotnet build Pankin.sln`，确保 **0 Error** 后方可推进下一步。
