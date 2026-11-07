"""Read production RGB shader multipliers for offline art previews."""
from pathlib import Path
import re

def production_colors():
    code = (Path(__file__).resolve().parents[1] / 'Source/Rendering/StorageStateColors.cs').read_text(encoding='utf-8')
    colors = {}
    for name, arguments in re.findall(r'Color (\w+) = new Color\(([^)]+)\)', code):
        rgb = []
        for expression in arguments.split(','):
            factors = [float(v.strip().rstrip('f')) for v in expression.split('/')]
            rgb.append(round(255 * factors[0] / (factors[1] if len(factors) == 2 else 1)))
        assert len(rgb) == 3 and all(0 <= v <= 255 for v in rgb)
        colors[name] = tuple(rgb)
    assert set(colors) == {'Active','Reserved','Locked','HostileLocked'}
    return colors
