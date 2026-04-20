# NVevaAce - 内网穿透工具

## 简介

NVevaAce 是一款内网穿透工具，采用 C/S 架构，支持将内网服务安全地暴露到公网。采用 .NET 开发，支持多隧道、连接池、心跳检测等高级功能。

## 核心特性

### ✅ 已实现

- **TCP 端口映射** - 支持标准 TCP 协议转发
- **多隧道支持** - 单配置多隧道，同时暴露多个服务
- **连接池优化** - 预建立连接，提高响应速度
- **心跳检测** - 自动检测连接健康状态，支持超时重连
- **配置热加载** - 支持运行时重新加载配置
- **Token 认证** - 基础认证机制，防止未授权访问
- **实时日志显示** - GUI 界面实时显示连接状态
- **TCP 多路复用** - 单连接多通道，减少连接开销
- **带宽限制** - 基于令牌桶的流量控制
- **负载均衡** - 多后端服务器轮询/最少连接/加权轮询
- **服务健康检查** - TCP/HTTP 端点健康监测
- **TLS 加密传输** - AES-256-CFB + TLS 1.2/1.3 双模式
- **GZip 压缩** - 流式压缩/解压缩集成
- **TCP Mux 实际使用** - 多路复用通道现已实际用于数据传输
- **真实 AES-256-CFB 加密** - 替换伪 XOR 实现
- **自动重连** - 指数退避重连机制
- **统计报告** - 实时流量/连接/运行时长监控
- **单文件发布** - 支持发布为独立可执行文件

### 🚧 开发中

- [ ] UDP 协议支持
- [ ] HTTP/HTTPS 代理
- [ ] P2P 模式（XTCP）
- [ ] Web 管理界面
- [ ] Prometheus 监控接口

## 快速开始

### 1. 配置服务器信息

编辑 `appsettings.json`：

```json
{
  "serverAddr": "your-server.com",
  "serverPort": 7000,
  "token": "your-auth-token",
  "protocol": "tcp",
  "tlsEnable": false,
  "poolCount": 5,
  "tcpMux": true,
  "heartbeatInterval": 30,
  "heartbeatTimeout": 90,
  "tunnels": [
    {
      "localPort": 8080,
      "remotePort": 8080,
      "protocol": "tcp",
      "bandwidthLimit": 1024,
      "healthCheckPath": "/health",
      "healthCheckInterval": 10,
      "loadBalanceStrategy": "round_robin",
      "backends": [
        { "address": "127.0.0.1", "port": 8080, "weight": 1 },
        { "address": "127.0.0.1", "port": 8081, "weight": 1 }
      ]
    }
  ]
}
```

### 2. 运行程序

**开发模式：**

```bash
dotnet run
```

**发布为单文件：**

```bash
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true
```

**发布到指定目录：**

```bash
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true -o ./publish
```

## 配置说明

### 全局配置

| 参数 | 说明 | 默认值 |
|------|------|--------|
| serverAddr | 服务器地址 | 127.0.0.1 |
| serverPort | 服务器端口 | 7000 |
| token | 认证令牌 | - |
| authMethod | 认证方式 (token/oidc) | token |
| protocol | 传输协议 (tcp/kcp/quic/websocket) | tcp |
| tlsEnable | 是否启用 TLS | false |
| poolCount | 连接池大小 | 5 |
| tcpMux | TCP 多路复用 | true |
| heartbeatInterval | 心跳间隔（秒） | 30 |
| heartbeatTimeout | 心跳超时（秒） | 90 |

### 隧道配置

| 参数 | 说明 | 默认值 |
|------|------|--------|
| localPort | 本地监听端口 | - |
| remotePort | 远程服务端口 | - |
| protocol | 协议类型 (tcp/udp/http/https) | tcp |
| useEncryption | 是否启用加密 | false |
| useCompression | 是否启用压缩 | false |
| bandwidthLimit | 带宽限制 (KB/s, 0 不限制) | 0 |
| healthCheckPath | 健康检查路径 | null |
| healthCheckInterval | 健康检查间隔(秒) | 10 |
| loadBalanceStrategy | 负载均衡策略 | round_robin |
| backends | 后端服务器列表 | null |

