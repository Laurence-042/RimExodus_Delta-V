#!/usr/bin/env python3
"""Stream a RimWorld save and report one map's building anchors.

Only the selected direct child of ``savegame/game/maps`` is retained.  The
rest of the save is cleared as ElementTree advances, so large pawn/world data
does not become part of the extracted layout.
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path


CELL_RE = re.compile(r"\((-?\d+),\s*(-?\d+),\s*(-?\d+)\)")
ROTATIONS = {"0": "North", "1": "East", "2": "South", "3": "West"}


def text(element: ET.Element, name: str, default: str = "") -> str:
    child = element.find(name)
    return child.text.strip() if child is not None and child.text else default


def parse_cell(value: str) -> tuple[int, int, int]:
    match = CELL_RE.fullmatch(value)
    if not match:
        raise ValueError(f"invalid RimWorld cell: {value!r}")
    return tuple(map(int, match.groups()))


def extract(save_path: Path, map_index: int) -> dict:
    stack: list[str] = []
    maps_depth: int | None = None
    selected: ET.Element | None = None
    selected_depth: int | None = None
    ordinal = -1

    for event, element in ET.iterparse(save_path, events=("start", "end")):
        if event == "start":
            stack.append(element.tag)
            if stack[-3:] == ["savegame", "game", "maps"]:
                maps_depth = len(stack)
            elif maps_depth is not None and len(stack) == maps_depth + 1 and element.tag == "li":
                ordinal += 1
                if ordinal == map_index:
                    selected = element
                    selected_depth = len(stack)
            continue

        depth = len(stack)
        if selected is not None and element is selected and depth == selected_depth:
            break

        # Clear completed top-level maps that are not the requested map.  Do
        # not clear descendants of the selected map before it is inspected.
        inside_selected = selected is not None and selected_depth is not None and depth >= selected_depth
        if not inside_selected and maps_depth is not None and depth >= maps_depth + 1:
            element.clear()
        stack.pop()
    else:
        raise IndexError(f"map index {map_index} was not found")

    assert selected is not None
    size = parse_cell(text(selected.find("mapInfo"), "size"))
    things = selected.find("things")
    buildings = []
    if things is not None:
        for thing in things.findall("thing"):
            class_name = thing.get("Class", "")
            if not class_name.startswith("Building"):
                continue
            x, y, z = parse_cell(text(thing, "pos"))
            buildings.append(
                {
                    "def": text(thing, "def"),
                    "class": class_name,
                    "anchor": [x, y, z],
                    "rotation": ROTATIONS.get(text(thing, "rot", "0"), text(thing, "rot", "0")),
                    "stuff": text(thing, "stuff") or None,
                }
            )

    counts = collections.Counter(item["def"] for item in buildings)
    return {
        "map_index": map_index,
        "unique_id": int(text(selected, "uniqueID")),
        "generator": text(selected, "generatorDef"),
        "size": list(size),
        "building_counts": dict(sorted(counts.items())),
        "buildings": buildings,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("save", type=Path)
    parser.add_argument("--map-index", type=int, default=2,
                        help="zero-based entry in savegame/game/maps (default: 2)")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    result = extract(args.save, args.map_index)
    rendered = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(rendered + "\n", encoding="utf-8")
    else:
        print(rendered)


if __name__ == "__main__":
    main()
