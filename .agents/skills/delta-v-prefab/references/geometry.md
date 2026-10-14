# 建筑位置、实际占地与旋转

依据 RimWorld 1.6 `GenAdj.OccupiedRect`／`AdjustForRotation` 和 `PrefabUtility`。版本变动时重新核对原版源码和程序集；这里的公式用于制作期解释与坐标求解，不替代游戏内入口。

## 四种不同的坐标

| 名称 | 含义 |
|---|---|
| 布局 origin `(a,b)` | 制作者选定的**旋转后实际占地**西南格，仅是方便编辑的约定 |
| 建筑 root / `Thing.Position` | 原版建筑锚点；输出到 `thingStates/li/pos`，不普遍是角格 |
| 占地矩形 / cells | `GenAdj.OccupiedRect(root, rotation, def.size)` 的结果 |
| 画面中心与外延 | 真实矩形中心或 Graphic 的绘制位置/尺寸；不能拿来填 root |

局部平面为 x 向东/图右、z 向北/图上，y=0。Rot4：0=North，1=East，2=South，3=West。Def 的 `size=(w,h)` 是朝北尺寸；East/West 的实际占地尺寸为 `(h,w)`。

## 奇偶尺寸如何决定 root

对于正尺寸，`GenAdj.OccupiedRect` 先调整尺寸与锚点，再用以下整数关系求矩形西南角：

```text
minX = adjustedRootX - floor((actualWidth  - 1) / 2)
minZ = adjustedRootZ - floor((actualHeight - 1) / 2)
maxX = minX + actualWidth  - 1
maxZ = minZ + actualHeight - 1
```

朝北时没有锚点补偿。单轴为奇数，root 在该轴的中间格；单轴为偶数，root 在中间两格中靠西/靠南的一格。因此 1×1 是自身，3×3 是中心格，2×2 是西南角，4×2 是底边靠左的中间格，4×4 是中间四格的西南格。

朝北尺寸旋到 East/West 时**先交换 w/h**；再仅对旋转后为偶数的轴加下面的补偿：

| rotation | 实际宽高 | 偶数实际宽轴的 Δx | 偶数实际高轴的 Δz |
|---|---|---:|---:|
| North / 0 | w,h | 0 | 0 |
| East / 1 | h,w | 0 | -1 |
| South / 2 | w,h | -1 | -1 |
| West / 3 | h,w | -1 | 0 |

奇数轴上的相应补偿为 0。不要把「建筑含偶数边」解释为两个轴都移动一格，也不要在尺寸交换之前判断对应轴的奇偶。

从预定西南格 `(a,b)` 反求 root：

```text
rootX = a + floor((actualWidth  - 1) / 2) - Δx
rootZ = b + floor((actualHeight - 1) / 2) - Δz
```

这也解释了为什么「同一个 root 改 rotation」与「同一块占地保持不动，只修改朝向」不是同一操作。后者需要按上式重新求 root。

## 常用尺寸速查

表中每项是 root 相对旋转后占地西南格 `(a,b)` 的偏移。尺寸列仍写朝北 Def 尺寸；East/West 的矩形长宽会交换。

| Def 尺寸 | North | East | South | West |
|---|---|---|---|---|
| 1×1 | (0,0) | (0,0) | (0,0) | (0,0) |
| 3×3 | (1,1) | (1,1) | (1,1) | (1,1) |
| 2×2 | (0,0) | (0,1) | (1,1) | (1,0) |
| 4×4 | (1,1) | (1,2) | (2,2) | (2,1) |
| 1×2 | (0,0) | (0,0) | (0,1) | (1,0) |
| 2×1 | (0,0) | (0,1) | (1,0) | (0,0) |
| 2×3 | (0,1) | (1,1) | (1,1) | (1,0) |
| 4×2 | (1,0) | (0,2) | (2,1) | (1,1) |
| 3×5 | (1,2) | (2,1) | (1,2) | (2,1) |

2×2 保持占地 `(10..11,20..21)` 不动时：朝北 root=(10,20)，朝东=(10,21)，朝南=(11,21)，朝西=(11,20)。对应四个角依次为西南、西北、东北、东南。

相反，固定 root=(10,20) 后改四向，原版占地分别是：

| 朝向 | x 范围 | z 范围 |
|---|---|---|
| North | 10..11 | 20..21 |
| East | 10..11 | 19..20 |
| South | 9..10 | 19..20 |
| West | 9..10 | 20..21 |

矩形的连续地图中心为 `(minX + actualWidth/2, minZ + actualHeight/2)`；root 格中心是 `(rootX+0.5, rootZ+0.5)`，二者对于偶数边可能相差半格。绘图标签可以用矩形中心，放置坐标仍写整数 root。

## 建筑朝向与整片 prefab 朝向

建筑的局部 rotation、整个 prefab 的放置 rotation、Map 外的 SpacePose 角度互相独立。布局里保存建筑的局部朝向；整片导入时由现有 `MapPrefab` 接口处理 prefab 旋转；宇宙运动不改真实建筑的局部坐标。

整个 prefab 的输入 `position` 也不是所有内容直接相加的左下角。调用方传给 `MapPrefab.Spawn` 的 position 经 `PrefabUtility.GetRoot` 得到 prefabRoot；该 root 是整片旋转后与局部零点对应的角，North=西南、East=西北、South=东北、West=东南。不要在调用方再补一次画布中心偏移。

43×55 prefab 朝北以 `position=(75,0,75)` 导入时，prefabRoot=(54,0,48)。建筑局部 root=(21,0,35) 对应地图格=(75,0,83)。这两个 root 属于不同层，不能混写。

现有重建器的转换关系是：

```csharp
var cell = prefabRoot + PrefabUtility.GetAdjustedThingLocalPosition(def, prefabRot, localRoot);
var placedRot = def.rotatable
    ? prefabRot.Rotated(Rot4.GetRelativeRotation(Rot4.North, localRot))
    : def.defaultPlacingRot;
var occupied = GenAdj.OccupiedRect(cell, placedRot, def.size);
```

地形、foundation 与 roof 的格只走 `GetAdjustedLocalPosition`，没有建筑奇偶锚点：North `(x,z)`，East `(z,-x)`，South `(-x,-z)`，West `(-z,x)`。可旋转建筑的 root 也随之旋转；不可旋转建筑由 `GetAdjustedThingLocalPosition` 额外处理锚点，同时保持 defaultPlacingRot，不要自己给所有建筑再加同一份偶数偏移。

## 怎么验证，而不只是相信表格

编辑坐标若采用西南 origin，先反求 root，再调用真实程序集检查：

```csharp
CellRect rect = GenAdj.OccupiedRect(root, rotation, def.size);
// rect.Min 应等于预定 origin；rect 的格集合应等于预定脚印。
```

整片四向检查应把**所有占地格**按 `GetAdjustedLocalPosition` 旋转，与转换后的建筑原版占地比较；不要只检查 root 点或尺寸数值。不可旋转的非正方形建筑需按保持默认朝向后的几何另行检查碰撞，不能假定其长方形也随整片旋转。

最后读实际建筑 `.OccupiedRect()`、交互点和图中占地框进行对账。更新 Def 后重算，不能沿用旧表中某个建筑的尺寸。
