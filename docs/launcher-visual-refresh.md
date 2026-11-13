# 启动器视觉重构与实机验证

## 范围与参考

- 只读参考：`_reference/Pane-win/Pane/images/launcher-{dark,light}.png`、`launcher-search-{dark,light}.png`，以及 `src/Pane.App/Views/LauncherView.xaml` 和 `Hosting/SceneB.cs`。实际目录大小写是 **Pane-win**，没有下载或修改参考源码。
- 基线工作区干净。基线构建：.NET SDK 8.0.427，0 warning / 0 error；本机 SDK 位于 `~/.dotnet`，需要加入 PATH。
- 仅重构主启动器。其他窗口只共享字体加载修复，以及圆角 0 的设置修复，没有全面重画。
- 不包含替换用户正在运行的 AppImage、安装 GNOME 扩展或修改全局桌面设置。

## 设计变化

| 项目 | 原启动器 | 本次实现 |
| --- | --- | --- |
| 首页主次 | 固定工具在上，最近应用在下 | 最近应用优先，固定工具为次级区域；保留原绑定和右键菜单 |
| 网格 | 94×94 DIP，44 DIP 容器 | 七列，82×86 DIP，36 DIP 真实图标；固定项高度 74 DIP、图标 28 DIP |
| 窗口 | 704×544 DIP | 640×496 DIP，60 DIP 搜索栏、34 DIP 底栏；独立调整密度，不是整体缩放 |
| 搜索 | 横向胶囊、彩色底座、组标题也被选中背景覆盖 | 统一线性图标动作区，50 DIP 结果行，独立组标题，弱化类型与 Alt 序号 |
| 字体 | Inter 名称未真正加载，依赖系统字体回退 | 加载随包 Inter，明确 Noto Sans CJK SC 回退；两行应用名、省略号和完整名称提示 |
| 图标 | 少数硬编码 PNG 路径，缺失时彩色字母 | XDG 主题查找与继承，PNG/SVG/XPM；统一不带品牌的中性缺省图标 |
| 状态 | 主要依赖 Fluent 默认效果 | 中性悬停、强调色键盘焦点、独立选中条、加强按下态，90ms 背景过渡 |
| 材质 | 近黑表面、明显白边与键帽 | 中性灰/暖白表面、低强度高光、细描边、小阴影、辅助级快捷键提示 |

首页方向键按**实际排布的位置**导航，包括最近应用不满一行时跨入固定区。Enter、搜索上下选择、Alt+数字、Ctrl+P、Ctrl+Enter、Alt+逗号、Esc 和右键命令仍走原业务命令。首页 Ctrl+P / Ctrl+Enter 作用于焦点应用，不误操作隐藏的搜索选中项。

## 图标链路与架构

- `IAppScanner.LoadIconPng` 是跨平台默认可选方法；Core 不引用 UI 或 Linux 库。
- `LinuxIconResolver` 在 Platform.Linux 中读取当前 GNOME 图标主题（只读 `gsettings get`，500ms 超时），按 `index.theme` 的 Directories / ScaledDirectories、Size / Scale / Type 和 Inherits 查找。支持用户图标、XDG 数据目录、Flatpak / Snap、hicolor 和 pixmaps；继承循环受保护。
- 使用 GdkPixbuf 及系统 librsvg loader 将 SVG / PNG / XPM 解码成 PNG，再交给 Desktop 创建 Bitmap。没有升级 Avalonia 或引入新的 NuGet 图形库。
- 桌面位图缓存区分 Linux 文件名大小写，96px 源位图覆盖 36 DIP 图标在 200% 下的显示。主题元数据与查找结果缓存至进程退出；切换系统图标主题后重启 Pankin 刷新缓存。
- 缺少原生库、SVG loader 或损坏文件会降级为统一线框图标。Ubuntu 软件包名称为 `libgdk-pixbuf-2.0-0` / `librsvg2-common`；本次未运行安装命令。

## 材质能力与设置

实际环境：Ubuntu / GNOME Shell 46.0，桌面会话 `wayland`；Avalonia **11.1.3** `UsePlatformDetect` 实际创建 **X11 XID** 窗口，通过 XWayland 显示。这不是原生 Wayland Avalonia 后端。

本机窗口的 `ActualTransparencyLevel` 为 **Transparent**。`TransparencyLevelHint="AcrylicBlur, Blur, Transparent"` 只是请求，不能证明合成器提供模糊。

