# 来源与许可证

本次整理日期：2026-10-23。模组自有代码由 Ghostknife 及贡献者提供，以 **GPL-3.0-only** 发布，并授予 [Vintage Story 链接例外](LICENSES/VintageStory-linking-exception.txt)。完整 GPL 文本见 [LICENSE](LICENSE)。该例外允许模组与用户已安装的游戏及其库一起运行，不授予重新分发游戏文件的权利。

仓库旧版以 MIT 发布。原声明完整保存在 [LICENSES/Legacy-MIT.txt](LICENSES/Legacy-MIT.txt)；旧提交与 `V1.2.0` 标签仍可按当时的 MIT 许可使用。此次选择 GPLv3 是项目作者的发布决定，不表示使用 WebSocket 协议本身会自动产生 GPL 义务。

## 实际打包的第三方组件

| 组件 | 版本 / 来源 | 用途与许可 |
|---|---|---|
| QRCoder | 1.6.0；[上游提交 bd980577](https://github.com/codebude/QRCoder/tree/bd980577640c47f8bb881cf24c8443415a579d36) | 生成二维码矩阵；MIT。安装包包含 `QRCoder.dll` 和 [原始许可全文](LICENSES/QRCoder-MIT.txt)。未修改其代码。 |

构建引用用户安装的 `VintagestoryAPI.dll` 和 `Lib/cairo-sharp.dll`，分别提供游戏 API 与原生 GUI 绘制；均设为 `Private=false`，不放入模组安装包。游戏、Cairo 库、.NET 运行时和 Windows 运行库由原游戏安装提供，本项目不再分发它们。无需 BepInEx、Harmony、Python、WebView 或独立桌面 GUI。

## 协议与实现依据

- [官方 DG-LAB 4 SDK 文档](https://github.com/dungeonlab-open/dglab-kit/blob/main/README_CN.md)：V4 配对链接、消息封装、RPC、设备快照与增量更新。V4 上限是 `slotState.channelA/channelB.intensityMax`；`props.intensityA/B` 是当前强度。
- [官方郊狼波形协议](https://github.com/dungeonlab-open/dglab-bluetooth-protocol/blob/main/coyote/README_CN.md)：郊狼 3.0 的 100 ms 帧，每帧 4 个频率字节随后 4 个幅度字节。
- [官方 Python SDK](https://github.com/dungeonlab-open/dglab-kit-python/tree/4cce771c0fd93d043f3e83bbb0467e4197cde62e)：作为 V4 行为核对资料，GPLv3；没有将该 SDK 或官方中继服务器源文件放入此 C# 模组或其安装包。通信使用 .NET 自带 WebSocket 与 JSON API。
- 原项目对 DGLab-Craft 的引用只作为功能参考记录，不据此断言复制了其代码或推断作者选择 GPL 的原因。本版控制器、状态合并、波形数学预设及原生 GUI 实现在 `src/v13/`。

三个波形预设“气泡节奏 / 平稳 / 呼吸”为本项目数学生成的预设，没有复制官方 SDK 的预设波形表。

许可边界应按实际包含、链接与派生关系判断。参考 [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#MereAggregation) 和所用组件各自的许可证；第三方的 MIT 等许可及署名继续保留，不会被改写为本项目作者的 GPL 声明。

## 二进制对应源码

`scripts/build.ps1` 从干净 Git 提交构建，每个二进制 ZIP 同时生成包含该提交全部构建脚本、测试及许可文件的 `-source.zip`，并生成 SHA-256 清单。源码 ZIP 还包含 `third_party/QRCoder/` 的完整上游源码，固定到上述提交；下载档案 SHA-256 为 `a932985b0aedda8fe999e93733696f3709de80a6a1afde487a581a03d1dbeedd`。ZIP 内的 `SOURCE.txt` 记录完整提交与下载地址。发布时应将二进制、同提交源码 ZIP、SHA-256 清单一起提供。NuGet 版本与包哈希由 `packages.lock.json` 锁定。
