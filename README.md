# ShellIsland

Windows 11 任务栏音乐岛，使用 Explorer XAML 注入实现。

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
