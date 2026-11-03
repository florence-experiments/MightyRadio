#!/usr/bin/env python3
"""
Static checker for Mighty Radio's WPF resources.

WPF only resolves {StaticResource}/{DynamicResource} references inside templates when the template is
instantiated, so a wrong-typed or missing resource compiles fine and fails at runtime (v1.1.0 shipped a
sys:Double used as RowDefinition.Height, which needs a GridLength). This script catches that class of bug
before a release:

  * every referenced key is defined (Controls.xaml, App.xaml, the palettes, or the file's own resources)
  * the resource's type suits the property it is assigned to (Double vs GridLength, Thickness,
    CornerRadius, Brush vs Color, FontFamily, Duration, Effect, Style, templates, converters)
  * all palettes define the same keys with the same types, and every theme in ThemeManager has a palette
  * palette keys are only used through DynamicResource (StaticResource would not follow a theme swap)
  * StaticResource references in a dictionary point at keys defined earlier (or in an earlier dictionary)
  * no DynamicResource inside a Freezable that lives in a resource dictionary (it gets frozen)
  * Style TargetType matches its ControlTemplate TargetType; trigger TargetName exists in the template
  * string keys used from C# (FindResource, TryFindResource, SetResourceReference, Resources["..."]) exist

Usage:  python3 tools/check-xaml-resources.py [project dir]      (exit code 1 on any error)
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

X = "{http://schemas.microsoft.com/winfx/2006/xaml}"
ROOT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "MightyRadio"
THEMES = ROOT / "Themes"
CONTROLS = THEMES / "Controls.xaml"

errors: list[str] = []
def err(msg): errors.append(msg)

def local(tag: str) -> str:
    return tag.split("}")[-1]

# ---------------------------------------------------------------- resource value types
TYPE_OF_TAG = {
    "SolidColorBrush": "Brush", "LinearGradientBrush": "Brush", "RadialGradientBrush": "Brush",
    "ImageBrush": "Brush", "DrawingBrush": "Brush", "VisualBrush": "Brush",
    "Double": "Double", "GridLength": "GridLength", "Thickness": "Thickness", "CornerRadius": "CornerRadius",
    "FontFamily": "FontFamily", "Color": "Color", "Duration": "Duration", "Style": "Style",
    "ControlTemplate": "ControlTemplate", "DataTemplate": "DataTemplate",
    "DropShadowEffect": "Effect", "BlurEffect": "Effect", "Boolean": "Boolean", "String": "String",
    "Int32": "Int32", "FontWeight": "FontWeight",
}
def type_of_resource(el) -> str:
    t = local(el.tag)
    if t in TYPE_OF_TAG:
        return TYPE_OF_TAG[t]
    if t.endswith("Converter"):
        return "Converter"
    return t

FREEZABLE_TAGS = {"SolidColorBrush", "LinearGradientBrush", "RadialGradientBrush", "ImageBrush", "DrawingBrush",
                  "VisualBrush", "GradientStop", "DropShadowEffect", "BlurEffect", "Pen", "GeometryDrawing"}

# Property name -> expected resource type (owner-specific overrides first).
OWNER_PROPS = {
    ("RowDefinition", "Height"): "GridLength", ("ColumnDefinition", "Width"): "GridLength",
    ("RowDefinition", "MinHeight"): "Double", ("RowDefinition", "MaxHeight"): "Double",
    ("ColumnDefinition", "MinWidth"): "Double", ("ColumnDefinition", "MaxWidth"): "Double",
    ("GradientStop", "Color"): "Color", ("SolidColorBrush", "Color"): "Color", ("DropShadowEffect", "Color"): "Color",
}
PROPS = {
    **{p: "Brush" for p in ("Background", "Foreground", "BorderBrush", "Fill", "Stroke", "CaretBrush",
                             "SelectionBrush", "OpacityMask")},
    **{p: "Double" for p in ("Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "FontSize",
                             "Opacity", "StrokeThickness", "LineHeight", "RadiusX", "RadiusY", "BlurRadius",
                             "ShadowDepth", "MaxDropDownHeight")},
    **{p: "Thickness" for p in ("Margin", "Padding", "BorderThickness")},
    "CornerRadius": "CornerRadius", "FontFamily": "FontFamily", "Effect": "Effect", "Color": "Color",
    "Duration": "Duration", "FontWeight": "FontWeight",
    **{p: "Style" for p in ("Style", "FocusVisualStyle", "BasedOn", "ItemContainerStyle")},
    "Template": "ControlTemplate",
    **{p: "DataTemplate" for p in ("ItemTemplate", "ContentTemplate")},
    "Converter": "Converter",
}
def expected_type(owner: str | None, prop: str) -> str | None:
    prop = prop.split(".")[-1]
    if owner and (owner, prop) in OWNER_PROPS:
        return OWNER_PROPS[(owner, prop)]
    return PROPS.get(prop)

REF = re.compile(r"\{(StaticResource|DynamicResource)\s+([A-Za-z_][\w.]*)\s*\}")

# ---------------------------------------------------------------- load files
def parse(path: Path):
    try:
        return ET.parse(path).getroot()
    except ET.ParseError as e:
        err(f"{path.name}: XML parse error {e}")
        return None

def collect_defs(root, path: Path):
    """Ordered (key, type, element) defined directly in a ResourceDictionary root or any *.Resources block."""
    out = []
    def visit(el, in_resources):
        for child in el:
            if in_resources and (k := child.get(X + "Key")) and not k.startswith("{"):
                out.append((k, type_of_resource(child), child))
            visit(child, local(child.tag).endswith(".Resources") or local(child.tag) == "ResourceDictionary")
    visit(root, local(root.tag) == "ResourceDictionary")
    return out

palette_files = sorted(p for p in THEMES.glob("*.xaml") if p.name != "Controls.xaml")
palettes = {}
for p in palette_files:
    r = parse(p)
    if r is not None:
        palettes[p.stem] = {k: t for k, t, _ in collect_defs(r, p)}

# Palette parity
all_palette_keys = set().union(*[set(d) for d in palettes.values()]) if palettes else set()
for name, d in palettes.items():
    for k in sorted(all_palette_keys - set(d)):
        err(f"Themes/{name}.xaml: missing palette key '{k}' (defined in another theme)")
ref_name = "Dark" if "Dark" in palettes else next(iter(palettes), None)
if ref_name:
    for name, d in palettes.items():
        for k, t in d.items():
            rt = palettes[ref_name].get(k)
            if rt and rt != t:
                err(f"Themes/{name}.xaml: '{k}' is a {t} but in {ref_name}.xaml it is a {rt}")

# Every theme id in ThemeManager has a palette file
tm = ROOT / "Services" / "ThemeManager.cs"
if tm.exists():
    for tid in re.findall(r'new\("(\w+)",\s*"', tm.read_text(encoding="utf-8")):
        if tid not in palettes:
            err(f"ThemeManager lists theme '{tid}' but Themes/{tid}.xaml is missing")

palette_types = {k: (palettes[ref_name] if ref_name else {}).get(k) for k in all_palette_keys}
for d in palettes.values():
    for k, t in d.items():
        palette_types.setdefault(k, t)

controls_root = parse(CONTROLS)
controls_defs = collect_defs(controls_root, CONTROLS) if controls_root is not None else []
app_root = parse(ROOT / "App.xaml")
app_defs = collect_defs(app_root, ROOT / "App.xaml") if app_root is not None else []

global_types = dict(palette_types)
for k, t, _ in controls_defs + app_defs:
    if k in global_types and k in all_palette_keys:
        err(f"Controls.xaml/App.xaml redefines palette key '{k}'")
    global_types[k] = t

# ---------------------------------------------------------------- check usages
def check_file(path: Path, root, ordered_dictionary: bool):
    local_defs = {k: t for k, t, _ in collect_defs(root, path)} if not ordered_dictionary else {}
    defined_so_far = set(palette_types)  # palettes are merged before Controls.xaml
    def_positions = {}
    if ordered_dictionary:
        for i, (k, _, el) in enumerate(collect_defs(root, path)):
            def_positions[id(el)] = k

    parent = {c: p for p in root.iter() for c in p}

    def owner_style_target(el):
        """For a Setter, the TargetType of the enclosing Style (if any)."""
        p = parent.get(el)
        while p is not None:
            if local(p.tag) == "Style":
                return (p.get("TargetType") or "").replace("{x:Type ", "").rstrip("}").split(":")[-1] or None
            if local(p.tag) == "ControlTemplate":
                return None
            p = parent.get(p)
        return None

    def inside_frozen_resource(el):
        p, chain = el, []
        while p is not None:
            chain.append(p)
            pp = parent.get(p)
            if pp is not None and (local(pp.tag).endswith(".Resources") or local(pp.tag) == "ResourceDictionary") \
                    and p.get(X + "Key"):
                return any(local(c.tag) in FREEZABLE_TAGS for c in chain)
            if local(p.tag) in ("ControlTemplate", "DataTemplate", "Style"):
                return False
            p = pp
        return False

    def lookup(key):
        if key in local_defs:
            return local_defs[key]
        return global_types.get(key)

    for el in root.iter():
        if ordered_dictionary and id(el) in def_positions:
            defined_so_far.add(def_positions[id(el)])
        tag = local(el.tag)
        for attr, value in el.attrib.items():
            for m in REF.finditer(value):
                kind, key = m.groups()
                where = f"{path.relative_to(ROOT)}: <{tag} {local(attr)}=\"{value}\">"
                rtype = lookup(key)
                if rtype is None:
                    err(f"{where}: resource '{key}' is not defined anywhere")
                    continue
                if kind == "StaticResource" and key in all_palette_keys:
                    err(f"{where}: palette key '{key}' must use DynamicResource so theme swaps apply")
                if kind == "StaticResource" and ordered_dictionary and key not in defined_so_far:
                    err(f"{where}: StaticResource '{key}' is used before it is defined in this dictionary")
                if kind == "DynamicResource" and inside_frozen_resource(el):
                    err(f"{where}: DynamicResource inside a Freezable resource (it is frozen; use a fixed value)")
                # Expected type
                if tag == "Setter":
                    prop = el.get("Property") or ""
                    exp = expected_type(owner_style_target(el), prop) if local(attr) == "Value" else None
                else:
                    exp = expected_type(tag, local(attr))
                if exp and rtype != exp:
                    err(f"{where}: '{key}' is a {rtype} but this property needs a {exp}")

    # Style TargetType vs ControlTemplate TargetType, and TargetName inside templates
    def tname(s): return (s or "").replace("{x:Type ", "").rstrip("}").split(":")[-1]
    for style in root.iter():
        if local(style.tag) != "Style":
            continue
        st = tname(style.get("TargetType"))
        for setter in style.iter():
            if local(setter.tag) == "Setter" and setter.get("Property") == "Template" and st:
                direct = [c for sv in setter if local(sv.tag) == "Setter.Value" for c in sv]
                for ct in direct:
                    if local(ct.tag) == "ControlTemplate" and ct.get("TargetType") and tname(ct.get("TargetType")) != st:
                        err(f"{path.name}: Style TargetType {st} has a ControlTemplate for {tname(ct.get('TargetType'))}")
                v = setter.get("Value") or ""
                if (m := REF.search(v)):
                    for k, t, tel in controls_defs:
                        if k == m.group(2) and tel.get("TargetType") and tname(tel.get("TargetType")) != st:
                            err(f"{path.name}: Style for {st} uses template '{k}' made for {tname(tel.get('TargetType'))}")
    for ct in root.iter():
        if local(ct.tag) != "ControlTemplate":
            continue
        names = {e.get(X + "Name") for e in ct.iter() if e.get(X + "Name")}
        for s in ct.iter():
            tn = s.get("TargetName")
            if tn and tn not in names:
                err(f"{path.name}: trigger setter TargetName '{tn}' is not inside its ControlTemplate")

if controls_root is not None:
    check_file(CONTROLS, controls_root, ordered_dictionary=True)
for p in palette_files:
    r = parse(p)
    if r is not None:
        check_file(p, r, ordered_dictionary=True)
for p in sorted(ROOT.rglob("*.xaml")):
    if p.parent == THEMES or "obj" in p.parts or "bin" in p.parts:
        continue
    r = parse(p)
    if r is not None:
        check_file(p, r, ordered_dictionary=False)

# ---------------------------------------------------------------- C# string keys
CS_KEY = re.compile(r'(?:FindResource|TryFindResource)\(\s*"([^"]+)"|SetResourceReference\([^,]+,\s*([^)]+)\)|Resources\[\s*"([^"]+)"\s*\]')
for cs in sorted(ROOT.rglob("*.cs")):
    if "obj" in cs.parts or "bin" in cs.parts:
        continue
    for m in CS_KEY.finditer(cs.read_text(encoding="utf-8")):
        keys = [m.group(1) or m.group(3)] if (m.group(1) or m.group(3)) else re.findall(r'"([^"]+)"', m.group(2) or "")
        for k in keys:
            if k not in global_types:
                err(f"{cs.relative_to(ROOT)}: resource key '{k}' used from code is not defined")

checked = len(palettes)
if errors:
    print(f"XAML resource check: {len(errors)} problem(s)")
    for e in errors:
        print("  - " + e)
    sys.exit(1)
print(f"XAML resource check: OK ({len(all_palette_keys)} palette keys x {checked} themes, "
      f"{len(controls_defs)} shared resources)")
