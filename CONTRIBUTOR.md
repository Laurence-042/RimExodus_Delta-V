# 贡献者与鸣谢

## TopologyLab 断裂检查算法

飞船断裂检查采用的 **TopologyLab 增量连通算法来自《新边疆》开发组的浪淘沙**。感谢浪淘沙提供这套算法，让船体在局部受损、尚未断裂时能够低成本完成连通性检查。

原始参考为 `references/TopologyLab/GridTopology.cs`，SHA256：

```
038c5d7181c179918612f6bc209a4575d65c1ed54d7b9689195bde8c3bc628ce
```

Delta-V 的[运行时实现](Source/Core/Topology/GridTopology.cs)从该参考摘取欧拉角点计数、前景／背景并查集、局部连通修复和节点池整理，移除了实验驱动与诊断代码。Delta-V 负责接入真实基架与归属事件、地图缓存生命周期及原有出力分离管线；算法原作者署名不因这些适配而改变。[接入回归](scripts/FoundationTopologyRegression/README.md)与[原算法性能评估](scripts/TopologyBenchmark/RESULTS.md)分别记录正确性和性能证据。

## Steam Workshop 发布要求

**编写或更新 Steam Workshop 介绍时，必须在鸣谢中保留《新边疆》开发组及浪淘沙的署名，不能遗漏，也不能将该算法表述为 Delta-V 原创。** 其他发布页介绍此算法时同样保留来源。

可直接使用：

> 飞船断裂检查采用的 TopologyLab 增量连通算法由《新边疆》开发组的浪淘沙提供，特此感谢。
