# 构建与打包

需要 Git、.NET 10 SDK 和合法安装的目标版本 Vintage Story。游戏不随源码或模组发布包再分发。

## 本地验证

```powershell
dotnet run --project tests/Tests.csproj
dotnet build Vintage_DGlab/CoyoteMod.csproj -c Release -p:GamePath="C:\Path\To\VintageStory"
```

项目检查 `VintagestoryAPI.dll` 的真实程序集版本，必须与当前分支的 `SupportedGameVersion` 一致。禁止使用 API stub 或其他版本的 DLL 来生成发布包。`GamePath` 默认采用环境变量 `VINTAGE_STORY`，也可通过参数指定。

## 发布包

先提交源码，确保工作区干净，再执行：

```powershell
./scripts/build.ps1 -GamePath "C:\Path\To\VintageStory"
```

脚本固定还原 `packages.lock.json`，运行协议与规则测试，编译真实游戏 API，生成：

- `artifacts/Vintage-DGLab-1.3.0-vs<版本>.zip`：安装用 ZIP。
- 同名 `-source.zip`：该完整 Git 提交的对应源码、构建脚本与许可文件，以及 `third_party/QRCoder/` 的固定版本完整源码。
- 同名 `-SHA256SUMS.txt`：两份 ZIP 的 SHA-256。

每次构建使用新的暂存目录，运行中的测试实例不会锁住新的构建输出。打包采用 DLL 白名单，禁止带入游戏 DLL、API stub、测试程序、账号设置与设备配对会话。`SOURCE.txt` 记录源码完整提交；公开分发二进制时一起附上同提交源码与校验清单。

### Mod DB 安装包

官方 Mod DB 会严格校验 `modinfo.json`，不接受 `tags` 扩展字段。分类标签在网站编辑，不写入清单。

```powershell
./scripts/build.ps1 -GamePath "C:\Path\To\VintageStory" -PackageSuffix moddb
```

此命令给二进制、对应源码和校验清单统一加 `-moddb` 后缀，保留原已发布附件及其校验值。此轮 1.3.0 修订只移除不受支持的清单字段，不改变运行时功能。发布时将三份新附件一同提供。

## 原生 GUI 实测

使用单独的 `--dataPath` 与 `--addModPath` 进入测试世界，避免改变日常存档和模组列表。用正常账户登录游戏；不要把 `clientsettings.json` 或测试数据目录提交到仓库。独立实例与常用游戏安装隔离，连接本身不触发反馈。

1. 用 `.dglab` 打开面板，检查四页控件、二维码首次生成与放大。
2. 用真实 APP 连接设备，核对手机上限与界面 A/B 上限一致。
3. 从“测试 20%”验证数值、结束归零和停止按钮，再验证实际血量变化。
4. 检查暂停、离开世界、断线、静音和复活行为，记录哪些是自动测试、哪些是实机验证。
