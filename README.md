# Vintage-DGLab

Vintage-DGLab 是一个 Vintage Story 客户端模块，它通过本地 WebSocket 将玩家的生命值实时映射到 DG-Lab Coyote 设备。伤害输出会随着连续伤害次数的增加而逐步提升，治疗后会降低，死亡时达到最大伤害输出。它集成了完整的 DG-Lab Socket 协议。

## 命令

| 命令 | 说明 |
|------|------|
| `.dglab-connect` | 启动 WebSocket 服务器 |
| `.dglab-disconnect` | 停止服务器和追踪 |
| `.dglab-track` | 开始追踪血量（连击递增） |
| `.dglab-untrack` | 停止追踪 |
| `.dglab-status` | 查看连接、血量、信号、连击、输出 |
| `.dglab-copy` | 复制 QR URL 到剪贴板 |
| `.dglab-qr` | 显示 QR 链接 |
| `.dglab-port <N>` | 修改端口（默认 8877） |

## 使用方法

1. 先使用 `.dglab-connect` 连接
2. 然后使用 `.dglab-copy` 复制二维码地址
3. 使用一个二维码生成网站生成复制下来的二维码
4. 扫描并连接，然后使用 `.dglab-track` 启动血量跟踪
5. 就可以用了

## 信号机制

- **连续受伤**：每次伤害比上一次更强（连击递增）
- **回血**：连击重置，输出降低
- **死亡**：输出瞬间拉满
- **血量稳定**：输出保持不变

## 安装

1. 下载 `Vintage-DGLab-v1.2.0.zip`
2. 解压到 Vintage Story 的 `Mods` 文件夹
3. 启动游戏

只需要客户端安装，服务器不需要。

## 作者

[B站/Bilibili:Ghostknife](https://space.bilibili.com/7133764)

