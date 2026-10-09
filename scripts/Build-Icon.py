"""Regenerate native icon assets from the tracked SVG (Pillow + CairoSVG)."""
from pathlib import Path
from io import BytesIO
import cairosvg
from PIL import Image

assets = Path(__file__).resolve().parents[1] / "src/FurtherAgent.Client/Assets"
source = assets / "FurtherAgent.svg"
png = cairosvg.svg2png(url=str(source), output_width=512, output_height=512)
(assets / "FurtherAgent.png").write_bytes(png)
image = Image.open(BytesIO(png)).convert("RGBA")
image.save(assets / "FurtherAgent.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
print("Generated PNG and multi-resolution ICO from FurtherAgent.svg")
