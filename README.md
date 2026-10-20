# ShellIsland

Windows 11 任务栏音乐岛，使用 Explorer XAML 注入实现。

## 功能

- 原生任务栏搜索框位置显示音乐岛，空闲恢复原生搜索
- 播放/暂停/停止跟随系统媒体会话
- 大岛：歌词、双语翻译、进度、上一首/播放/下一首、封面、来源应用
- 小岛：圆形封面、歌词/歌名、搜索框尺寸适配
- 支持仅图标 / 图标+标签 / 搜索框三种任务栏搜索尺寸
- 跟随系统深浅色，尺寸变化带过渡动画
- 点击岛打开面板，点击搜索图标打开原生 Windows 搜索
- Explorer 连续重启 3 次时提示并退出

## 构建

需要 .NET 10 SDK、Windows SDK 10.0.26100、Visual Studio C++ 工具链。

```powershell
dotnet publish src\ShellIsland -c Release -r win-x64 --self-contained true
```

TAP DLL 和 Injector 在 `native/XamlTap`、`native/ShellHook` 中构建，发布时需要和 `ShellIsland.exe` 放在同一目录。

## 安装

使用 Release 中的 `ShellIsland-1.0.0-win-x64.zip`，解压后以管理员身份运行 `Install.cmd`。

注入器和 TAP 会被杀软误判，安装脚本会添加 Defender 排除。

## 赞助

如果 ShellIsland 对你有帮助，欢迎支持一下：

<img src="docs/afdian.jpg" alt="爱发电赞助码" width="320" />
