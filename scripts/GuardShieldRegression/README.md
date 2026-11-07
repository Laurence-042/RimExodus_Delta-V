# 守卫盾贴图裁剪离线检查

从仓库根运行 `.local-tools/dotnet/dotnet.exe run --project scripts/GuardShieldRegression/GuardShieldRegression.csproj -c Release`。比较耗时时先设置 `$env:DOTNET_TieredCompilation='0'`，避免不同样本落入不同 JIT 层级。

直接编译生产 `GuardShieldOutline.cs`，验证原版贴图四边形／UV 范围、包含、重合去重与相离；200 组确定性随机布局以独立射线法判断点是否在裁剪多边形内，对拍真实圆并集边界，并验证并集内部恰有一个贴图覆盖、枚举顺序不影响面积。当前共 375237 项通过。这里的几何仅用于显示，不参与挡弹。

程序还导出 `Source/obj/GuardShieldPreview/geometry.json`。安装 Pillow／NumPy 后运行 `python scripts/GuardShieldRegression/preview.py`，读取 `_references/RimWorldArt` 的原版 ForceField PSD、按生产多边形和原 UV 采样，生成 `Source/obj/GuardShieldPreview/comparison.png`。单盾、等径／异径交叠、包含、三盾与相离的离线预览已检查；这是固定透明度的几何／贴图预览，不是游戏截图，不能代替 Unity 材质与缩放验收。

2026-11-07 在本机 i7-12700H 上，Release、关闭分层 JIT、预热 100 次后计时 200 次，**整组裁剪重建**的样本如下。只包含 CPU 多边形计算，不含成员状态查询、Mesh 创建／上传、DrawMesh 提交或 GPU 绘制。

| 布局 | 台数 | 中位数 ms | P95 ms | 顶点／三角形 |
| --- | ---: | ---: | ---: | ---: |
| 密集重叠 | 8 | 0.0012 | 0.0020 | 32／16 |
| 密集重叠 | 32 | 0.0239 | 0.0264 | 176／112 |
| 密集重叠 | 128 | 0.4044 | 0.4380 | 754／498 |
| 链式重叠 | 32 | 0.0079 | 0.0089 | 128／64 |
| 相离 | 32 | 0.0068 | 0.0073 | 128／64 |

正常帧复用缓存网格，状态／范围／结构版本变化时才重裁；成员快照比较仍随同船数量平方增长。每个可见裁剪网格一次绘制，选中和受击各有额外绘制；原版材质、采样次数与纹理资源沿用，不引入额外离屏贴图。以上数据只支持“裁剪重建成本较小”，不能据此给出游戏 FPS 或保证优于原版。

容量与武器回归沿用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/WeaponTuningRegression/run.ps1`，本轮 1762 项通过，覆盖满常规手动切应急、充满不自动返回、显式切回、无容量禁切、耗尽后切回及 Scribe 保存的手动状态。此测试不启动游戏、不验证 GPU 输出、Harmony 实际安装、游戏内帧耗时或存读档视觉；验收清单见 [TODO](../../doc/TODO.md)。

圆心修正另由 `RailgunRegression/GuardCenterChecks.cs` 使用构建后的 1.6 生产 DLL 与真实扫掠路径验证：1×1／3×3、四向放置、三种船体角度、实际命中位置与反馈圆心、聚焦 identity／相对位置和角度、重复切换不修改绝对位姿，以及原版 Scribe 往返保存的位置／护盾状态／SpacePose。地图夹具没有区域图，因此直接写入建筑放置旋转；存读档验证不等于完整地图归档测试。运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/RailgunRegression/run.ps1 -GameRoot D:/SteamLibrary/steamapps/common/RimWorld_1.6 -HarmonyPath C:/Users/Laure/.nuget/packages/lib.harmony/2.4.2/lib/net8.0/0Harmony.dll`，本轮完整套件共 628 项通过，其中圆心专项 312 项。两版构建均零警告、零错误。
