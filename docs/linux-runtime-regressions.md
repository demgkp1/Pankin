# Linux 运行时问题修复（2026-11-13）

## 修复范围

1. **AppImage 自启动**：自启动文件改为无 BOM UTF-8、按 Desktop Entry 规则转义参数、使用持久的 `APPIMAGE` 路径并携带 `--autostart`。兼容 framework-hosted `dotnet <dll>` 启动；禁止登记临时 AppImage 挂载路径。启动时刷新已启用的旧条目，设置开关反映实际文件状态，写入失败不会把未成功的注册显示为开启。
2. **面板点击误关闭**：屏幕点击通过 `PointToScreen` 与物理窗口范围比较，不再把屏幕像素和 DIP 尺寸混用。Wayland 下不使用 SharpHook 的 X11 坐标判断外部点击，改用延迟检查真实失焦；窗口内交互、拖动及临时焦点切换不会立即隐藏。修复同样用于超级面板的失焦处理和划词位置过滤。
3. **AppImage 搜索**：继续优先解析 `.desktop`（支持 XDG 应用目录），另外扫描 `~/app`、`~/Applications`、`~/AppImages`、下载目录和 `~/.local/bin` 的可执行 AppImage。扫描深度限制为两级，过滤无执行权限文件，去重已被 `.desktop` 登记的同一文件，生成稳定 ID；直接启动发现的 AppImage，不经过 shell 解释文件名。未提取 AppImage 内嵌图标，优先使用同名 PNG/SVG，否则使用通用应用图标。
4. **文本剪贴板历史**：桌面端向 Linux `IClipboardService` 实现注入 UI 线程上的 Avalonia 原生读写，因此文本历史不依赖 `xclip`。监听记录初始剪贴板、后续变化、清空后重新复制，抑制自身粘贴造成的重复；对原生轮询和显式读写串行化，避免 X11 异步 selection 请求相互干扰。GNOME Wayland 后台划词采样不调用可能抢焦点的 `wl-paste` 回退。

`Pankin.Core` 未增加 OS/UI 依赖；Windows 参考源码未修改。

## 自动化验证

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build Pankin.sln
dotnet test Pankin.sln --no-build
```

本机结果：构建 0 warning / 0 error，Core 374 项、Linux 14 项单元测试全部通过。Linux 测试覆盖自启动文件、GLib/GIO 实际参数解析（空格、引号、美元和百分号）、发现/搜索/去重/直接启动 AppImage、初始复制入 SQLite、监听停止/恢复、暂态失败和原生读写串行化。

### 隔离原生窗口验证

新增验证器入口：

```bash
PANKIN_ISOLATED_DISPLAY=1 dotnet run \
  --project tests/Pankin.Launcher.Verification -- --runtime-regressions
```

**必须使用独立 X server（例如 Xvfb）的 DISPLAY**，不要将此命令运行在用户真实桌面上。验证器会产生原生鼠标点击与测试剪贴板内容；设置 HOME、XDG_CONFIG_HOME、XDG_DATA_HOME 为临时目录，关闭网络更新和建议请求，并在退出时清理。隔离会话应取消 `WAYLAND_DISPLAY`、`APPIMAGE` 和 `DBUS_SESSION_BUS_ADDRESS`，设置 `XDG_SESSION_TYPE=x11`、`XDG_CURRENT_DESKTOP=PankinTest`，以免修改用户 GNOME 热键配置。裸 Xvfb 没有桌面进程创建剪贴板 atoms，验证器在启动 Avalonia 前会补齐标准 atoms。

本机独立 Xvfb：实际 RenderScaling 1 与 2，各 **14 项**通过。验证器运行真实 `App` 初始化流程（不是第二套 UI），检查：

- `--autostart` 不自动显示任何面板，但面板仍可打开；
- 无 `xclip`/`wl-paste` 时，原生文本复制仍进入 SQLite 并显示于剪贴板窗口；
- 默认 `~/app` 目录中的隔离可执行 AppImage 测试项能被启动器搜索到；
- 物理边界判断正确，真实面板内鼠标点击不关闭窗口，外部点击可以关闭；
- 测试鼠标事件确实经过全局输入钩子。

日志保存于 `artifacts/runtime-regressions/scale-1.log` 与 `scale-2.log`。本机已有旧 Pankin 使用 UDP 49182，测试进程记录 `Address already in use`；X11 全局钩子及验证仍通过，没有停止旧应用或修改其 IPC/桌面设置。

## 使用新版与限制

打包脚本为 AppDir 补齐 `.DirIcon`（可用时生成 PNG），并优先使用 `.tools/runtime-x86_64` 的本地 AppImage runtime，避免重建受 GitHub 在线下载影响；也可通过 `APPIMAGE_RUNTIME_FILE` 指定缓存文件。

新版源码、更新检查器、设置页和 AppImage/deb 统一为 0.3.2，文件位于 `artifacts/`。本次不自动替换 `~/app` 中用户正在运行的旧 AppImage、不安装 deb、不注销或重启系统。更新时先退出旧应用，再替换固定路径的文件或启动新版包；已启用的旧自启动条目会在新版启动时刷新。

- 已验证 Desktop Entry 实际启动参数和真实应用后台启动。0.3.1 AppImage 在独立 Xvfb 中通过冒烟验证：进程正常运行、自动生成的无 BOM 自启动条目指向真实 AppImage、4 个原生面板窗口保持未映射（日志 `artifacts/release-0.3.1/appimage-smoke.log`）；**实际开机/注销登录后的自启动尚需人工确认**。
- 隔离原生窗口验证运行于 X11；未声称完成真实 GNOME Wayland 合成器端到端验证。Wayland 分支避免不可靠全局坐标，依赖窗口真实失焦事件。
- 本次修复文本历史读取链路，不新增图片或文件剪贴板采集能力，也不能恢复此前未记录的历史。
- GNOME Wayland Primary Selection 仍依赖可用的静默 X11 后端；无 `xclip` 时不再通过抢焦点的 `wl-paste` 临时窗口回退。

## 0.3.2 失焦隐藏补修

0.3.1 的延迟判断仍把 `IsPointerOver` 当作焦点条件，并丢弃刚激活后的失焦事件，导致鼠标未移动的应用切换和快速失焦漏隐藏。0.3.2 修正这些条件并增加真实原生焦点转移回归；完整说明及隔离验证方法见 [失焦隐藏回归报告](focus-loss-regressions.md)。先前 0.3.1 的上述冒烟检查不等于已覆盖这些失焦场景。
