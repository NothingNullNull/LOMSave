# LOMSave 2.0 自动检查

在项目根目录执行：

```powershell
python -m pip install -r .\tools\requirements.txt
.\tests\Run-Tests.ps1 -GameDir 'E:\SteamLibrary\steamapps\common\LegendOfMortal'
```

`-Python` 可指定 Python 可执行文件。需要 .NET SDK、本地游戏程序集和 BepInEx 5 x86；测试构建始终禁用自动部署，不读取或修改玩家存档。

缺少剧情资料时，入口脚本从本地游戏提取 `back_02_501`、`ch2_4_5_3` 到 `assets/story/`，不分发游戏脚本。游戏更新后可手动重新提取：

```powershell
python .\tools\Extract-TestStories.py --game-dir 'E:\SteamLibrary\steamapps\common\LegendOfMortal'
```

当前基线为 43 项通过、31 个补丁方法目标及参数检查。测试资料与 `results.txt` 位于 `.work/regression-*/`。

## 覆盖范围

- 原生存档与附加档配对、原子替换、覆盖清理和写入失败处理。
- 旧格式反序列化、格式 2 回退、格式 3 无验证快照读取、格式 4 命运事件。
- Lua 跨脚本标量变量、脚本内自动存档边界、读取入口隔离和等待位置。
- 状态快照独立性、差异定位、集合顺序与时间戳、报告和异常回放判定。
- 命运属性/好感/标记、扣点与撤销、多个面板和起始状态校验。
- Harmony 目标方法和注入参数与本地游戏程序集匹配。

历史格式测试使用 `fixtures/format2/LOMSave2.dll` 和 `fixtures/format3/LOMSave2.dll`，在独立进程中生成合成测试档；这些 DLL 是历史 MOD 构建产物，不是游戏程序集，也不应安装到游戏。

## 限制

独立 CLR 无法执行 Unity 内部 ECall，测试中的引擎交互使用替身，不能证明实际 Harmony 安装、场景生命周期、输入或界面正常。MoonSharp 初始化时可能输出 UnityAssetsScriptLoader 的 SecurityException；最终以各项结果和退出码判断，不能把该信息当成游戏实机日志。

`ch2_4_5_3` 边界用例截取前 81 行。更新脚本后若结构改变，应重新选取有效边界，不能直接放宽断言或把用例失效当成游戏兼容性结论。

实机检查见 [游戏更新兼容性检查清单](../游戏更新兼容性检查清单.md)。历史运行证据留在本地 `.work/`，不随仓库分发；当前已知随机状态四项差异没有被测试或报告隐藏。
