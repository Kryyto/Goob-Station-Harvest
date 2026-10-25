#!/usr/bin/env python3
"""Regenerates the android cook and bartender menus from the game prototypes.

Usage: python Tools/android_npc_gen_menus.py
Writes Resources/Prototypes/_Harvest/AndroidNpc/menus.yml:
- bartender: every pre-filled metamorphic glass of a standard drink, named after its reagent (en-US and fr-FR locale);
- cook: every standard dish a microwave recipe makes. French names come from the fr-FR `ent-<id>` keys, else the
  prototype names; English names from the en-US keys, else the prototypes before the French translation commit
  (TRANSLATION_PARENT), read with `git show`.
Edit the exclusion lists below, then regenerate.
"""
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROTOS = ROOT / "Resources" / "Prototypes"
LOCALE = ROOT / "Resources" / "Locale"
DST = PROTOS / "_Harvest" / "AndroidNpc" / "menus.yml"

# Last commit with the original English prototype names.
TRANSLATION_PARENT = "25d6b81e23^"

DRINK_GLASSES = PROTOS / "Entities" / "Objects" / "Consumable" / "Drinks" / "drinks_metamorphic.yml"
DRINK_REAGENTS = PROTOS / "Reagents" / "Consumable" / "Drink"
FOOD_DIR = "Resources/Prototypes/Entities/Objects/Consumable/Food"
RECIPES = PROTOS / "Recipes" / "Cooking" / "meal_recipes.yml"

# Not something a bartender serves on request.
DRINK_EXCLUDED = {
    "AcidSpit", "Blood", "Booger", "BudgetInsulsDrink", "Ice", "JuiceBerryPoison", "Neurotoxin", "Nothing",
    "PoisonWine", "ToxinsSpecial",
}
# Plain pours are quick; everything else is mixed.
QUICK_DRINK_FILES = {"juice.yml", "soda.yml", "drinks.yml"}

# Joke, poisonous, moth-only or gross dishes, and ingredients rather than dishes.
FOOD_EXCLUDED = re.compile(
    r"Cotton|Cannabis|Dank|Appendix|Brain|Human|BurgerCat|Corgi|Ghost|Mcguffin|Spell|BurgerRat|TacoRat|Robot|Xeno|"
    r"Amanita|Uranium|Moldy|Anomaly|Eyeball|Monkey|BurgerBear|MothRoach|Suppermatter|Slime|Copy|Crazy|Empowered|"
    r"BurgerFive|Baseball|TacoShell|Swept|Spider|Dragon|Blood|Clown|Mime|Mimana|Lemoon|Bungo|Batch|Valid|Jelly")

LANGS = ("en", "fr")


def q(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def blocks(text: str, kind: str):
    """Yields the field lines of each top-level prototype of the given kind."""
    for block in re.split(r"^- ", text, flags=re.M)[1:]:
        if re.match(rf"type: {kind}\s*$", block.splitlines()[0]):
            yield block


def field(block: str, name: str):
    m = re.search(rf"^  {name}: *(.+)$", block, flags=re.M)
    return re.sub(r"\s+#.*$", "", m.group(1)).strip().strip('"\'') if m else None


def parents(block: str):
    m = re.search(r"^  parent: *(.*)$", block, flags=re.M)
    if not m:
        return []
    value = m.group(1).strip()
    if value.startswith("["):
        return [p.strip() for p in value.strip("[]").split(",") if p.strip()]
    if value:
        return [value]
    return re.findall(r"^  - (\w+)", block[m.end():].split("\n  components:")[0], flags=re.M)


def ftl(lang: str):
    out = {}
    for path in (LOCALE / lang / "reagents" / "meta" / "consumable" / "drink").glob("*.ftl"):
        for line in path.read_text(encoding="utf-8").splitlines():
            if "=" in line and not line.startswith(("#", " ")):
                key, value = line.split("=", 1)
                out[key.strip()] = value.strip()
    return out


def entity_keys(lang: str):
    """id -> name from the `ent-<id>` keys of a locale (the game prefers them over the prototype name)."""
    out = {}
    for path in (LOCALE / lang).rglob("*.ftl"):
        if not path.is_file():
            continue
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if m := re.match(r"^ent-([\w-]+) *= *(\S.*)$", line):
                out[m.group(1)] = m.group(2).strip()
    return out


def entity_names(texts):
    """id -> (name, parents) for every entity in the given YAML texts."""
    out = {}
    for text in texts:
        for block in blocks(text, "entity"):
            if ident := field(block, "id"):
                out[ident] = (field(block, "name"), parents(block))
    return out


def resolve_name(ident, names, seen=()):
    if ident not in names or ident in seen:
        return None
    name, ps = names[ident]
    if name:
        return name
    for p in ps:
        if found := resolve_name(p, names, (*seen, ident)):
            return found
    return None


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, encoding="utf-8", check=True).stdout


