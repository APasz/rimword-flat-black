"""Render the shared checkbox and radio artwork into native game textures."""

from __future__ import annotations

from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


SVG_NAMESPACE = "http://www.w3.org/2000/svg"
ET.register_namespace("", SVG_NAMESPACE)


def render_controls(source: Path, destination: Path) -> None:
    """Render each SVG symbol at its declared pixel size using librsvg."""
    artwork = ET.parse(source).getroot()
    definitions = artwork.find(f"{{{SVG_NAMESPACE}}}defs")
    if definitions is None:
        raise ValueError(f"Missing SVG definitions: {source}")
    symbols = definitions.findall(f"{{{SVG_NAMESPACE}}}symbol")
    if not symbols:
        raise ValueError(f"Missing control symbols: {source}")
    destination.mkdir(parents=True, exist_ok=True)
    for symbol in symbols:
        name = symbol.attrib["id"]
        if not name.isidentifier():
            raise ValueError(f"Invalid control name: {name}")
        x, y, width, height = map(int, symbol.attrib["viewBox"].split())
        if x != 0 or y != 0 or width <= 0 or height <= 0:
            raise ValueError(f"Invalid control canvas: {name}")
        canvas = ET.Element(f"{{{SVG_NAMESPACE}}}svg", {
            "width": str(width), "height": str(height), "viewBox": symbol.attrib["viewBox"],
        })
        canvas.append(definitions)
        ET.SubElement(canvas, f"{{{SVG_NAMESPACE}}}use", {
            "href": f"#{name}", "width": str(width), "height": str(height),
        })
        subprocess.run(
            ["rsvg-convert", "--output", str(destination / f"{name}.png")],
            input=ET.tostring(canvas, encoding="utf-8"), check=True,
        )


if __name__ == "__main__":
    repository = Path(__file__).resolve().parents[1]
    render_controls(repository / "Art/Controls.svg", repository / "1.7/Textures/FlatBlack/UI/Widgets")
