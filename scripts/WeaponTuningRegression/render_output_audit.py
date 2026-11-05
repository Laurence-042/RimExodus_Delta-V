"""Render the production-component audit. Requires matplotlib; does not change gameplay."""
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
LOCAL_DEPS = ROOT / "Source/obj/WeaponAuditPython"
if LOCAL_DEPS.exists():
    sys.path.insert(0, str(LOCAL_DEPS))
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager
from matplotlib.colors import LogNorm
import numpy as np

data = json.loads((ROOT / "Source/obj/WeaponTuningRegression/output-audit.json").read_text(encoding="utf-8"))
rows = data["Rows"]
font = Path("C:/Windows/Fonts/msyh.ttc")
if font.exists():
    font_manager.fontManager.addfont(str(font))
    plt.rcParams["font.family"] = font_manager.FontProperties(fname=str(font)).get_name()
plt.rcParams.update({"font.size": 11, "axes.spines.top": False, "axes.spines.right": False,
                     "axes.unicode_minus": False, "figure.facecolor": "#f6f8fb", "axes.facecolor": "#ffffff"})
fig, axes = plt.subplots(2, 2, figsize=(15, 11), layout="constrained")
fig.suptitle("当前武器输出审计｜静止、正入射、全部命中", fontsize=21, fontweight="bold")
ax = axes[0, 0]
for size, label, color in [("Small", "小型激光", "#3275bd"), ("Medium", "中型激光", "#df7932")]:
    group = [r for r in rows if r["Family"] == "Laser" and r["Size"] == size]
    ax.plot([r["PulseWatts"] / 1000 for r in group], [r["SustainedDps"] for r in group], "o-", label=label, color=color)
    for r in group:
        ax.annotate(f'{"小" if size == "Small" else "中"}{r["Power"]}档',
                    (r["PulseWatts"] / 1000, r["SustainedDps"]),
                    xytext=(5, -17 if size == "Small" else 6), textcoords="offset points", color=color)
ax.set(title="激光：功率与输出等比例增长", xlabel="储能侧等效功率（kW，不含待机和充电损失）", ylabel="持续输出预算 / 秒")
ax.set_ylim(0, 530)
ax.legend(); ax.grid(alpha=.2)

ax = axes[0, 1]
for size, label, color in [("Small", "小型", "#3275bd"), ("Medium", "中型", "#df7932")]:
    group = sorted([r for r in rows if r["Family"] == "Rapid" and r["Size"] == size and r["Material"] == "Steel"], key=lambda r: r["Interval"], reverse=True)
    rate = [60 / r["Interval"] for r in group]
    ax.plot(rate, [r["Dps10"] for r in group], "-", color=color, label=f"{label}前10秒")
    ax.plot(rate, [r["SustainedDps"] for r in group], "--", color=color, label=f"{label}完整热循环")
ax.set(title="速射：提高射速压缩爆发窗口", xlabel="设定射速（发 / 秒）", ylabel="输出预算 / 秒")
ax.legend(ncols=2); ax.grid(alpha=.2)

ax = axes[1, 0]
heavy = [r for r in rows if r["Family"] == "Heavy" and r["Size"] == "Medium" and r["Material"] == "Steel"]
grid = np.zeros((96, 96))
for r in heavy:
    grid[r["Charge"] - 1, r["Heads"] - 1] = r["SustainedDps"]
im = ax.imshow(grid, origin="lower", extent=(.5, 96.5, .5, 96.5), aspect="auto", cmap="viridis", norm=LogNorm(vmin=9, vmax=950))
ax.plot([1, 96], [1, 96], "w--", linewidth=1.3)
ax.plot([48, 96, 96], [48, 96, 1], "o", color="#fc644e", markersize=6)
ax.text(43, 52, "默认48/48", color="white", ha="center")
ax.text(92, 90, "96/96", color="white", ha="right")
ax.text(92, 7, "1/96", color="white", ha="right")
ax.set(title="中型重炮：持续输出取决于弹头 / 装药比", xlabel="弹头份数 M", ylabel="装药份数 C")
fig.colorbar(im, ax=ax, label="输出预算 / 秒（对数色阶）", fraction=.045)

ax = axes[1, 1]
group = [next(r for r in heavy if r["Charge"] == c and r["Heads"] == 96) for c in [1, 2, 4, 8, 16, 24, 48, 96]]
pos = np.arange(len(group)); width = .36
ax.bar(pos - width / 2, [r["Dps60"] for r in group], width, color="#3275bd", label="60秒，持续补弹")
ax.bar(pos + width / 2, [r["StockDps60"] for r in group], width, color="#df7932", label="60秒，仅初始满弹仓")
ax.set_xticks(pos, [f'{r["Charge"]}/96' for r in group])
ax.set(title="重弹高输出需要补给；整仓只有12发", xlabel="中型重炮装药 / 弹头", ylabel="60秒平均输出预算 / 秒")
ax.legend(); ax.grid(axis="y", alpha=.2)
fig.supxlabel("2026-11-05｜60 tick = 1秒；冷炮首发在t=0，统计[0,T)；持续值含热锁，尚未扣除飞行时间、脱靶及目标承伤限制。", fontsize=11)
output = ROOT / "scripts/WeaponTuningRegression/results/history/武器输出审计-2026-11-05.png"
output.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(output, dpi=150)
plt.close(fig)
print(output)
