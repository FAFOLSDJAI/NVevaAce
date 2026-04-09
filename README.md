# NVevaAce - 内网穿透工具

## 简介

NVevaAce 是一款受 frp 启发的内网穿透工具，支持将内网服务安全地暴露到公网。采用 C/S 架构，支持多种协议、认证、加密等高级功能。

## 核心特性

### 已实现
- ✅ TCP 端口映射
- ✅ 多隧道支持
- ✅ 基础认证（Token 认证）
- ✅ 配置热加载
- ✅ 实时日志显示
- ✅ 单文件发布

### 计划中
- [ ] UDP 协议支持
- [ ] HTTP/HTTPS 代理
- [ ] P2P 模式（XTCP）
- [ ] 连接池优化
- [ ] 带宽限制
- [ ] 负载均衡
- [ ] 服务健康检查
- [ ] TLS 加密传输
- [ ] 压缩支持

## 快速开始

### 1. 配置服务器信息

编辑 `appsettings.json`：

```json
{
  "RemoteHost": "your-server.com",
  "RemotePort": 7000,
  "Token": "your-auth-token",
  "Protocol": "tcp",
  "UseEncryption": false,
  "Tunnels": [
    {
      "LocalPort": 8080,
      "RemotePort": 8080,
      "Protocol": "tcp"
    }
  ]
}
```

### 2. 运行程序

```bash
dotnet run
```

或发布为单文件：

```bash
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true
```

## 与 frp 对比

| 特性 | frp | NVevaAce |
|------|-----|----------|
| TCP 支持 | ✅ | ✅ |
| UDP 支持 | ✅ | ❌ |
| HTTP 代理 | ✅ | ❌ |
| P2P 模式 | ✅ | ❌ |
| 认证机制 | Token/OIDC | Token |
| 加密 | TLS | 计划中 |
| 压缩 | ✅ | ❌ |
| 连接池 | ✅ | ❌ |
| 负载均衡 | ✅ | ❌ |
| 健康检查 | ✅ | ❌ |

## 架构设计

### 当前架构
```.
┌─────────────┐    TCP    ┌──────────────┐    TCP    ┌──────────────┐
│   Client    │──────────>│ TunnelManager│──────────>│ Remote Server│
│  (Browser)  │           │              │           │  (tunnel)    │
└─────────────┘           └──────────────┘           └──────────────┘
     :8080                      :Local                  :Remote
```

### 目标架构（类 frp）
```.
┌──────────┐                          ┌──────────┐
│  frpc    │  ┌───────────────────┐   │   frps   │
│  Client  │  │ Authentication &  │   │  Server  │
│          │─>│ Encryption Layer  │──>│ (Public) │
└──────────┘  │ Connection Pool   │   └──────────┘
              │ Health Monitor    │
              └───────────────────┘
```

## 改进计划

### 第一阶段：修复核心问题
1. ✅ 修复 TunnelConfig 重复定义
2. ✅ 改进 JSON 解析器
3. ✅ 添加认证支持
4. [ ] 实现真正的隧道连接（连接到 frps 服务端）

### 第二阶段：增强功能
- [ ] 实现连接池
- [ ] 添加心跳检测
- [ ] 支持服务注册
- [ ] 添加管理界面

### 第三阶段：高级特性
- [ ] UDP 协议支持
- [ ] HTTP 代理
- [ ] P2P 穿透
- [ ] 带宽控制

## 开发笔记

### 参考 frp 的设计
1. **C/S 架构**：清晰分离客户端和服务端
2. **控制连接 + 工作连接**：控制连接保持心跳，工作连接传输数据
3. **认证层**：每次连接都需要认证
4. **资源管理**：连接池、限流、健康检查

### 当前限制
- 仅支持简单的 TCP 转发
- 缺少与服务端的握手协议
- 没有实现 frp 的控制命令协议

## 许可证

MIT License

## 致谢

灵感来源于 [frp](https://github.com/fatedier/frp) - 一款优秀的内网穿透工具
