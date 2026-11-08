# Logo, images et icônes

[Documentation technique](README.md)

Le logo original Imagine Neo représente un stickman noir et blanc qui pense au mot **neo**.
Les fichiers de l’application sont dans [ui/Assets/](../ui/Assets/imagine-neo-logo.svg).

| Fichier | Usage |
| --- | --- |
| `imagine-neo-logo.svg` | Source vectorielle modifiable |
| `imagine-neo-logo.png` | Logo 512 × 512 de l’application |
| `imagine-neo-logo-1024.png` | Variante de grande taille |
| `imagine-neo.ico` | Icône Windows, tailles 16 à 256 pixels |

Régénération Windows depuis la racine :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/create_brand_assets.ps1
```

Le script utilise `rsvg-convert` de l’environnement Conda local pour rendre le SVG.
Cet outil concerne la **régénération des assets**, pas l’installation normale ni la compilation,
qui utilisent les images déjà versionnées.

Les assets de la démo publique sont dans `docs/site/assets/`.
`scripts/build_demo.py` génère les images synthétiques et l’aperçu de partage.
La capture de l’atelier provient d’un test réel WinUI.
