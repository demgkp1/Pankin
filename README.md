# Pankin ⚡

<p align="center">
  <strong>Native Modern Desktop Productivity Platform for Ubuntu & Linux</strong><br/>
  专为 Ubuntu 24.04 LTS（GNOME Wayland / X11）深度定制的原生现代桌面效率平台
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Ubuntu-24.04%20LTS-E95420?style=flat-square&logo=ubuntu&logoColor=white" alt="Ubuntu 24.04" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white" alt=".NET 8" />
  <img src="https://img.shields.io/badge/Avalonia%20UI-11.1.3-8B8DFF?style=flat-square" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/Version-0.3.2-blue?style=flat-square" alt="Version 0.3.2" />
  <img src="https://img.shields.io/badge/License-MIT-green?style=flat-square" alt="License" />
</p>

---

## 📖 Introduction / 项目介绍

**Pankin** 是一款基于 **.NET 8** 与 **Avalonia 11** 构建的现代化 Linux 桌面生产力终端。采用原生自研的 **"Graphite Glass"（石墨毛玻璃）** 设计系统，提供流畅丝滑的高斯模糊质感与无缝的多端体验，完美兼容主流 GNOME Wayland 与 X11 会话。

集成了极速应用与全盘启动器、持久化剪贴板历史、全局鼠标手势、鼠标划词智能超级面板、AI 多轮流式对话与划词翻译、目录穿梭与批量文件重命名等全套生产力工具。

---

## ✨ Core Features / 核心功能

* 🚀 **极速启动器 (Fast Launcher)**
  * **智能拼音索引**：支持汉字全拼与首字母模糊匹配（如输入 `wx` 极速命中 `微信`）。
  * **活跃感知动态排序**：自学习使用频率与最近访问权重，高频应用一键直达。
  * **固定磁贴 (Pinned Tiles)**：自定义主屏常驻应用磁贴，支持分类置顶与快速启动。
  * **双擎文件秒搜 (Dual-Engine Search)**：无缝融合系统 `.desktop` 应用索引与 `plocate` 极速全盘文件检索。
  * **即时算式与系统命令**：输入数学算式即时得出计算结果，内置关机/重启倒计时与终端集成。

* 📋 **SQLite 剪贴板历史 (Clipboard History)**
  * 高性能 SQLite 驱动的剪贴板历史管理，支持文本与富文本快照。
  * 搜索、置顶、一键回车粘贴（兼容 Wayland 与 X11 主选区/剪贴板），杜绝 Dock 闪烁。

* ✨ **划词智能超级面板 (Selection Super Panel)**
  * **光标跟随交互**：文本选区后快捷唤出或划词触发，跟随鼠标位置动态定位。
  * **AI 对话与翻译 (AI Stream & Translation)**：内置多轮流式大模型对话与一键划词翻译，实时解析选中文本。
  * **快捷动作组**：划词直达搜索、网页直跳、字符统计与大小写转换。

* 📁 **目录穿梭与批量重命名 (FolderJump & Batch Renamer)**
  * **FolderJump**：快速穿梭常用项目目录，在终端或文件管理器中瞬时定位。
  * **BatchRenamer**：强大的可视化批量重命名工具，支持正则替换、序号追加与前后缀插入。

* 🎨 **"Graphite Glass" 设计系统**
  * 沉浸式毛玻璃半透明效果与平滑圆角，支持深色 / 浅色模式自由切换。
  * 强调色个性化定制，失焦自隐藏，低资源占用与极致流畅帧率。

---

## ⌨️ Default Hotkeys / 默认全局快捷键

| 快捷键 (Hotkey) | 功能 (Action) | 说明 (Description) |
| :--- | :--- | :--- |
| <kbd>Ctrl</kbd> + <kbd>Space</kbd> | **唤出 / 隐藏 主启动器** | 极速应用启动、文件检索、算式计算 |
| <kbd>Alt</kbd> + <kbd>V</kbd> | **唤出 剪贴板历史窗口** | 浏览历史复制记录，支持搜索与快捷回贴 |
| <kbd>Alt</kbd> + <kbd>S</kbd> | **唤出 划词智能超级面板** | 跟随鼠标光标唤出，支持 AI 解读与翻译 |
| <kbd>Alt</kbd> + <kbd>,</kbd> | **打开 系统设置中心** | 模块管理、界面外观、快捷键与 AI 配置 |

*(所有热键均可在「设置中心」中自定义)*

---

## 📦 Installation & Usage / 安装与运行

