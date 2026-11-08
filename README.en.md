# Imagine Neo

**A Windows image editor built around Python and OpenCV plugins.**

A native **C# / WinUI 3** workspace and **Win2D** canvas connect to a local **Python / NumPy / OpenCV** engine.
Import an image, select a region, apply a treatment and compare versions.
Add a Python class and YAML configuration to create a plugin with automatically generated controls.

[Interactive demo](https://professor-kaiser.github.io/imagine-neo/) · [Français](README.md) · [Plugin reference](docs/PLUGINS.md) · [Backend guide](backend/README.md) · [Technical docs](FURTHER/README.md)

[![Documentation and demo checks](https://github.com/professor-kaiser/imagine-neo/actions/workflows/publication.yml/badge.svg)](https://github.com/professor-kaiser/imagine-neo/actions/workflows/publication.yml)

![Imagine Neo workspace](docs/site/assets/workspace.png)

## Why use Imagine Neo?

Experiment with OpenCV algorithms in a visual workspace, write Python image filters without a custom WinUI form,
choose regions and masks, and compare every output with its source.
The desktop engine processes pixels locally. Imported files remain intact, and versions are kept for the session.

This is a **development preview**, installed from source.
See [STATUS.md](docs/STATUS.md) for the exact limits and known test failures.

## Features and demo

Import multiple images, zoom/pan, edit rectangular selections, rotate and resize,
draw and apply shapes, undo/redo, and export PNG or JPEG.
The project has grayscale, Gaussian / Average / Median blur, borders, Canny contours,
denoising, Laplacian and morphology implementations.
Masks support channels, HSV thresholds and painting.

Segmentation has known validation failures.
Bilateral blur, channel separation, color picking, palette extraction and the generic selection filter are incomplete.
History and masks last for the current session; full project saving and HDR / 16-bit editing are not available.

The [before/after demo](https://professor-kaiser.github.io/imagine-neo/#demo) shows actual project processing
on a deterministic synthetic scene. Results are precomputed; the full editor runs on Windows.
Download [the sample PNG](docs/site/assets/original.png), import it, choose denoising in the plugin catalogue and click **Apply**.

## Install on a new Windows PC

### 1. Install all prerequisites

Use **Windows 11 x64**. Install these tools before running the clone/setup commands:

| Tool | Purpose | Manual installer |
| --- | --- | --- |
| Git | Clone and update the project | [Git for Windows](https://gitforwindows.org/) |
| **.NET 8 SDK x64** | Build the C# interface; includes the .NET runtime | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Miniforge3 | Supply Conda and install the project's Python environment | [Miniforge Windows x86_64](https://github.com/conda-forge/miniforge#install) |

With Winget, run in PowerShell:

```powershell
winget install --id Git.Git --exact
winget install --id Microsoft.DotNet.SDK.8 --exact --architecture x64
winget install --id CondaForge.Miniforge3 --exact --architecture x64
```

Use the manual links if Winget is unavailable.
Install the **SDK**, rather than only the runtime.
Close existing terminals after installation and open **Miniforge Prompt** from the Start menu.
This is a CMD prompt with Conda available.

```cmd
git --version
dotnet --list-sdks
conda --version
```

The SDK list must include **8.0.x**. An Internet connection is needed for the initial Conda and NuGet downloads.

### 2. Clone and install project dependencies

In Miniforge Prompt:

```cmd
git clone https://github.com/professor-kaiser/imagine-neo.git
cd imagine-neo
scripts\setup_conda.cmd
```

The script creates or updates **photolab** from [backend/environment.yml](backend/environment.yml):
Python 3.12, NumPy 2.2, OpenCV 4.12, pywin32 311, PyYAML 6.0, pytest 8.4 and jsonschema 4.25.
You do not need a separate Python installation, a venv or an additional pip install for this workflow.

### 3. Build and launch

```cmd
scripts\run_all.cmd
```

The launcher builds C#, restores NuGet packages, validates the plugin catalogue and starts a fresh Python session.
Visual Studio is not required for the current C# configuration.
The build tools used from the Windows SDK are restored through NuGet;
the Windows App SDK runtime is deployed alongside the executable.

The [current release](https://github.com/professor-kaiser/imagine-neo/releases) contains source code and a sample image,
not a standalone installer. Keep the entire output folder when copying a build;
the .NET 8 runtime and Python environment are still required.

```cmd
scripts\run_all.cmd Release
scripts\run_all.cmd Debug --no-build
scripts\run_all.cmd Debug --check
```

`--no-build` uses an existing build; `--check` builds and checks the catalogue without opening the editor.
Keep the existing technical identifiers `photolab`, `PhotoLab.UI` and `PHOTOLAB_*`.

## Create a plugin from Python to the UI

The example adds a tint treatment with a color palette and an intensity field.
Copy the [example files](docs/examples/tint/plugin.py) into:

```text
plugins/
  plugins.yaml
  tint/
    plugin.py
    plugins.yaml
```

### Python class

In `plugins/tint/plugin.py`:

```python
"""Example tint plugin: mix a color while preserving source alpha."""
import numpy as np
from app.plugins import (
    ImagePlugin, validate_bgra, validate_image_selection, apply_masked_result,
)


class TintPlugin(ImagePlugin):
    def __init__(self, color: list[int], amount: float):
        self.color = validate_bgra(color)
        if not 0.0 <= amount <= 1.0:
            raise ValueError("amount must be between 0 and 1")
        self.amount = amount

    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        result = image.copy()
        weight = self.amount * self.color[3] / 255.0
        mixed = (
            image[:, :, :3].astype(np.float32) * (1.0 - weight)
            + np.array(self.color[:3], dtype=np.float32) * weight
        )
        result[:, :, :3] = np.clip(np.rint(mixed), 0, 255).astype(np.uint8)
        return apply_masked_result(image, result, selection, masks)
```

The color's alpha controls the mixing weight; the source image alpha remains unchanged.
`apply_masked_result` limits changes to the region and selected masks.

### Child YAML

In `plugins/tint/plugins.yaml`:

```yaml
version: 1
plugins:
  - id: tint
    name: Teinte personnalisée
    description: Mélange une couleur avec l'image, en conservant son alpha.
    file: plugin.py
    class: TintPlugin
    output_kind: image
    parameters:
      color:
        type: color_bgra
        label: Couleur de teinte
        default: [0, 0, 255, 128]
      amount:
        type: number
        label: Intensité
        description: 0 conserve l'image ; 1 utilise toute la force de la couleur.
        default: 0.4
        min: 0
        max: 1
```

Labels in this sample follow the French tutorial; you may change the displayed name and labels.
`file` is relative to the child YAML folder; `class` names the ImagePlugin subclass.
Parameter keys match the constructor exactly.

### Register and reload

Append `- tint` to the **existing** folder list in [plugins/plugins.yaml](plugins/plugins.yaml).
From the repository root:

```cmd
conda activate photolab
cd backend
python -m app.check_plugins
cd ..
scripts\run_all.cmd Debug --no-build
```

Build the interface once before using `--no-build`.
If it is already running, **Reconnect** reloads Python/YAML and restores the session's documents.
Select the new plugin, choose a color and amount, then click **Apply**.
No plugin-specific C# button, form or dispatch branch is required.

## YAML controls and Python values

The **YAML type** selects the UI control; the **parameter key** selects the Python keyword argument.

| YAML | WinUI control | Python |
| --- | --- | --- |
| `text` | Text field | `str` |
| `text` + `choices` | Dropdown | One allowed `str` |
| `integer` | Integer number field | `int` |
| `number` | Decimal number field | `float` |
| `boolean` | Checkbox | `bool` |
| `color_bgra` | Color palette with alpha and B/G/R/A fields | `list[int]`, **[B,G,R,A]**, 0–255 |
| `color_hsv` / `hsv` | H/S wheel, V slider and numeric fields | `list[int]`, H 0–179, S/V 0–255 |
| `integer` + `palette_channel` | Colored scalar bar linked to a channel choice | `int`, 0–255 |

`color_rgba` is not a supported YAML type. Convert a BGRA value explicitly when an algorithm expects RGBA:

```python
b, g, r, a = self.color
rgba = (r, g, b, a)
```

The catalogue validates metadata and signatures, `list_plugins` supplies descriptors,
and the common Properties panel builds controls.
Apply sends `plugin_id` and `arguments`, the engine constructs the class and calls its `apply` method,
then valid results become new library entries.
The [backend README](backend/README.md) maps this workflow to the source files.

## Image contract

`apply(image, selection, masks)` receives the full `uint8 (H,W,4)` BGRA image,
an optional Rect in image coordinates, and OpenCV masks `uint8 (H,W)`.
BGRA mask transport envelopes are decoded before the plugin runs.

Return one BGRA image or a nonempty list of up to 64 images.
The host supplies independent copies, protects source documents and keeps outside-selection pixels
for same-sized results. A different-sized output is an independent image.
Masks are combined by the plugin; the public helper uses their union intersected with the selection.

Full metadata, validation, mask generators, conditional fields and batch results are covered in [PLUGINS.md](docs/PLUGINS.md).

## Update, troubleshoot and contribute

```cmd
git pull --ff-only
scripts\setup_conda.cmd
scripts\run_all.cmd
```

If Conda is not found, use Miniforge Prompt or set `PHOTOLAB_CONDA` to your actual `condabin\conda.bat` path.
If dotnet is missing, install the x64 .NET 8 SDK and reopen the terminal.
If a plugin is absent, check both YAML files and the constructor, run the catalogue preflight and Reconnect.
Close the application before rebuilding C# files.

Use [CONTRIBUTING.md](CONTRIBUTING.md), [the technical guides](FURTHER/README.md)
and [GitHub issues](https://github.com/professor-kaiser/imagine-neo/issues).
The publication workflow verifies demo processing, executable examples and documentation links.
Known full-suite failures remain documented in [STATUS.md](docs/STATUS.md).

Keywords: Windows image editor, Python plugins, OpenCV, WinUI 3, NumPy, computer vision, denoising, segmentation, image masks, C#.

Licensed under [MIT](LICENSE).
