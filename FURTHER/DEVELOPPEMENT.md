# Développer et vérifier Imagine Neo

[Documentation technique](README.md) · [Créer un plugin](../README.md#créer-un-plugin-de-bout-en-bout)

## Choisir le bon point d’extension

| Objectif | Emplacement |
| --- | --- |
| Ajouter un traitement d’image | Nouveau dossier dans `plugins/`, classe ImagePlugin, YAML enfant et entrée dans le catalogue principal |
| Ajouter un paramètre à un plugin | Constructeur et YAML du plugin |
| Ajouter un type de paramètre réutilisable | Catalogue/validation Python et contrôles communs WinUI |
| Modifier sélection, dessin ou affichage | Contrôles/services C# correspondants |
| Compléter un outil historique existant | Sa fonction dans `backend/app/processing/`, en conservant le contrat |
| Ajouter une forme de résultat autre qu’image/masque | Abstractions communes, protocole et validation, puis UI commune |

Respectez [AGENTS.md](../AGENTS.md).
N’ajoutez pas de branche, bouton ou formulaire WinUI propre à l’identifiant d’un plugin.
Le [tutoriel](../backend/README.md) indique les fichiers du trajet YAML → UI → Python.

## Dépendances et conventions

Python 3.12 est géré par Conda, environnement `photolab`.
Pour ajouter une dépendance, mettez à jour [environment.yml](../backend/environment.yml)
et, pour une dépendance applicative, [pyproject.toml](../backend/pyproject.toml).
Exécutez ensuite `scripts\setup_conda.cmd`.

Préservez les originaux, les pixels hors sélection, l’historique et les contraintes d’alpha du traitement.
Un résultat valide est `uint8 BGRA` ; utilisez les helpers publics de `app.plugins`.
Pour les tableaux que votre code modifie, une vue contiguë ne remplace pas toujours une copie indépendante.

## Lancement et recompilation

Depuis la racine dans Miniforge Prompt :

```cmd
scripts\run_all.cmd
scripts\run_all.cmd Release
scripts\run_all.cmd Debug --no-build
scripts\run_all.cmd Debug --check
```

Pour une modification Python/YAML, **Reconnect** ou un redémarrage charge les nouveaux fichiers.
Pour C#/XAML, fermez l’interface et reconstruisez avec `scripts\build_ui.cmd`.

Deux terminaux sont possibles : `scripts\run_backend.cmd` puis `scripts\run_ui.cmd`.
Le backend manuel reste sous la responsabilité de son terminal.

## Vérifications adaptées

| Commande depuis la racine | Portée |
| --- | --- |
| `scripts\test_backend.cmd` | Suite complète Python, avec échecs connus documentés |
| `scripts\test_backend.cmd tests/test_plugins.py -q` | Catalogue, paramètres, copies, masques et lots |
| `scripts\build_ui.cmd` | Compilation du front |
| `scripts\test_interop.cmd` | Transport C#/Python réel |
| `scripts\test_ui.cmd Debug run_all` | Parcours de l’interface avec backend de production |
| `scripts\test_ui.cmd Debug run_all fixtures` | Parcours UI avec retours de test |
| `python scripts\verify_docs.py` dans photolab | Exemple documenté chargé et exécuté par le vrai moteur |
| `python scripts\verify_publication.py` dans photolab | Liens, metadata et démo précalculée |

Les tests de gestes natifs utilisent temporairement la souris :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_native_drawing.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_native_transforms.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test_native_drop.ps1
```

Les rapports et captures sont écrits sous `artifacts/`, exclu du dépôt.
Décrivez précisément dans votre contribution ce que vous avez testé :
les fixtures UI ne valident pas les algorithmes manquants.
La référence actuelle est [STATUS.md](../docs/STATUS.md).

## Site et documentation

Le site est dans `docs/site/` ; son comparateur affiche des résultats précalculés.
La génération utilise le code du projet :

```cmd
conda activate photolab
python scripts\build_demo.py
python scripts\verify_publication.py
python scripts\verify_docs.py
```

Le workflow de publication vérifie la démo et les exemples.
Le workflow Pages vérifie les fichiers statiques puis déploie le site.
Les fichiers de vérification Google versionnés doivent rester disponibles.
