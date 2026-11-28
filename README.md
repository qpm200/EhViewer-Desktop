# EhViewer Desktop

E-Hentai / ExHentai Windows 桌面客户端，使用 .NET 10 与 WinUI 3 开发。

## 主要功能

- 画廊列表浏览、多条件搜索、以图搜图、快速搜索与本地规则过滤
- 云端收藏夹分类与本地收藏管理、浏览与阅读历史记录
- 单页 / 双页 / 条漫阅读器，支持缩放平移、预加载、缩略图预览与断点续读
- 多线程下载队列、断点续传、归档下载与本地图库扫描导入
- Classic Material / MIUIX 双主题、Mica / Acrylic 窗口材质、托盘、老板键与应用锁
- 可选本地 OCR 识别与漫画翻译（支持 llama.cpp 本地推理或 OpenAI 兼容接口）

## 运行与构建

- 系统要求：Windows 10 1809 (17763) 及以上或 Windows 11（x64 / ARM64）
- 直接使用：从 Releases 下载独立运行单文件版 `EhViewer.Desktop.exe`，无需额外安装运行时。
- 源码构建（需 .NET 10 SDK）：

```powershell
dotnet build EhViewer.Desktop.slnx -c Release -p:Platform=x64
dotnet test tests/EhViewer.Core.Tests/EhViewer.Core.Tests.csproj -c Release --no-build
dotnet test tests/EhViewer.Desktop.Tests/EhViewer.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-build
```

打包单文件版本：

```powershell
dotnet publish src/EhViewer.Desktop/EhViewer.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages="zh-Hans%3Ben" -p:DebugType=None -p:DebugSymbols=false -o publish/win-x64
```