def drinks():
    reagents = {}
    for path in DRINK_REAGENTS.glob("*.yml"):
        for block in blocks(path.read_text(encoding="utf-8"), "reagent"):
            if (ident := field(block, "id")) and (key := field(block, "name")):
                reagents[ident] = (key, path.name in QUICK_DRINK_FILES)

    locales = {"en": ftl("en-US"), "fr": ftl("fr-FR")}
    items = []
    for block in blocks(DRINK_GLASSES.read_text(encoding="utf-8"), "entity"):
        ident = field(block, "id")
        reagent = re.search(r"ReagentId: *(\w+)", block)
        if not ident or not reagent or reagent.group(1) in DRINK_EXCLUDED or reagent.group(1) not in reagents:
            continue
        key, quick = reagents[reagent.group(1)]
        names = {lang: locales[lang].get(key) for lang in LANGS}
        if None in names.values():
            continue
        items.append((ident, names, 3 if quick else 5))
    return sorted(items, key=lambda i: i[1]["fr"])


def food():
    results = sorted(set(re.findall(r"^  result: *(\w+)", RECIPES.read_text(encoding="utf-8"), flags=re.M)))
    # Upstream moves dishes between folders, so look through every prototype.
    current = entity_names(p.read_text(encoding="utf-8", errors="replace") for p in PROTOS.rglob("*.yml") if p.is_file())
    old_files = git("ls-tree", "-r", "--name-only", TRANSLATION_PARENT, "--", FOOD_DIR).split()
    old = entity_names(git("show", f"{TRANSLATION_PARENT}:{f}") for f in old_files if f.endswith(".yml"))
    fr_keys, en_keys = entity_keys("fr-FR"), entity_keys("en-US")

    items = []
    for ident in results:
        if not ident.startswith("Food") or FOOD_EXCLUDED.search(ident):
            continue
        fr = fr_keys.get(ident) or resolve_name(ident, current)
        if not fr:
            continue
        en = en_keys.get(ident) or resolve_name(ident, old) or resolve_name(ident, current) or fr
        items.append((ident, {"en": en, "fr": fr}, 8))
    return sorted(items, key=lambda i: i[1]["fr"])


def write_menu(out, ident, items):
    out.append(f"\n- type: androidMenu\n  id: {ident}\n  items:\n")
    for item, names, prep in items:
        joined = ", ".join(f"{lang}: {q(names[lang])}" for lang in LANGS)
        out.append(f"  - item: {item}\n    names: {{ {joined} }}\n    prepSeconds: {prep}\n")


def main():
    out = [
        "# SPDX-License-Identifier: AGPL-3.0-or-later\n"
        "# Generated by Tools/android_npc_gen_menus.py from the drink and recipe prototypes. Edit the script, then regenerate.\n"
        "# Hand-written entries with patterns live in jobs.yml and are matched first.\n"
    ]
    write_menu(out, "Bartender", drinks())
    write_menu(out, "Cook", food())
    DST.write_text("".join(out), encoding="utf-8", newline="\n")
    print(f"Wrote {DST}")


if __name__ == "__main__":
    main()