- 当实际报告 Blur / AcrylicBlur 时，不绘制遮蔽底层。
- 其他情况使用独立可读性遮蔽底层和半透明表面；**这是玻璃风格回退，不是真实磨砂**。不采集桌面纹理，不运行背景截图循环。
- 保存的 PanelOpacity 仍映射为表面 tint 的 alpha，没有写回或迁移用户值。回退的底层独立存在：40% tint 时总遮蔽约 94%，94% tint 时约 99.4%（不计微弱高光），以避免清晰背景文字干扰。滑块仍改变背景 tint/透光，但不是回退最终合成透明度的线性百分比。
- 文字与图标不设置整体 Opacity；主题、强调色、圆角 0/16/28 均动态生效。
- 用户真实 `~/.config/pankin/settings.json` 的验证前后 SHA-256 一致；验证使用临时 XDG_CONFIG_HOME / XDG_DATA_HOME，不启动全局输入钩子或剪贴板监听。

### 可选 GNOME 增强（未执行）

可以由用户自行选择与 GNOME 46 匹配的 [Blur my Shell](https://extensions.gnome.org/extension/3193/blur-my-shell/) 版本，在扩展的应用模糊配置页针对 WM_CLASS `pankin` 设置规则，而非对所有应用启用。选项名称随扩展版本变化；XWayland 应用支持、圆角、显卡性能和其他扩展兼容性需在实际桌面验证。

GNOME 扩展施加的效果不一定能反馈为 Avalonia 的 Blur 能力，本实现不会因为安装扩展就擅自移除可读性遮蔽层，因此增强效果可能有限。没有承诺 Windows Acrylic 像素级一致，也没有要求切换桌面环境。

## 本机验证结果（2026-11-13）

- Debug 与 Release 构建均为 0 warning / 0 error。
- 现有 Core 测试：365 / 365 通过。
- 原生窗口检查：100%、125%、200% 各 57 项通过；包含分数缩放下的一物理像素布局舍入容差。
- 每档首屏 14 / 14 个本机应用图标解码成功。没有在 UI 里硬编码应用列表。
- 深浅色首页/搜索、长名称/缺图、键盘焦点、三档透明度和圆角截图均保存于 `artifacts/launcher-verification/` 的对应缩放目录；日志随同保存。
- 打包脚本补充 GdkPixbuf / librsvg SVG loader 的 Debian 依赖声明；`bash -n` 通过。未生成、安装或替换 AppImage / deb。

## 验证入口

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build Pankin.sln
dotnet test tests/Pankin.Core.Tests/Pankin.Core.Tests.csproj --no-build
dotnet run --project tests/Pankin.Launcher.Verification -- artifacts/launcher-verification/native
```

原生验证器使用真实的 `App.axaml` / `MainWindow` / `MainViewModel`，从本机扫描 Linux 应用，执行真实搜索。只在命令分发测试中使用无外部副作用的动作记录器，以及长名称/缺图测试项。不是第二套 UI 或静态 mockup。GUI 会短暂显示；需要 DISPLAY 和 `xwd`。

本机桌面基础缩放为 2，因此分别使用 `AVALONIA_GLOBAL_SCALE_FACTOR=0.5`、`0.625`、`1` 得到实际 RenderScaling 1、1.25、2。该变量是乘数，不应直接假设所有电脑上 `1` 都等于 100%；以验证输出为准。

覆盖项目：

- 真实首页、搜索、七列排布、最近/固定顺序、两行长名称和缺图。
- 方向键、跨不完整行导航、焦点边框、Enter、Alt+1、固定/取消固定、设置入口、Esc。
- 分组标题外的独立选中背景，避免 Fluent 默认选中材质泄漏。
- 浅色/深色，tint 40% / 94% / 100%，圆角 0 / 16 / 28，自定义强调色，文字不透明度。
- 主题继承、循环、用户目录优先、常用尺寸、SVG 高 DPI 栅格化、PNG、XPM、损坏文件。

截图保存为原始 `.xwd` 和转换后的 `.png`，均来自实际 X11 应用窗口。XWD 不包含 GNOME 最终合成的桌面背景。供 Markdown 预览的图片只把原始 alpha 合成到注明的中性底色上，避免某些查看器把半透明白描边误显示为纯白；未重新绘制控件或伪造磨砂效果。

未覆盖：全局快捷键在不同 Wayland 合成器中的权限行为、真实背景 Blur/Acrylic、GNOME 扩展、外部应用实际启动/提权、对打包 AppImage 的重新部署。命令分发用无副作用记录器检查，不在验证中执行系统动作。
