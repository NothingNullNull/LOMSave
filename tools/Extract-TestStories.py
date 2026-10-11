"""Extract only the two regression stories from the user's game installation."""
import argparse
from pathlib import Path

import UnityPy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--output-dir", type=Path,
                        default=Path(__file__).resolve().parents[1] / "assets" / "story")
    args = parser.parse_args()
    game_data = args.game_dir / "Mortal_Data"
    if not game_data.is_dir():
        parser.error(f"Game data directory not found: {game_data}")
    names = {"back_02_501", "ch2_4_5_3"}
    found = {}
    # The current game stores these scripts in resources.assets. Search the
    # remaining bundles too if a later game version moves them.
    bundles = [game_data / "resources.assets"]
    bundles += sorted(game_data.glob("sharedassets*.assets"))
    bundles += [game_data / "globalgamemanagers.assets"]
    for bundle in bundles:
        if not bundle.is_file():
            continue
        environment = UnityPy.load(str(bundle))
        for obj in environment.objects:
            if obj.type.name != "TextAsset":
                continue
            data = obj.read()
            if data.m_Name not in names:
                continue
            script = data.m_Script
            raw = (script.encode("utf-8", "surrogateescape")
                   if isinstance(script, str) else bytes(script))
            found[data.m_Name] = raw
        if names <= found.keys():
            break
    missing = names - found.keys()
    if missing:
        parser.error("Required story assets not found: " + ", ".join(sorted(missing)))
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for name in sorted(names):
        target = args.output_dir / (name + ".txt")
        target.write_bytes(found[name])
        print(f"Extracted {target}")


if __name__ == "__main__":
    main()