当前源码版本为 **0.3.2**。安装包以 GitHub Releases 中实际上传的版本为准；以下 0.3.2 文件名也可按下文打包步骤在本地生成。

### 1. 系统依赖环境 (Prerequisites)

以下工具与图标库用于完整功能（文件检索、Primary Selection 划词、命令行剪贴板后端、系统通知及 SVG 图标加载）。桌面端的文本剪贴板历史使用 Avalonia 原生剪贴板，不再要求安装 `xclip` 或 `wl-clipboard`：

```bash
sudo apt update
sudo apt install -y plocate xclip wl-clipboard libnotify-bin libgdk-pixbuf-2.0-0 librsvg2-common
```

### 2. 方式 A：Debian 软件包安装 (.deb，推荐)

准备已发布或本地构建的 `pankin_0.3.2_amd64.deb`：

```bash
sudo apt install ./pankin_0.3.2_amd64.deb
```

安装后即可在应用菜单中找到 **Pankin**，或在终端输入 `pankin` 启动。

### 3. 方式 B：AppImage 免安装运行

准备已发布或本地构建的 `Pankin-0.3.2-x86_64.AppImage`：

```bash
chmod +x Pankin-0.3.2-x86_64.AppImage
./Pankin-0.3.2-x86_64.AppImage
```

- 建议将 AppImage 放在固定目录，例如 `~/app/` 或 `~/Applications/`。启动器同时扫描这些目录以及 `~/AppImages/`、下载目录、`~/.local/bin/`，可搜索没有 `.desktop` 文件的可执行 AppImage。
- 首次运行新版时，已启用的自启动条目会自动重写为当前 AppImage 的持久路径。自启动使用 `--autostart`，登录后仅后台常驻，不自动弹出面板。移动文件后需要手动从新位置启动一次，以更新自启动路径。
- 剪贴板历史会记录启动时已有的文本和之后的新复制；历史监听不能补回此前从未记录的内容。

运行时修复与回归验证说明见 [Linux 运行时回归验证](docs/linux-runtime-regressions.md)。

---

## 🛠️ Architecture & Development / 架构与本地构建

Pankin 遵循严格的三层解耦架构设计，解除了对 Windows 专有 API 的一切依赖：

```
Pankin.sln
├── src/Pankin.Core             # 跨平台核心业务层 (.NET 8 ClassLib)
├── src/Pankin.Platform.Linux   # Linux/Ubuntu 系统级适配层 (.NET 8 ClassLib, SharpHook/plocate/XDG)
├── src/Pankin.Desktop          # Avalonia 11 UI 桌面前端 (MVVM, Graphite Glass)
└── tests/Pankin.Core.Tests     # 核心业务单元测试工程 (xUnit, 349+ Tests)
```

### 本地构建要求

* **OS**: Ubuntu 22.04 / 24.04 LTS (或主流 x86_64 Linux 发行版)
* **SDK**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 编译与测试命令

```bash
# 1. 克隆代码库
git clone https://github.com/demgkp1/Pankin.git
cd Pankin

# 2. 构建整套解决方案 (0 Warning, 0 Error)
dotnet build Pankin.sln

# 3. 运行全量单元测试
dotnet test Pankin.sln

# 4. 本地启动桌面应用
dotnet run --project src/Pankin.Desktop
```

### 自动化打包 (.deb & .AppImage)

执行项目根目录下的自动化打包脚本，即可一键生成自包含 Release 安装包：

```bash
bash scripts/package-linux.sh
```

生成的安装包将存放在 `artifacts/` 目录下：
* `artifacts/pankin_0.3.2_amd64.deb`
* `artifacts/Pankin-0.3.2-x86_64.AppImage`
* `artifacts/SHA256SUMS.txt`（AppImage/deb 校验文件，发布时一并上传）
* `artifacts/pankin.svg`

### 推送 GitHub 与发布 0.3.2

代码推送不会自动上传安装包。请先提交改动并推送 `main` 和 `v0.3.2` 标签，再在 GitHub 新建正式 Release，上传上述两个安装包及 `SHA256SUMS.txt`，并设置为 Latest；保留旧的 `v0.3.0` 和 `v0.3.1` Release。

详细操作见 [GitHub 发布指南](docs/github-release.md)，本次可直接使用的发布说明见 [0.3.2 发布说明](docs/releases/v0.3.2.md)。

---

## 📄 License

本项目采用 [MIT License](LICENSE) 开源协议。
