# NVevaAce

**一款内网穿透软件**

## 简介
NVevaAce 是一款专为 Windows 平台设计的内网穿透工具，支持自定义本地端口，将流量安全地转发到指定的公网服务器。它提供简洁的 UI，用户可以直接在主窗口输入要映射的端口并启动/停止穿透。

## 功能特性
- 自定义本地监听端口
- 将流量转发到配置好的远程服务器（在 `appsettings.json` 中配置）
- 实时进程日志显示，帮助排查问题
- 自动化打包为单文件可执行程序（Windows x64）
- GitHub Actions 自动构建并发布 Release

## 项目结构
```
NVevaAce/
├─ NVevaAce.sln                # Visual Studio 解决方案文件
├─ NVevaAce/                    # 主项目目录
│   ├─ NVevaAce.csproj          # .NET 项目文件（net6.0-windows）
│   ├─ Program.cs              # 程序入口
│   ├─ MainForm.cs             # WinForms 主窗体（启动按钮、端口输入、日志）
│   ├─ MainForm.Designer.cs    # 设计器生成的 UI 代码
│   └─ appsettings.json        # 配置文件（远程服务器地址等）
├─ logs/                       # 运行时日志目录（示例）
├─ .github/workflows/          # CI/CD 工作流
│   └─ build.yml                # 自动编译并发布 Release
├─ .gitignore                  # Git 忽略文件
└─ LICENSE                     # MIT 许可证
```

## 快速开始
1. **克隆仓库**（已在 GitHub 自动创建）
   ```bash
   git clone https://github.com/<your‑username>/NVevaAce.git
   cd NVevaAce
   ```
2. **配置远程服务器**
   编辑 `appsettings.json`，填写您公网服务器的 `RemoteHost` 与 `RemotePort`（默认占位 `tunnel.example.com:443`）。
3. **编译**
   ```bash
   dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true
   ```
   可执行文件会生成在 `bin/Release/net6.0-windows/win-x64/publish/` 目录下。
4. **运行**
   双击生成的 `NVevaAce.exe`，在窗口中输入本地端口，点击 “启动内网穿透”。日志会实时显示连接状态。

## GitHub Actions 自动构建
仓库中已配置工作流 `build.yml`，每次 push 到 `main` 分支后会自动编译，并将生成的单文件 exe 作为 Release 附件上传。无需手动打包。

## 许可证
本项目采用 **MIT 许可证**（见 `LICENSE` 文件），您可以自由使用、修改并分发。
