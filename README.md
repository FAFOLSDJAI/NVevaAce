# NVevaAce - 内网穿透工具

## 简介

NVevaAce 是一款受 frp 启发的内网穿透工具，采用 C/S 架构，支持将内网服务安全地暴露到公网。采用 .NET 开发，支持多隧道、连接池、心跳检测等高级功能。

## 核心特性

### ✅ 已实现

- **TCP 端口映射** - 支持标准 TCP 协议转发
- **多隧道支持** - 单配置多隧道，同时暴露多个服务
- **连接池优化** - 预建立连接，提高响应速度
- **心跳检测** - 自动检测连接健康状态，支持超时重连
- **配置热加载** - 支持运行时重新加载配置
- **Token 认证** - 基础认证机制，防止未授权访问
- **实时日志显示** - GUI 界面实时显示连接状态
- **单文件发布** - 支持发布为独立可执行文件

### 🚧 开发中

- [ ] UDP 协议支持
- [ ] HTTP/HTTPS 代理
- [ ] TLS 加密传输
- [ ] P2P 模式（XTCP）
- [ ] 带宽限制
- [ ] 负载均衡
- [ ] 服务健康检查
- [ ] 压缩支持

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
  "heartbeatInterval": 30,
  "heartbeatTimeout": 90,
  "tunnels": [
    {
      "localPort": 8080,
      "remotePort": 8080,
      "protocol": "tcp"
    },
    {
      "localPort": 8081,
      "remotePort": 8081,
      "protocol": "tcp"
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

## 与 frp 对比

| 特性 | frp | NVevaAce |
|------|-----|----------|
| TCP 支持 | ✅ | ✅ |
| UDP 支持 | ✅ | ❌ |
| HTTP 代理 | ✅ | ❌ |
| P2P 模式 | ✅ | ❌ |
| 认证机制 | Token/OIDC | Token |
| 连接池 | ✅ | ✅ |
| 心跳检测 | ✅ | ✅ |
| TLS 加密 | ✅ | 计划中 |
| 压缩支持 | ✅ | 计划中 |
| 负载均衡 | ✅ | 计划中 |
| 健康检查 | ✅ | 计划中 |

## 架构设计

### 当前架构

```
┌─────────────┐      TCP       ┌──────────────┐      TCP       ┌──────────────┐
│   Client    │ ────────────>  │TunnelManager │ ────────────>  │Remote Server │
│  (Browser)  │                │              │                │  (tunnel)    │
└─────────────┘                └──────────────┘                └──────────────┘
   :8080                         :Local                        :Remote
```

### 目标架构（类 frp）

```
┌──────────┐
│   frpc   │ ──> ┌───────────────────────────┐ ──> ┌──────────┐
│  Client  │     │ Authentication & Encryption│     │  frps    │
│          │     │ Connection Pool            │     │  Server  │
│          │     │ Heartbeat Monitor          │     │ (Public) │
│          │     │ Health Check               │     │          │
└──────────┘     └───────────────────────────┘     └──────────┘
```

## 开发笔记

### 参考 frp 的设计

1. **C/S 架构** - 清晰分离客户端和服务端
2. **控制连接 + 工作连接** - 控制连接保持心跳，工作连接传输数据
3. **认证层** - 每次连接都需要认证
4. **资源管理** - 连接池、限流、健康检查

### 代码结构

```
NVevaAce/
├── Program.cs           # 程序入口
├── MainForm.cs          # GUI 主界面
├── TunnelManager.cs     # 隧道管理器（核心）
├── TunnelConfig.cs      # 配置模型
├── ConnectionPool.cs    # 连接池实现
├── AppConfig.cs         # 应用配置
└── appsettings.json     # 配置文件
```

### 待改进点

1. **协议层** - 目前仅支持简单 TCP 转发，缺少与 frps 的握手机制
2. **安全性** - 需要实现 TLS 加密传输
3. **协议扩展** - 添加 UDP、HTTP 等协议支持
4. **性能优化** - 异步处理、缓冲区优化
5. **监控告警** - 添加连接状态监控和异常告警

## 许可证

MIT License

## 致谢

灵感来源于 [frp](https://github.com/fatedier/frp) - 一款优秀的内网穿透工具。

## 更新日志

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
