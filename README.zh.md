# DSH Dock for Visual Studio

**把 DeepSeek Harness 的对话放进 Visual Studio 工具窗口。**

DSH Dock 把 DSH 自己的对话界面放在正在处理的代码旁边：由 QuickDock 提供页面，用 WebView2
显示在工具窗口里——工具卡片、审批、模型与权限控制，和 VS Code、JetBrains 的 dock 是同一个
页面。点对话里的文件路径会在 Visual Studio 中打开。它是 **dsh-quickdock**（运行在 DSH 里的
插件）的客户端。

这个第一版是 dock 窗口，外加**只读**的编辑器工具：agent 能看到解决方案、读文件、搜索、
读诊断和符号、把文件打开到某一行。写入工具和审阅 diff 是下一个里程碑。

## 功能

- **真正的对话，就在工具窗口里。** **视图 → 其他窗口 → DSH Dock**：DSH 自己的界面放在
  解决方案资源管理器旁边，不是另写的一份。
- **完整的 QuickDock 壳页。** 新建会话、侧边栏、会话标题、切换回主窗口等整个标题栏；dock
  跟随 DSH 绑定的会话。
- **文件在 Visual Studio 里打开。** 工具卡片里的路径会在 IDE 中打开；dock 报相对路径时按
  当前解决方案目录解析。
- **把代码发给对话。** 右键文件或选区 → *Add to DSH Conversation*；输入框出现 `@路径 (lines a–b)`，
  agent 从该位置开始。
- **给 agent 的编辑器工具。** 打开 dock 标题栏的**插头**：扩展会启动一个回环 MCP server，
  QuickDock 把它挂到解决方案目录上，agent 就得到一组 `mcp__visualstudio__*` 工具——工作区
  目录、选中内容、读文件、列文件、文本搜索、Error List、文件符号、把文件打开到某一行。
- **按解决方案生效。** 只有工作目录在解决方案目录里的会话才会挂载工具；会话在别处时，
  标题栏出现琥珀色警告三角，「列表」按钮显示发布了什么、挂载了什么。
- **自动审阅 diff。** DSH 自己的文件工具写入或新建文件时，在 Visual Studio 的对比窗口里打开
  修改前/后的 diff。只有 DSH 上报的写入会进来，构建、格式化、shell 命令不会；未保存的缓冲区、
  二进制和大文件会跳过。可在 工具 → 选项 → DSH Dock 里关闭。
- **不用重启 VS 就能重载。** 工具窗口自带的工具栏里有一个 Reload 按钮：重新解析 QuickDock
  端口并重载壳页，DSH 重启后不需要重启 IDE。
- **不会只剩空白。** DSH 或 QuickDock 连不上时，卡片会说明尝试的地址以及端口来源，并提供
  Retry、启动 DSH、在浏览器打开、连接设置、QuickDock 日志、采用 `config.json` 里的端口。
- **设置页。** **工具 → 选项 → DSH Dock**：QuickDock 端口（0 表示跟随配置）、是否跟随
  `config.json`、是否随 dock 启动编辑器工具、编辑器工具端口。
- **自动找监听端口。** 依次读 `DSH_QUICKDOCK_PORT`、QuickDock 的 `config.json`、`47910`，
  和其他 dock 一致。
- **用 WebView2**，Windows 和 Visual Studio 自带，不需要额外装浏览器运行时。

## 几个 dock 怎么配合

DSH Dock for Visual Studio 是同一个页面的三个编辑器 dock 之一。**它要求 DSH 安装
[dsh-quickdock](https://github.com/CoplenSasbian/dsh-quickdock) 插件**——QuickDock 负责提供
对话页面，以及本窗口读取的回环监听。

| 项目 | 运行在 | 是什么 |
|---|---|---|
| [dsh-quickdock](https://github.com/CoplenSasbian/dsh-quickdock) | DSH | 宿主：把对话作为回环页面提供出来，并提供编辑器 dock 注册工具的 MCP 桥。**每个编辑器 dock 都依赖它。** |
| [DSH Dock for VS Code](https://github.com/CoplenSasbian/dsh-vsdock) | VS Code | 同一页面放进辅助侧边栏，外加 VS Code 编辑器工具。 |
| [DSH Dock for JetBrains IDEs](https://github.com/CoplenSasbian/dsh-intellijdock) | IntelliJ IDEA、CLion、PyCharm、WebStorm、GoLand 等 | 同一页面放进工具窗口，外加 JetBrains 编辑器工具。 |
| [DSH Dock for Visual Studio](https://github.com/CoplenSasbian/dsh-visualstudio) | Visual Studio 2026 | 同一页面放进工具窗口，外加 Visual Studio 编辑器工具（目前只读）。 |

这些编辑器 dock 可以同时连同一个目录：工具命名空间（`vscode_*`、`mcp__<ide>__*`）不会冲突。

## 环境要求

- Windows 上的 Visual Studio 2026（18.x），带 WebView2 运行时（Windows 11 和 Visual Studio
  自带）。
- DSH（桌面版或 `dsh web`）安装 **dsh-quickdock 0.6.0+**——它提供 `visualstudio` 视图。
- QuickDock 监听地址可达：`http://127.0.0.1:47910`（DSH → **设置 → QuickDock → 连接**）。
  端口为 `0` 表示监听已关闭。

## 安装

双击 [Releases](https://github.com/CoplenSasbian/dsh-visualstudio/releases) 里的 VSIX（或对它
运行 `VSIXInstaller.exe`），重启 Visual Studio，然后打开 **视图 → 其他窗口 → DSH Dock**。

## 许可

MIT，见 [LICENSE](LICENSE)。
