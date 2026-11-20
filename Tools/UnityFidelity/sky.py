"""Unity Skybox/Cubemap conversion for CrossCubeSkybox.

Unity (linear colour space) outputs  decode(texel) * linear(_Tint) * unity_ColorSpaceDouble(4.5948) * linear(_Exposure);
_Exposure is declared [Gamma], so it is linearized like a colour. Without an HDR texture format, the cross is stored as
encode(decode(t)*m)/encode(m) per channel and the renderer multiplies by gain = encode(m), reproducing the radiance exactly.
"""
import re
from unity_assets import srgb_to_linear, linear_to_srgb, open_rgba
from PIL import Image

def multiplier(material_text):
    tint = [float(x) for x in re.findall(r"[rgb]: ([+\-\d.eE]+)", re.search(r"_Tint: (\{[^}]+})", material_text)[1])][:3]
    exposure = float(re.search(r"_Exposure: (\S+)", material_text)[1])
    return [srgb_to_linear(t) * 4.5947938 * srgb_to_linear(exposure) for t in tint]

def rotation(material_text): return float(re.search(r"_Rotation: (\S+)", material_text)[1])

def gain(m): return [linear_to_srgb(k) for k in m]

def encode_cross(image, m, size=(2048, 1536)):
    lut = []
    for k in m:
        top = linear_to_srgb(k)
        lut.extend(0 if top <= 0 else min(255, round(255 * linear_to_srgb(srgb_to_linear(i / 255) * k) / top)) for i in range(256))
    return image.convert("RGB").resize(size, Image.Resampling.LANCZOS).point(lut)
