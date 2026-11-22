# EhViewer Desktop

基于 **.NET 10** 与 **WinUI 3（Windows App SDK）** 构建的 E-Hentai / ExHentai 桌面端应用。

## 功能特性

- **画廊浏览与搜索**：支持首页、热门、订阅、排行榜、标签与上传者检索、以图搜图、高级搜索面板、快速搜索抽屉及本地屏蔽规则过滤。
- **收藏与历史管理**：支持云端多收藏夹分类与本地收藏、批量管理与排序、本地浏览/阅读历史检索。
- **高性能阅读器**：支持单页、双页拼合、长图条漫模式，光标锚定缩放与平移、缩略图预览条、自动预加载及断点续读。
- **下载与本地图库**：支持多线程队列下载、断点续传、标签分组、归档下载、本地目录扫描与压缩包导入。
- **桌面增强体验**：支持 Classic Material / MIUIX 双主题切换、SystemBackdrop（Mica / Acrylic）材质、老板键、Windows 应用锁（Hello / PIN）、系统托盘、任务栏进度条与多窗口浏览。
- **离线 OCR 与漫画翻译**：支持 Windows OCR、ONNX 漫画气泡检测/OCR 管线，以及对接本地 `llama.cpp` 服务或自定义 OpenAI 兼容接口的漫画翻译。

## 项目结构

```text
EhViewer.Desktop/
├── EhViewer.Desktop.slnx
├── Directory.Build.props
├── Directory.Packages.props
├── src/
│   ├── EhViewer.Core/          # 网络请求、Cookie/Hosts/DoH、HTML 解析、SQLite 存储、标签库
│   └── EhViewer.Desktop/       # WinUI 3 桌面壳层、页面、ViewModel、阅读器、下载器与翻译管线
└── tests/
    ├── EhViewer.Core.Tests/    # Core 单元测试与解析基准测试
    └── EhViewer.Desktop.Tests/ # Desktop 单元测试
```

## 环境要求

- Windows 10（19041+）或 Windows 11（x64 / ARM64）
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（仅源码构建时需要；独立发行版已自带运行时）

## 从源码构建与测试

```powershell
# 构建 Release (x64)
dotnet build EhViewer.Desktop.slnx -c Release -p:Platform=x64

# 运行单元测试
dotnet test tests/EhViewer.Core.Tests/EhViewer.Core.Tests.csproj -c Release --no-build
dotnet test tests/EhViewer.Desktop.Tests/EhViewer.Desktop.Tests.csproj -c Release -p:Platform=x64 --no-build

# 打包独立运行发行版 (win-x64)
dotnet publish src/EhViewer.Desktop/EhViewer.Desktop.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o publish/win-x64
```
