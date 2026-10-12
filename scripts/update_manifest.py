"""Maintains the Jellyfin plugin repository manifest (manifest.json).

Plugin metadata, target ABI and changelog are read from build.yaml so there is a single source of truth.
"""
import argparse
import json
import re
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "manifest.json"
BUILD_YAML = ROOT / "build.yaml"


def read_build_yaml():
    """Parses the flat key: value pairs (and '>' folded blocks) used in build.yaml."""
    values = {}
    lines = BUILD_YAML.read_text(encoding="utf-8").splitlines()
    i = 0
    while i < len(lines):
        m = re.match(r'^(\w+):\s*(.*)$', lines[i])
        i += 1
        if not m:
            continue
        key, value = m.group(1), m.group(2).strip()
        if value in (">", "|"):
            block = []
            while i < len(lines) and (lines[i].startswith(" ") or not lines[i].strip()):
                block.append(lines[i].strip())
                i += 1
            value = " ".join(b for b in block if b)
        values[key] = value.strip('"')
    return values


def add(args):
    meta = read_build_yaml()
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8")) if MANIFEST.exists() else []
    plugin = next((p for p in manifest if p["guid"] == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        manifest.append(plugin)

    plugin.update({
        "name": meta["name"],
        "overview": meta["overview"],
        "description": meta["description"],
        "owner": meta["owner"],
        "category": meta["category"],
        "imageUrl": meta.get("imageUrl", ""),
    })
    plugin["versions"] = [v for v in plugin["versions"] if v["version"] != args.version]
    plugin["versions"].insert(0, {
        "version": args.version,
        "changelog": meta.get("changelog", ""),
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.url,
        "checksum": args.checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    plugin["versions"].sort(key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    p_add = sub.add_parser("add", help="add or replace a version")
    p_add.add_argument("--version", required=True)
    p_add.add_argument("--checksum", required=True)
    p_add.add_argument("--url", required=True)
    sub.add_parser("changelog", help="print the changelog from build.yaml")
    args = parser.parse_args()
    if args.command == "add":
        add(args)
    else:
        print(read_build_yaml().get("changelog", ""))


if __name__ == "__main__":
    main()
