# Quantum Harness icon

Approved direction B: a black geometric double-bracket mark on a warm white rounded tile. Approved by the user on 2026-10-14; integrated on 2026-10-15.

- `QuantumHarness.png`: RGBA master with transparent outer corners, used by the sidebar.
- `QuantumHarness.ico`: 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixel frames, embedded in the EXE and used by the main window. Other windows inherit the executable icon.

The master was prepared with built-in ImageGen from the approved B reference. Final prompt:

> Use case: precise-object-edit / logo-brand production export. Edit the attached approved Quantum Harness icon B into a clean final desktop icon asset. Keep EXACTLY the existing black two-bracket geometric emblem, silhouette, rotation, spacing, shapes and proportions; do not reinterpret or redesign it. Keep the warm off-white rounded-square tile and its corner radius. Remove only the external light-gray presentation surround, make the area outside the rounded tile genuinely transparent. Center the tile in a square image with very small equal transparent safe margins. Flatten the existing subtle lighting and textures into solid warm off-white tile (#FFFCF6) and solid near-black symbol (#171717), clean precise antialiased vector-like edges. No shadow, no gloss, no depth, no gradients, no backdrop, no text, no new details. This must look like the SAME approved B icon, with original two bracket mark unchanged.

To regenerate the ICO from the master with Pillow, run from the repository root:

```powershell
python -c "from PIL import Image; from pathlib import Path; p = Path('src/ClawBox.Desktop/Assets'); Image.open(p / 'QuantumHarness.png').convert('RGBA').save(p / 'QuantumHarness.ico', format='ICO', sizes=[(n,n) for n in (16,20,24,32,40,48,64,96,128,256)])"
```

The images are WPF resources rather than loose files in the portable package.
