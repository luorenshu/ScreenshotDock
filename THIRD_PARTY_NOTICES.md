# 第三方声明

ScreenshotDock 自有代码采用根目录中的 [MIT License](LICENSE)。第三方组件及其版权、许可证保持原样。

## 应用依赖

| 组件 | 版本 | 版权与许可证 |
| --- | --- | --- |
| [Serilog](https://github.com/serilog/serilog) | 4.3.0 | Copyright © Serilog Contributors；[Apache License 2.0](licenses/Apache-2.0.txt) |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 7.0.0 | Copyright © 2016 Serilog Contributors；[Apache License 2.0](licenses/Apache-2.0.txt) |

上述组件通过 NuGet 还原，应用使用其发布包，未修改组件源码。许可证原文见 `licenses/Apache-2.0.txt`。

## 自包含运行时

Windows x64 自包含发行包包括 Microsoft .NET Runtime 和 Windows Desktop Runtime，由 .NET Foundation and Contributors 提供，采用 MIT 许可证；运行时包含的其他组件按照其随附的第三方声明授权。

发布脚本从本次 NuGet 还原的实际运行时包提取许可原文，连同项目许可证、本文及 Apache 2.0 原文一起放入发行目录和 ZIP：

- `licenses/dotnet-runtime/`：.NET Runtime 的许可证及第三方声明。
- `licenses/windowsdesktop-runtime/`：Windows Desktop Runtime 的许可证。
- `licenses/runtime-packages.json`：该发行包使用的运行时包名与版本。

运行时上游：[.NET Runtime](https://github.com/dotnet/runtime)、[WPF](https://github.com/dotnet/wpf)。发行包中的许可文件对应实际打包版本。

## 开发与测试工具

.NET SDK、Microsoft.NET.Test.Sdk、xUnit 和测试运行器仅用于构建或测试，不打入 ScreenshotDock 应用。它们通过各自的安装包或 NuGet 包提供，并遵循其上游许可证。
