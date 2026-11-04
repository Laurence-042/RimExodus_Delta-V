# 教程出生站制作源

`build.py` 是唯一布局编辑入口，读取安装版与项目 Def 的尺寸和继承链，解析占地／操作格后由同一份记录输出：

- `1.6/TutorialPrefabs/ArrivalStation.xml`：正式可打包 prefab。
- `ArrivalStation.png`：与 XML 对应的布局图。
- `Source/obj/TutorialContent/ArrivalStation.records.json`：原版离线检查输入，不入库。

`prototypes.states` 是从本地 Demo／Demo_0 导出中摘取并清除源图、派系、控制台身份、电网引用后的实例状态。空休眠舱按原版 `Building_Casket`／`ThingOwner` 存档契约补齐，并经真实 Scribe 加载验证。不要删掉内部容器的具体 Class 或空列表，也不要把完整 Scribe 替换成普通 PrefabThingData。

```powershell
python scripts/TutorialContent/build.py
just build 1.6
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/TutorialRegression/run.ps1 -HarmonyPath C:/Users/Laure/.nuget/packages/lib.harmony/2.4.1/lib/net8.0/0Harmony.dll
just build
```

可用 `--data` 指定其他安装目录的 Data。构建依赖 Python Pillow；它只用于制作期示意图，不是 Mod 运行前置。

布局朝北，编辑位置使用旋转后占地西南格，生成器反求原版 root。原版四向／操作格／电网检查使用 `TutorialRegression` 的 1.6 程序集夹具；不运行 Unity 或启动游戏。当前布局是可验收的出生站基础版本，后续课程内容仍按[计划](../../doc/教程开局实施计划.md)施工。
