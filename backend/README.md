# Backend Imagine Neo — développer des plugins Python

Le backend est le moteur local de l’éditeur Imagine Neo.
Il charge les plugins YAML, instancie leurs classes Python, traite des images NumPy avec OpenCV,
valide les résultats et les remet à l’interface WinUI.

[Installation de l’application](../README.md#installation-depuis-les-sources) · [Tutoriel complet](../README.md#créer-un-plugin-de-bout-en-bout) · [Contrat des plugins](../docs/PLUGINS.md) · [Internals et IPC](../FURTHER/ARCHITECTURE.md)

## Installer et lancer le backend

Pour l’application complète, commencez par les installations **Git + SDK .NET 8 x64 + Miniforge**
du [README principal](../README.md). Le backend Windows utilisé seul nécessite Git et Miniforge ;
le SDK .NET est nécessaire pour compiler le front.

Dans **Miniforge Prompt**, depuis un dossier de travail :

```cmd
git clone https://github.com/professor-kaiser/imagine-neo.git
cd imagine-neo
scripts\setup_conda.cmd
scripts\run_backend.cmd
```

L’environnement **photolab** est défini dans [environment.yml](environment.yml) :
Python 3.12, NumPy 2.2, OpenCV 4.12, pywin32, PyYAML et dépendances de test.
Le serveur est local à Windows et utilise pywin32 ; le parcours de lancement complet n’est pas disponible sur Linux ou macOS.

Pour vérifier le catalogue sans ouvrir l’interface, à la racine du dépôt :

```cmd
conda activate photolab
cd backend
python -m app.check_plugins
```

Pour lancer l’application complète depuis la racine : `scripts\run_all.cmd`.

## Un plugin comporte trois déclarations

```text
plugins/plugins.yaml        -> liste les dossiers activés
plugins/tint/plugins.yaml   -> classe, chemin Python et paramètres
plugins/tint/plugin.py      -> classe TintPlugin(ImagePlugin)
```

Les plugins vivent sous `plugins/`, pas dans un nouveau dispatch de `app/processing/`.
Le YAML enfant contient `file: plugin.py`, chemin relatif au dossier du plugin.
L’identifiant doit être unique ; les paramètres correspondent exactement à ceux de `__init__`.
Les imports relatifs, par exemple `from .helpers import helper`, fonctionnent dans le dossier.

## Exemple complet : palette et intensité

Copiez [l’exemple Python](../docs/examples/tint/plugin.py) dans `plugins/tint/plugin.py` :

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

Puis créez `plugins/tint/plugins.yaml` avec [ce YAML](../docs/examples/tint/plugins.yaml) :

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

Ajoutez `- tint` à la liste existante de [plugins/plugins.yaml](../plugins/plugins.yaml).
Validez le catalogue avec `python -m app.check_plugins` depuis `backend/`.
Lancez l’application, ou utilisez **Reconnect**, puis choisissez **Teinte personnalisée** et cliquez **Apply**.

Le YAML donne une valeur par défaut à chaque paramètre. Sans `default` YAML, le paramètre devient obligatoire,
même si le constructeur Python possède une valeur par défaut.
Les paramètres sont validés **avant** l’instanciation ; la classe est instanciée à chaque application.

## Comment la couleur arrive à la palette WinUI

```yaml
color:
  type: color_bgra
  label: Couleur de teinte
  default: [0, 0, 255, 128]
```

Ce champ entraîne une palette WinUI avec alpha et quatre nombres **B, G, R, A**.
Lorsque l’utilisateur choisit une couleur, WinUI transmet une liste JSON dans cet ordre.
Le binder Python valide les quatre entiers 0–255 et fournit une copie indépendante à `__init__(color: list[int], ...)`.

`color_rgba` n’est pas un type YAML reconnu. Pour travailler en RGBA dans votre algorithme :

```python
b, g, r, a = self.color
rgba = (r, g, b, a)
```

La décision vient du **type**, pas du nom du champ.
`color: {type: text}` créerait un champ texte ; `accent: {type: color_bgra}` créerait la palette.

| YAML | WinUI | Python |
| --- | --- | --- |
| `text` | TextBox | `str` |
| `text` avec `choices` | ComboBox | Un des `str` autorisés |
| `integer` | NumberBox, entier | `int` |
| `number` | NumberBox, décimal | `float` |
| `boolean` | CheckBox | `bool` |
| `color_bgra` | PluginColorInput → ColorPicker avec alpha | `list[int]`, [B,G,R,A] |
| `color_hsv` / `hsv` | PluginColorInput → roue H/S et barre V | `list[int]`, [H,S,V] |
| `integer` + `palette_channel` | Barre colorée liée au canal sélectionné | `int` entre 0 et 255 |

Pour HSV, H est entre **0 et 179**, S/V entre **0 et 255**.
`visible_when` affiche un paramètre selon les choix d’un autre champ ; un paramètre masqué
doit posséder un défaut YAML et prend ce défaut lorsque WinUI omet sa valeur.
`palette_channel` et ses contraintes sont détaillés dans [PLUGINS.md](../docs/PLUGINS.md).

## La liaison avec le front, fichier par fichier

| Étape | Fichier responsable |
| --- | --- |
| Charger YAML, classes et annotations ; produire le catalogue | [catalog.py](app/plugins/catalog.py) |
| Valider couleurs et paramètres | [parameters.py](app/plugins/parameters.py) |
| Répondre à `list_plugins` et aux commandes de traitement | [receiver.py](app/ipc/receiver.py) |
| Représenter la description côté C# | [PluginDescriptor.cs](../ui/Contracts/PluginDescriptor.cs) |
| Construire les champs et collecter les arguments | [ProcessingPanel.Plugins.cs](../ui/Views/ProcessingPanel.Plugins.cs) |
| Afficher palette, roue HSV et barres | [PluginColorInput.cs](../ui/Controls/ColorPicker/PluginColorInput.cs) |
| Envoyer l’opération et transférer les résultats | [ImageProcessingService.Plugins.cs](../ui/Services/ImageProcessingService.Plugins.cs) |
| Isoler les entrées, appeler et valider le plugin | [executor.py](app/plugins/executor.py) |

Le flux commun est :

```text
list_plugins -> description des paramètres -> Properties
Apply -> run_operation(operation="run_plugin")
      -> validation des arguments -> nouvelle instance de la classe
      -> apply(image, selection, masks)
      -> validation du lot -> transfert BGRA -> versions dans la bibliothèque
```

Extrait des paramètres d’une commande, après enregistrement du plugin :

```json
{
  "operation": "run_plugin",
  "params": {
    "plugin_id": "tint",
    "arguments": {
      "color": [0, 0, 255, 128],
      "amount": 0.4
    }
  }
}
```

Cet extrait ne constitue pas une commande IPC complète : le client fournit aussi
version, request_id, identifiants, buffers et sélection.
Le nom `color` devient le mot-clé Python `color=...` ; `amount` devient `amount=...`.
Aucun contrôle ou bouton dédié à `tint` n’est ajouté dans C#.

## Contrat de apply et helpers publics

```python
def apply(self, image, selection, masks):
    ...
```

- `image` : NumPy `uint8 (H, W, 4)`, BGRA et alpha droit ; l’image entière est fournie.
- `selection` : `Rect(x, y, width, height)` ou `None`, sans découpage préalable.
- `masks` : liste NumPy `uint8 (H, W)`, mêmes dimensions ; les calques BGRA du transport sont décodés avant l’appel.
- Retour : image BGRA ou liste non vide de 1 à 64 images ; `None`, tuples, images vides ou mauvais dtype sont refusés.

| Helper exporté par `app.plugins` | Usage |
| --- | --- |
| `validate_image` | Valider une image BGRA |
| `validate_image_selection` | Valider ensemble image, sélection et masques |
| `validate_bgra` | Valider/copier [B,G,R,A] |
| `validate_hsv` | Valider/copier [H,S,V] dans les plages OpenCV |
| `apply_masked_result` | Garder le résultat dans l’union des masques intersectée avec la sélection |
| `encode_mask` | Encoder un masque (H,W) dans le retour BGRA opaque attendu |

Le moteur protège les originaux avec des copies et valide l’intégralité du lot.
Pour une image résultat de mêmes dimensions, il restaure les pixels hors sélection.
Les masques ne sont pas combinés automatiquement par le moteur : le plugin choisit son comportement.
Avec `apply_masked_result`, une liste vide traite toute la sélection ; un masque entièrement noir ne traite aucun pixel.

Un résultat de dimensions différentes devient une image indépendante.
Pour un générateur `output_kind: mask`, B=G=R encode la valeur du masque et A=255.
La gestion des sources, `input_kind: painting`, l’extérieur du masque et les lots sont détaillés dans [PLUGINS.md](../docs/PLUGINS.md).

## Vérifier et déboguer

Depuis la racine du dépôt :

```cmd
scripts\test_backend.cmd tests/test_plugins.py -q
```

Pour vérifier les exemples documentés dans un catalogue isolé :

```cmd
conda activate photolab
python scripts\verify_docs.py
```

Ce contrôle charge l’exemple par le vrai catalogue, valide ses paramètres et vérifie les pixels,
la sélection, les masques et l’alpha, sans ajouter le plugin au catalogue de production.

| Erreur | À vérifier |
| --- | --- |
| `plugin_configuration` | Chemin relatif, classe concrète ImagePlugin, annotations, YAML et paramètres |
| Valeur hors limites | Bornes YAML, ordre des canaux et validation du constructeur |
| `invalid_result` | dtype uint8, 4 canaux, dimensions positives, contrat des sorties |
| Plugin absent après modification | `python -m app.check_plugins`, puis **Reconnect** |
| Résultat hors masque | Combinaison de masques dans le plugin ; utiliser un helper si adapté |

Un plugin est du **code Python exécuté sur le PC**. Utilisez les plugins dont vous connaissez la provenance ;
le chargeur de classes ne constitue pas un bac à sable.

Pour l’IPC, les processus, les quotas, l’historique et la maintenance du front,
poursuivez dans [FURTHER](../FURTHER/README.md).
La suite complète conserve des échecs connus décrits dans [STATUS.md](../docs/STATUS.md).
