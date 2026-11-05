"""Render shield candidate results; all combat/thermal numbers come from the C# audit."""
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
deps = ROOT / "Source/obj/WeaponAuditPython"
if deps.exists():
    sys.path.insert(0, str(deps))
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager
import numpy as np

data = json.loads((ROOT / "Source/obj/WeaponTuningRegression/shield-duration-audit.json").read_text(encoding="utf-8"))
rows = data["Results"]
font = Path("C:/Windows/Fonts/msyh.ttc")
if font.exists():
    font_manager.fontManager.addfont(str(font))
    plt.rcParams["font.family"] = font_manager.FontProperties(fname=str(font)).get_name()
plt.rcParams.update({"font.size": 11, "axes.spines.top": False, "axes.spines.right": False,
                     "axes.unicode_minus": False, "figure.facecolor": "#f6f8fb"})

def select(**filters):
    standard = dict(Guns=4, Shields=2, UnlimitedSupply=True, FullEmergency=True,
                    Powered=True, SplitTargets=False, LaserRechargeWatts=0)
    standard.update(filters)
    return [r for r in rows if all(r[k] == v for k, v in standard.items())]

def one(**filters):
    result = select(**filters)
    assert len(result) == 1, filters
    return result[0]

labels = {"Heavy48": "重炮48/48", "Heavy96": "重炮96/96", "RapidDefault": "默认速射",
          "Laser1": "激光1档", "Laser4": "激光4档", "Mixed": "混合火力"}
fig, axes = plt.subplots(2, 2, figsize=(16, 11), layout="constrained")
fig.suptitle("护盾时长候选 B｜4门中型武器／2台标准盾", fontsize=21, fontweight="bold")
fig.supxlabel("首次盾面失效 ≠ 舰船被击毁；60 tick = 1秒。供弹供能充足、静止正入射、射界内全命中；间歇窗口是输入条件，不是已验证的机动收益。", fontsize=10)
colors = ["#8290a0", "#d6a447", "#3275bd", "#8f64ac"]

ax = axes[0, 0]
names = ["Heavy48", "RapidDefault", "Laser1", "Mixed"]
pos = np.arange(len(names)); width = .19
for index, candidate in enumerate(["Current", "A18k", "B30k", "C42k"]):
    values = [one(Candidate=candidate, Loadout=name, Cadence="Synchronized", Exposure="Continuous")["FirstGapSeconds"] for name in names]
    bars = ax.bar(pos + (index - 1.5) * width, values, width, color=colors[index], label=["现行", "常规1.8万", "常规3万", "常规4.2万"][index])
    ax.bar_label(bars, fmt="%.0f", fontsize=9, padding=3)
ax.axhspan(90, 180, color="#64a682", alpha=.1)
ax.set_xticks(pos, [labels[n] for n in names]); ax.set_ylabel("首次盾面失效（秒）")
ax.set_title("容量扫描：浅绿为首次破盾90～180秒检验窗")
ax.legend(ncols=2, fontsize=10); ax.set_ylim(0, 265)

ax = axes[0, 1]
names = list(labels); pos = np.arange(len(names)); width = .36
for index, cadence in enumerate(["Synchronized", "Staggered"]):
    values = [one(Candidate="B30k", Loadout=name, Cadence=cadence, Exposure="Continuous")["FirstGapSeconds"] for name in names]
    bars = ax.bar(pos + (index - .5) * width, values, width, color=["#3275bd", "#df7932"][index], label=["同步开火", "错峰首发"][index])
    ax.bar_label(bars, fmt="%.0f", fontsize=9, padding=3)
ax.axhspan(90, 180, color="#64a682", alpha=.1)
ax.set_xticks(pos, [labels[n] for n in names], rotation=12); ax.set_ylabel("首次盾面失效（秒）")
ax.set_title("齐射对照：慢重炮留出回血；高功率激光仍很快")
ax.legend(); ax.set_ylim(0, 255)

ax = axes[1, 0]
names = ["Heavy48", "RapidDefault", "Laser1", "Mixed", "Laser4"]
pos = np.arange(len(names)); width = .35
base = [one(Candidate="B30k", Loadout=n, Cadence="Synchronized", Exposure="Continuous")["FirstGapSeconds"] for n in names]
ax.bar(pos - width / 2, base, width, color="#3275bd", label="连续暴露，同步开火")
samples = [[r["FirstGapSeconds"] for r in select(Candidate="B30k", Loadout=n)
            if r["Exposure"].startswith("45on15off")] for n in names]
means = [(min(v) + max(v)) / 2 for v in samples]
errors = [(max(v) - min(v)) / 2 for v in samples]
bars = ax.bar(pos + width / 2, means, width, yerr=errors, capsize=5, color="#df7932", label="45秒有射界／15秒无射界")
for p, v in zip(pos, samples):
    ax.text(p + width / 2, max(v) + 6, f"{min(v):.0f}–{max(v):.0f}", ha="center", fontsize=10)
ax.set_xticks(pos, [labels[n] for n in names]); ax.set_ylabel("首次盾面失效（秒）")
ax.set_title("间歇交火：范围包含三种窗口相位与两种开火相位")
ax.legend(fontsize=10); ax.set_ylim(0, 285)

ax = axes[1, 1]
for exposure, color, label in [("Continuous", "#3275bd", "连续交火"), ("45on15off_p0", "#df7932", "45秒交火／15秒脱离")]:
    trace = np.array(one(Candidate="B30k", Loadout="Mixed", Cadence="Synchronized", Exposure=exposure)["Trace"])
    ax.plot(trace[:, 0], trace[:, 1] / 1000, color=color, label=f"{label}：常规池")
    ax.plot(trace[:, 0], trace[:, 2] / 1000, "--", color=color, label=f"{label}：前盾应急")
    ax.axvline(trace[-1, 0], color=color, alpha=.4, linewidth=1)
for start in [45, 105]:
    ax.axvspan(start, start + 15, color="#777777", alpha=.08)
ax.set_title("混合火力：2重炮＋1速射＋1档激光")
ax.set(xlabel="从首发命中起（秒）", ylabel="剩余容量（千点）", ylim=(0, 64))
ax.legend(fontsize=9, loc="upper right"); ax.grid(alpha=.15)

out = ROOT / "scripts/WeaponTuningRegression/results/history/护盾时长方案-2026-11-05.png"
out.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(out, dpi=150); plt.close(fig)
print(out)
