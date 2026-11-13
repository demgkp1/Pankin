# 0.3.2 失焦自动隐藏回归报告

日期：2026-11-13。

## 根因与复现

- 0.3.1 的延迟隐藏要求 `!IsPointerOver`。用键盘切换应用时鼠标可能仍在面板上，窗口虽已失焦但被跳过，而且一次性定时器不再检查。
- 三个面板还直接丢弃激活后 180 毫秒内的 `Deactivated` 事件，导致刚打开就切走时永不隐藏。
- 旧隔离测试验证了全局鼠标点击，没有由窗口管理器产生的真实焦点转移；裸 Xvfb 本身没有窗口管理器，因此此前测试未覆盖这个缺陷。

增加原生 `XSetInputFocus` 转移测试后，旧代码在“鼠标保持面板内、焦点切到外部窗口”的场景确定失败。日志：`artifacts/focus-regressions/before-fix.log`。测试只在独立 X server 上运行。

## 修改

- 移除失焦隐藏的鼠标悬停条件以及三处 180 毫秒事件过滤。
- 每个窗口维护一个可取消的 120 毫秒延迟检查，检查真正的窗口激活状态及用户设置。重新激活、隐藏、关闭都会取消旧检查。
- 保留活跃所属窗口/弹出窗口的交互保护，并持续复查，避免关闭弹出窗口后面板滞留。
- 外部点击判断包含已打开的所属右键菜单/弹出窗口的物理屏幕范围。
- 启动器只在最终隐藏前保存位置。所有代码仍遵守 Core / Linux / Desktop 分层，未修改 Windows 参考源码。

## 回归结果

- Core 374 + Linux 14 = 388 项单元测试全部通过。
- 实际 `RenderScaling=1` / `2`：正常 X11 分支各 33 项，禁用全局坐标的安全分支各 34 项，共 134 项原生运行时检查通过。
- 新检查覆盖启动器/剪贴板鼠标未动时的焦点转移、激活后立即失焦、临时失焦后的重新激活、设置关闭和重新开启、超级面板失焦，以及右键菜单打开和离开后的隐藏。原有自启动、AppImage 搜索、剪贴板入库、面板内部点击和外部点击回归继续通过。

日志位于 `artifacts/focus-regressions/scale-1.log`、`scale-2.log` 及对应 `*-no-global-pointer.log`。

## 隔离运行方法

先启动独立 Xvfb，并设置临时 HOME / XDG 目录、该 X server 的 DISPLAY；取消 `WAYLAND_DISPLAY`、`DBUS_SESSION_BUS_ADDRESS`、`APPIMAGE`，设置 `XDG_SESSION_TYPE=x11` 和 `XDG_CURRENT_DESKTOP=PankinTest`，避免影响真实桌面、剪贴板和 GNOME 快捷键。

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build Pankin.sln
PANKIN_ISOLATED_DISPLAY=1 AVALONIA_GLOBAL_SCALE_FACTOR=1 \
  dotnet run --no-build --project tests/Pankin.Launcher.Verification -- --runtime-regressions
```

200% 缩放用 `AVALONIA_GLOBAL_SCALE_FACTOR=2`，并检查输出确实为 `RenderScaling=2`。仅设置 `AVALONIA_SCREEN_SCALE_FACTORS=2` 不能保证实际生效。设置 `PANKIN_REGRESSION_NO_GLOBAL_POINTER=1` 可在 UI 启动后禁用全局坐标分支；它仍是隔离 X11 焦点事件验证，不能宣称为原生 GNOME Wayland 测试。

**不要在用户真实 DISPLAY 上运行这些测试**：测试会主动改变鼠标位置、窗口焦点和测试剪贴板。真实 GNOME Wayland、实际重启自启动及桌面拖动仍需人工确认。

## 0.3.2 发布核验

实际 0.3.2 AppImage 在独立 X server 中通过面板内指针保持、真实失焦隐藏、隐藏后后台常驻和无 BOM 持久自启动路径检查。未替换或停止用户正在运行的旧版。

本地打包、单测及 AppImage 失焦冒烟日志保存于 `artifacts/release-0.3.2/`。发布时先推送源码与新标签，再创建草稿并上传 AppImage、deb 和 SHA256SUMS.txt，核验后发布为正式 Latest；保留旧标签和旧 Release。
