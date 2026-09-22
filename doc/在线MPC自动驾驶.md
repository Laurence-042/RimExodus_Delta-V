# 在线 MPC 自动驾驶

## 目标与边界

`ShipAutopilot` 只编排固定航点/随动目标任务、回调、到达判定和 60 Hz 游戏 tick
积分；制导算法经 `IAutopilotGuidance` 隔离。当前唯一实现 `OnlineMpcGuidance` 每 6 tick
（10 Hz）求一次期望广义力，间隔 tick 保持上一指令。对接完成仍只由
`LandingApproachController` 的位姿窗口与保持 tick 判定，MPC 不感知对接。

所有期望力仍必须经过 `ShipPhysics.ForceToCommand → Allocate → Integrate`。MPC 不写推进器
油门；`BuildControlModel` 只读采样与真实 `Allocate` 相同的分配核心，只有后者提交油门和
火焰新鲜度。终段虚空动力保持既有语义，只补齐期望与可达出力之差。

## 模型

状态为 `[px, py, vx, vy, angleError, relativeAngularVelocity]`，其中位置/速度在目标点
球面切基表达。控制量为归一化 `[fx, fz, torque]`。每次求解按当前质量、惯量、船体角度、
可用推进器和终段虚空动力比例重新线性化为双积分模型；预测期内目标按恒定线速度和
角速度运动，0.1 秒后重新观测。

随动目标点速度统一由 `SpaceTransform.VelocityAtLocalPoint` 计算：宿主锚点速度加
`ω×offset` 后平行输运到目标点。不得退回只匹配宿主中心速度的实现。

QP 使用状态/终端/输入/输入变化率二次成本，以及随距离降低允许闭合速度的软约束。
默认归一化尺度为 10 格、5 格/秒、10°、10°/秒；状态权重
`[4,4,2,2,4,2]`，终端权重 20 倍，输入权重 0.15，输入变化权重 0.75，松弛权重
10000。平移捕获速度 0.15 格/秒、距离斜率 0.2/s；角捕获速度 0.03 rad/s、角度斜率
1.0/s。

## 求解器与预设

`DenseAdmmQpSolver` 是无游戏依赖的纯 C# 稠密 ADMM QP 求解器，固定迭代次数、热启动、
工作区复用。不得按墙钟提前中断，否则同一 tick 状态可能在不同机器产生不同控制结果。
首个控制量最终投影到 `[-1,1]`；有限但尚未达到高精度 KKT 残差的解仍可执行，分解失败、
非有限结果或残差明显发散才切换停止距离回退。

- Fast：16 段、24 次迭代、最长 30 秒。
- Balanced：24 段、40 次迭代、最长 45 秒，默认。
- Precise：32 段、64 次迭代、最长 60 秒。

预设属于 `ShipAutopilot` 实例，不存档、不进入玩家设置。开发者菜单可循环切换；活动任务
在下次制导更新时重置热启动。参考：Wang & Boyd, *Fast Model Predictive Control Using
Online Optimization*；Richards et al., *Model Predictive Control for Spacecraft Rendezvous*。

## 失败与验证

数值失败时使用 `BrakingGuidanceFallback`，按 `v_limit=sqrt(2*a_safe*distance)` 提前制动，
旋转使用同构角停止距离；不会恢复旧饱和 PD。失败/恢复日志只在状态边沿输出。固定航点
最长 120 秒或连续 10 秒无进展后失败回调，不再强制改写位姿。

开发者入口：

- `MPC: cycle developer profile`
- `MPC: diagnostics summary`
- `MPC: deterministic self-test`

纯数值自测覆盖初速 20 格/秒与 0.5、1.0、2.2 格/秒²三档制动能力；游戏内仍需验证
移动/旋转目标、偏置落点、非对称或故障推进器、暂停/倍速、终段虚空动力与火焰一致性。
