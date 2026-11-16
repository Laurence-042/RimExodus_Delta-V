# 网格碰撞离线对照

夹具使用合成网格和固定种子。普通场景使用 24 艘 23×23 网格船，大型基架使用 8 艘 87×87 网格船。两种场景独立运行单格删除、局部批量删除、分散删除和清空 16×16 区域。最后一种会移除四个 8×8 块或一个 16×16 块。

点对网格使用零半径。目标的固定倾角先变换到局部系，然后比较逐格 DDA、两级固定块 DDA 和块级 BVH。目标在 tick 内旋转时，DDA 基线改在保守包围区内枚举格子，固定块和 BVH 共用逐格的曲线接触判定。DDA 是离线参考实现。

网格对网格的固定块与块级 BVH 共用逆变换筛格、连续 SAT 以及旋转区间判定。两船可以同时平移和旋转。验收比较最早接触的全部格子对。

## 运行

在仓库根目录用 PowerShell 执行。程序输出 JSONL，结果和二进制均放在构建目录。

```powershell
$dir = Join-Path (Get-Location) 'Source/obj/GridCollisionBenchmark/post-parry'
$env:CARGO_TARGET_DIR = Join-Path $dir 'target'
cargo +1.94.0 build --release --locked --manifest-path scripts/GridCollisionBenchmark/Cargo.toml
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$exe = Join-Path $env:CARGO_TARGET_DIR 'release/grid-collision-benchmark.exe'
& $exe verify > "$dir/verify.jsonl"
if ($LASTEXITCODE -ne 0) { throw 'Verification failed' }
foreach ($lane in @('point','pair')) {
    & $exe count $lane > "$dir/count-$lane.jsonl"
    if ($LASTEXITCODE -ne 0) { throw "Counting failed for $lane" }
}
for ($i = 0; $i -lt 5; $i++) {
    $lanes = if ($i % 2 -eq 0) { @('point','pair') } else { @('pair','point') }
    foreach ($lane in $lanes) {
        & $exe time $lane $i > "$dir/time-$lane-$($i+1).jsonl"
        if ($LASTEXITCODE -ne 0) { throw "Timing failed for $lane" }
    }
}
```

`count` 和 `time` 的末尾可以加配置名，默认跑两个配置。 `mixed` 每批有六条固定朝向和两条角度随时间变化的查询。 `linear` 保留倾角和双体平移，把每批的角速度设为零。

## 口径

- 独立基准使用穷举逐格区间、凸包扫掠和世界坐标下的凸多边形距离。固定朝向的 TOI 容差为 1e−9 tick。旋转比较保守时间区间，两边区间容差 1e−6 tick。区间无见证时报告未决。点射线在格角沿两个轴同时前进，沿静止轴的边界遵循半开区间。
- 正确性先跑手工几何用例，每条在删除前后运行一次。随后对所有性能输入做删除后交叉验证。点以 DDA 为参考，网格对网格以 8×8 固定块为参考。
- `total_ns` 从第一次删除开始，到同一批八条查询及首命中格恢复完成为止。每批从本组相同初始网格建立索引，测量前执行一次预热查询。 `build_ns` 单独记录冷建索引。计时版通过编译期常量去除计数，结果汇总和 JSON 输出在计时区间外。
- `nodes_visited` 表示上层裁剪测试次数，`blocks_visited` 表示访问或进入末端处理的块／块对。 `candidates` 表示格或格对筛选次数，`exact_tests` 表示逐格探测或末端接触求解次数，`interval_tests` 仅计旋转求解中的时间区间裁剪。 `cells_deleted`、`blocks_emptied`、`index_nodes_updated` 分别记录成功删格、清空块和 BVH 的更新节点。
- 单轮普通场景每种删除方式有 24 批，大型场景有 16 批。五轮轮换后端顺序并交替运行两条查询路径。只用结果通过对拍的配置比较速度，带异常的原始耗时保留供诊断。

## 历史对照

Parry 0.31.1 的独立原型和 C# 桥接已经移除。最初的 [Rust 汇总](../../Source/obj/GridCollisionBenchmark/summary.csv) 与 [Rust 对拍](../../Source/obj/GridCollisionBenchmark/verify.jsonl) 保留了 Parry 和固定块／BVH 的原始比较。复用旧 C# 夹具八条射线的 [计时汇总](../../Source/obj/CollisionVoxelsComparison/summary.csv)、[精确对拍](../../Source/obj/CollisionVoxelsComparison/parity.jsonl) 和 [独立计数](../../Source/obj/CollisionVoxelsComparison/counts.jsonl) 也继续保留。C# 历史结果包含纯几何、空桥接、边界校正及删格到第八条查询结束的总耗时。原始五轮结果与汇总位于同一目录。
