# 活侠传自由存档 MOD · 2.0

通过游戏自带的存档/读档界面，在剧情和歇息场景保存当前进度，读档时从剧情边界快速回放到保存位置。也可用 F5/F9 快捷操作，读档后可检查属性和状态是否一致。

## 下载与安装

当前版本 **2.0.4**，适配游戏 `release_1.0.6000.2`。同一套源码提供 BepInEx 5 和 6 两种构建，功能、配置及存档格式相同。[前往下载](https://github.com/NothingNullNull/LOMSave/releases/tag/v2.0.4)。

按已有加载器选择，**只安装其中一种**：

| 加载器 | 一键整合包 | 普通 MOD 包 |
| --- | --- | --- |
| BepInEx 5.4.23.5 Windows x64 | `LOMSave-v2.0.4-BepInEx5-OneClick.zip` | `LOMSave-v2.0.4-BepInEx5.zip` |
| BepInEx 6.0.0-be.692 Unity Mono Windows x64 | `LOMSave-v2.0.4-BepInEx6-OneClick.zip` | `LOMSave-v2.0.4-BepInEx6.zip` |

没有安装过加载器，或继续使用本 MOD 2.0.0～2.0.3 的加载器，可选择 **BepInEx 5**。使用依赖 BepInEx 6 的骰子大师时，选择 **BepInEx 6**。已验证公开的“v3 修正版显示选项结果”样本；其他骰子大师版本及其他 MOD 仍需单独验证。

### 一键整合包（解压安装）

1. 关闭游戏并备份完整存档目录。
2. 下载上表对应的一键整合包，已包含加载器和本 MOD。
3. 将 ZIP 内的全部内容解压到游戏根目录（`Mortal.exe` 所在文件夹），不要额外套一层文件夹。
4. 启动游戏，通过游戏自带的存档/读档界面使用，也可使用下方快捷键。

解压后，`winhttp.dll` 与 `Mortal.exe` 同级，MOD 位于 `BepInEx/plugins/LOMSave2/LOMSave2.dll`。无需运行安装程序；整合包不附带配置、玩家存档或骰子大师。

### 已安装对应加载器

使用上表对应的普通包，解压到游戏根目录即可。也可下载 `LOMSave2-BepInEx5.dll` 或 `LOMSave2-BepInEx6.dll`，**改名为 `LOMSave2.dll`** 后放入 `BepInEx/plugins/LOMSave2/`。移走旧 `LOMSave.dll`，并确保插件目录中只有一份 `LOMSave2.dll`，不要同时装两种构建。

### 切换 BepInEx 5 / 6

关闭游戏，备份整个 `BepInEx` 文件夹及根目录的 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`。将旧 `BepInEx/core`、`BepInEx/cache` 移到游戏目录之外，再解压目标版本的一键包；保留自己的 `config`，其他插件须匹配目标加载器。回退时恢复整套旧加载器和对应 MOD。

5/6 核心不能混装，单改 `target_assembly` 无法完成迁移。旧骰子大师整合包可能附带 x86 加载器，当前游戏为 x64，不要覆盖回旧 `winhttp.dll`。BepInEx 6 以 [官方 692 构建](https://builds.bepinex.dev/projects/bepinex_be/692/BepInEx-Unity.Mono-win-x64-6.0.0-be.692%2B851521c.zip) 为基线，不保证其他开发构建或 IL2CPP 包可用。

每次发布均提供两种一键包、两种普通包、两个独立 DLL 和 `SHA256SUMS.txt`。普通包只含 MOD 和说明；整合包另含对应 BepInEx、第三方许可及相应源码。均不包含游戏程序集、游戏资源或玩家存档。

## 使用方式

**不使用快捷键也能正常存读档。** 打开游戏自带的存档界面，选择槽位保存；需要读取时，在读档界面选择对应存档即可。剧情和歇息场景的普通保存同样记录精确回放资料与验证基准，普通读档同样自动回放并验证。

快捷键只是便捷入口，与游戏的存读档共用同一套机制。F5/F9 操作系统当前槽位；首次还没有当前槽位时，先通过游戏存档菜单选择一个槽位保存。

| 快捷键 | 操作 |
| --- | --- |
| F5 | 覆盖系统当前槽位 |
| F9 | 读取系统当前槽位，标题界面也可使用 |
| F8 | 比较读档后的状态与保存时基准，只读取、不修改数值 |

例如当前槽位是 003，F5 保存的就是 003，F9、“最近存档”和编号 003 都能找到它；切换系统槽位后快捷键也跟随切换。自由行动场景使用原生存读档；剧情和歇息场景额外记录精确回放资料。

剧情中建议在对话或选项暂停处保存。回放期间等到完成提示再操作；场景切换、掷骰和交互面板处理中暂时不能保存。

读档后默认自动验证。默认报告位置：`BepInEx/plugins/LOMSave2/verification/latest.txt`，每次报告另有存档槽与时间对应的历史副本。

- `PASS`：参与比较的值一致，且未检测到回放异常。
- `DIFF`：发现差异，查看报告中的字段、保存值和读取值。
- `INCOMPLETE`：回放或验证未完成，不能视为一致。

`FreeRandomState` 不计入差异。其余差异超过 1 项时显示持续 10 秒的橙红色警告；只有 1 项时保留 `DIFF` 和普通提示。

详细步骤见 [存读档状态验证](docs/存读档状态验证.md)。

## 配置

首次启动后生成 `BepInEx/config/com.nn.lomsave2.cfg`。

| 配置 | 默认值 / 用途 |
| --- | --- |
| `Hotkeys.QuickSave` / `QuickLoad` / `Verify` | F5 / F9 / F8 |
| `Replay.TimeScale` | 50，回放速度倍率，可自行调整 |
| `Replay.ShowOverlay` | true，回放时用不透明黑幕遮住画面，保留进度提示 |
| `Replay.NoProgressTimeoutSeconds` | 45，回放无进度保护的真实秒数；交互和战斗暂停不计入，设为不大于 0 可关闭 |
| `Verification.AutoCheck` | true，回放完成后自动检查 |
| `Verification.ReportDirectory` | 验证报告目录 |

更新 MOD 会保留已有配置。旧配置若仍为 `TimeScale = 12`，关闭游戏后手动改成 `TimeScale = 50`。需要玩家操作的暂停阶段会显示游戏界面，完成操作后恢复遮罩。

回放异常或持续无进度时会停止回放，恢复正常速度和输入，并保留失败报告。此时禁止保存不完整状态；请重新读档，成功读取后恢复正常保存。

## 存档兼容与限制

- 原生 `.dat` 仍由游戏保存；剧情附加档为同名 `.dat.loms`，需要一起备份、移动。
- 当前附加档格式为 4，接受格式 3。旧格式 3 若缺少命运面板记录，需要按原选择人工确认，完成回放后重新保存，才会补入记录。
- 在 BepInEx 5 / 6 构建之间切换不改变存档格式、插件 GUID 和程序集身份，已有 2.0.x 的 `.dat` / `.dat.loms` 无需转换。第三方 MOD 的数值及选项修改仍需用存档副本验证。
- 格式 2、损坏或配对不一致的附加档回退到原生读取；恢复点可能早于保存时的对话位置。
- **旧仓库 1.x 的 `.ss` / `.ss.json` 不会自动转换为 `.loms`。** 保留完整旧档备份，先核对原生读档后的进度，再用 2.0 重新保存；不能保证旧 MOD 的精确对话停点。
- 开发过程中的 2.1.x～2.3.x 已整合为此次正式发布的 **2.0.0**；插件 GUID、程序集身份与附加档格式保持兼容。
- `FreeRandomState`（含嵌入还原存档）不参与一致性判定及警告计数，完整报告标注“已忽略”；此规则不改变实际随机状态，也不保证后续随机结果相同。
- 当前不完整拦截跨游戏版本或脚本变化。游戏更新后先按 [兼容性检查清单](游戏更新兼容性检查清单.md) 用存档副本验证。
- `release_1.0.6000.2` 新增大量任务、角色和事件标记，并调整部分剧情；旧档与新版本快照之间可能出现大量差异。样本能读入不代表所有旧剧情停点都兼容，重新保存也不能反向证明旧档恢复准确。
- UI 动画、语音播放位置，以及 Lua 局部变量、表、函数不在状态快照比较范围内。其他修改相同接口的 MOD 需要单独验证兼容性。

## 从源码编译

需要 .NET SDK、本地游戏程序集和对应的 BepInEx 依赖。默认构建 BepInEx 5；指定 `BepInExVersion=6` 可构建 6。`BepInExDir` 可单独指定加载器的 `core` 目录，默认使用指定游戏目录下的 `BepInEx/core`。

```powershell
$gameDir = '<游戏安装目录的完整路径>'
dotnet build .\LOMSave2\LOMSave2.csproj -c Release -p:Deploy=false "-p:GameDir=$gameDir" -p:BepInExVersion=5
dotnet build .\LOMSave2\LOMSave2.csproj -c Release -p:Deploy=false "-p:GameDir=$gameDir" -p:BepInExVersion=6 '-p:BepInExDir=<BepInEx 6 core 目录>'
```

输出分别为 `LOMSave2/bin/bepinex5/Release/LOMSave2.dll` 和 `LOMSave2/bin/bepinex6/Release/LOMSave2.dll`，中间文件也独立。省略 `-p:Deploy=false` 会复制到指定游戏目录；仅检查编译时保留该参数。

运行自动检查与生成发布包：

```powershell
python -m pip install -r .\tools\requirements.txt
$gameDir = '<游戏安装目录的完整路径>'
.\tests\Run-Tests.ps1 -GameDir $gameDir -BepInExVersion 5
.\tests\Run-Tests.ps1 -GameDir $gameDir -BepInExVersion 6 -BepInExDir '<BepInEx 6 core 目录>'
.\tools\Build-Release.ps1 -GameDir $gameDir
```

测试缺少剧情资料时会从本地游戏提取两个用例，保留在被忽略的 `assets/story/`。发布构建自动准备两套官方加载器依赖，不要求游戏目录已安装加载器；只读取游戏程序集，不部署到游戏。首次下载会校验固定哈希，缓存保留在 `.work/bundle-cache/`，后续可离线复用。

**发布新版本：** 更新版本号、README 与 CHANGELOG，在有游戏依赖的本地环境运行 `tools/Build-Release.ps1 -GameDir $gameDir -OutputDir ./release`，提交生成的全部 **7 个文件**（四个 ZIP、两个 DLL、一个校验文件）。更新记录标题须为准确的 `## 版本号`。GitHub Action 校验两套包齐全、组件和哈希一致后才发布，不在云端编译游戏 MOD；只改源码版本号或重跑旧 Action 不会生成新包。

源码位于 `LOMSave2/`，测试位于 `tests/`，构建工具位于 `tools/`。测试范围见 [tests/README.md](tests/README.md)，双版本验证见 [发布验证记录](docs/2026-10-16双加载器发布验证.md)，版本说明见 [CHANGELOG.md](CHANGELOG.md)。