## 架构设计

### 当前架构

```
┌─────────────┐ TCP     ┌──────────────────┐     ┌──────────────┐ TCP     ┌──────────────┐
│   Client    │────────>│  TunnelManager   │────────>│ Remote Server│────────>│ Local Service│
│  (Browser)  │         │                  │         │   (tunnel)   │         │              │
└─────────────┘         └──────────────────┘         └──────────────┘         └──────────────┘
                              │
                              ├─ ConnectionPool (连接池)
                              ├─ TcpMultiplexer (TCP多路复用)
                              ├─ HealthChecker (健康检查)
                              ├─ BandwidthLimiter (带宽限制)
                              └─ LoadBalancer (负载均衡)
```

## 代码结构

```
NVevaAce/
├── Program.cs              # 程序入口
├── MainForm.cs             # GUI 主界面
├── TunnelManager.cs        # 隧道管理器（核心）
├── TunnelConfig.cs         # 隧道配置模型
├── ConnectionPool.cs       # 连接池实现
├── TcpMultiplexer.cs       # TCP 多路复用
├── BandwidthLimiter.cs     # 带宽限制 + 负载均衡
├── HealthChecker.cs        # 健康检查器
├── ControlConnection.cs    # 控制连接管理器
├── ProxyProtocol.cs        # 代理协议处理器
├── SecureConnection.cs     # TLS 安全连接
├── CryptoUtils.cs          # AES-256-CFB 加密 + 压缩流
├── CompressionUtils.cs     # GZip 压缩工具
├── Logger.cs               # 日志系统
├── ILogger.cs              # 日志接口
├── AppConfig.cs            # 应用配置
└── appsettings.json        # 配置文件
```

## 更新日志

### v0.3.2 (2026-04-20)
- ✅ **Bug 修复**: ConnectionPool 端口参数 bug（`_serverPort = poolSize` → `_serverPort = serverPort`）
- ✅ **Bug 修复**: MuxChannel.WriteAsync 空实现现已实际发送数据
- ✅ **Bug 修复**: EncryptedStream 解密逻辑错误已修复
- ✅ **安全提升**: 替换伪 XOR CFB 为真正的 AES-256-CFB8 加密
- ✅ **新功能**: TCP Mux 多路复用现已实际集成到数据传输流程
- ✅ **新功能**: GZip 压缩现已实际集成到 TunnelManager
- ✅ **新功能**: 运行时统计报告（流量/连接数/速率/运行时间）
- ✅ **新功能**: GitHub Actions CI/CD 自动构建发布流程
- ✅ **改进**: TcpMultiplexer 帧协议更完整（支持 WINDOW_UPDATE/CLOSE）
- ✅ **改进**: 添加日志前缀标识改进可读性

### v0.3.1 (2026-04-16)
- ✅ 添加 TLS 加密传输（TLS 1.2/1.3，支持自定义证书）
- ✅ 添加 GZip 压缩支持（流式压缩/解压缩）
- ✅ 添加 SecureConnection 安全连接管理
- ✅ 添加 GitHub Actions 自动编译发布

### v0.3.0 (2026-04-11)

- ✨ 添加 TCP 多路复用支持，单连接多通道
- ✨ 添加带宽限制器，基于令牌桶算法
- ✨ 添加负载均衡器，支持多种策略
- ✨ 添加后端服务器配置支持
- ✨ 改进 Nagle 算法，减少延迟
- ✨ 改进统计报告，包含带宽信息

### v0.2.0 (2026-04-10)

- ✨ 添加连接池支持，提高连接复用性
- ✨ 添加心跳检测机制，自动检测连接健康状态
- 🐛 修复配置加载逻辑
- 🐛 清理重复代码
- 📝 更新配置格式为驼峰命名

### v0.1.0 (2026-03-25)

- ✨ 初始版本发布
- ✨ TCP 端口映射
- ✨ 多隧道支持
- ✨ Token 认证

## 许可证

MIT License

## 致谢

感谢开源社区。