# Imagine Neo — Ajouter des fonctionnalités

Ce guide explique comment compléter un outil existant ou ajouter une nouvelle fonctionnalité à Imagine Neo. L’application utilise **C# / WinUI 3** pour l’interface et **Python / NumPy / OpenCV** pour les traitements d’image. L’interface est en anglais ; ce guide reste en français.

Pour installer et lancer le projet, consultez le [README principal](README.md). Pour les détails du serveur Python, consultez le [README du backend](backend/README.md).

## 1. Choisir les fichiers à modifier

| Votre objectif | Où intervenir |
| --- | --- |
| Compléter un filtre déjà proposé dans l’interface | Sa fonction dans `backend/app/processing/`, puis ses tests |
| Ajouter un nouveau filtre au menu | Fonction Python, déclaration et validation de l’opération, schéma JSON, panneau WinUI et tests |
| Ajouter un paramètre à un filtre existant | Signature Python, validation des paramètres, schéma JSON, contrôles WinUI et tests |
| Ajouter une analyse, par exemple un histogramme | Traitement Python et prise en charge du nouveau résultat dans les contrats, le transport et l’interface |
| Modifier l’apparence ou les interactions | `ui/Views/`, `ui/Controls/`, `ui/ViewModels/` et, selon le besoin, `ui/MainWindow.xaml.cs` |
| Ajouter l’export, l’historique ou des calques | Services et modèles C#, interface, puis contrat/backend si nécessaire ; les interfaces réservées ne suffisent pas |

## 2. Comprendre le parcours d’une opération

```text
Panneau d’outils : choix du filtre et de ses paramètres
  ui/Views/ProcessingPanel.xaml et .xaml.cs
        ↓
Événement Appliquer et affichage du résultat
  ui/MainWindow.xaml.cs
        ↓
Envoi de run_operation et préparation de la mémoire de sortie
  ui/Services/ImageProcessingService.cs
        ↓
Validation de la commande et des paramètres Python
  backend/app/ipc/protocol.py + backend/app/models/operations.py
        ↓
Réception, appel du traitement et validation de son résultat
  backend/app/ipc/receiver.py + backend/app/operation_service.py
        ↓
Votre fonction dans backend/app/processing/
        ↓
Retour du résultat, miniature et nouvelle image dans la bibliothèque
```

Les commandes et paramètres passent en JSON sur un pipe Windows. Les pixels passent par la mémoire partagée. Un traitement image crée un document dérivé ; les images restent en mémoire pour la session.

## 3. Compléter une fonction déjà présente

Les dix outils suivants sont déjà raccordés à l’interface. Conservez leur nom, leur signature et leur type de retour lorsque vous complétez leur implémentation.

| Fonction | Fichier dans `backend/app/processing/` | Paramètres en plus de `image` | Retour |
| --- | --- | --- | --- |
| `to_grayscale` | [filters.py](backend/app/processing/filters.py) | `selection` | Image complète |
| `apply_blur` | [filters.py](backend/app/processing/filters.py) | `blur_type`, paramètres de la variante, `selection` | Image complète |
| `apply_border` | [filters.py](backend/app/processing/filters.py) | `border_type`, `thickness`, `color`, `selection` | Image complète |
| `apply_contours` | [filters.py](backend/app/processing/filters.py) | `threshold1`, `threshold2`, `selection` | Image complète |
| `apply_color_separation` | [filters.py](backend/app/processing/filters.py) | `mode`, `selection` | Image complète |
| `apply_to_selection` | [selection_ops.py](backend/app/processing/selection_ops.py) | `selection`, `operation`, `params` | Image complète |
| `replace_selection_with_color` | [selection_ops.py](backend/app/processing/selection_ops.py) | `selection`, `color` | Image complète |
| `replace_selection_with_region` | [selection_ops.py](backend/app/processing/selection_ops.py) | `selection`, `source_image`, `source_region` | Image complète |
| `pick_color` | [color_tools.py](backend/app/processing/color_tools.py) | `x`, `y` | `ColorRGBA` |
| `extract_palette` | [color_tools.py](backend/app/processing/color_tools.py) | `selection`, `max_colors` | Liste de `ColorFrequency` |

1. Ouvrez la fonction et lisez sa docstring, notamment `Implementation guide (OpenCV)`.
2. Complétez son corps. `to_grayscale` fournit un exemple de validation, de sélection et de préservation de l’alpha.
3. Ajoutez des tests qui vérifient les pixels attendus et les cas limites.
4. Rechargez le backend avec **Reconnect**, ou fermez l’application et relancez `scripts\run_all.cmd`. Un backend démarré manuellement peut aussi être arrêté puis relancé depuis son terminal.
5. Importez une image, choisissez l’outil et cliquez sur **Apply** ou **Analyze**.

Une modification limitée au Python ne demande pas de recompilation C#. Le bouton d’un filtre appelle directement sa fonction : il fonctionne indépendamment du helper `apply_to_selection`, dont le corps reste à compléter.

Une fonction qui renvoie `None`, notamment avec `pass`, affiche **Coming soon**. Un résultat qui ne respecte pas le contrat produit `invalid_result`.

## 4. Respecter le format des images

Les types sont définis dans [image_types.py](backend/app/models/image_types.py).

| Élément | Contrat |
| --- | --- |
| Tableau d’image | `numpy.ndarray`, type `uint8`, forme `(hauteur, largeur, 4)` |
| Ordre des canaux | **B, G, R, A**, avec alpha droit, non prémultiplié |
| Sélection | `Rect(x, y, width, height)` en pixels, dans les limites de l’image |
| Absence de sélection | `None` signifie toute l’image pour les filtres ; les deux remplacements exigent une sélection |
| Indexation NumPy | `[y, x]`, puis canal ; les fins de tranches sont exclusives |
| Retour d’un filtre | Image complète de mêmes dimensions, même si seule une zone est traitée |
| Original | Préserver l’entrée et renvoyer une copie indépendante |
| Pixels hors sélection | Les conserver exactement |
| Transparence | La préserver pour les filtres de couleur ; suivre le contrat propre aux remplissages et remplacements |

Une couleur `ColorRGBA` est en ordre **R, G, B, A**. Pour écrire dans une image BGRA, utilisez `(color.b, color.g, color.r, color.a)`.

Validez les limites avant de découper une région : NumPy peut tronquer silencieusement une tranche trop grande. `np.ascontiguousarray()` ne garantit pas une copie si le tableau est déjà contigu ; ajoutez `.copy()` si vous allez modifier cette région.

## 5. Exemple : ajouter « Inverser les couleurs »

L’exemple utilise le nom technique `invert_colors`, sans paramètre supplémentaire. Il inverse les trois canaux de couleur et conserve l’alpha. **Le code ci-dessous est un exemple à intégrer ; ce guide n’ajoute pas l’outil à l’application.**

### Étape A — Écrire la fonction Python

Ajoutez cette fonction dans [filters.py](backend/app/processing/filters.py). Les imports `numpy as np`, `ImageArray` et `Rect` y sont déjà présents.

```python
def invert_colors(image: ImageArray, selection: Rect | None = None) -> ImageArray:
    """Inverse B, G et R dans la région choisie, en conservant l’alpha."""
    if not isinstance(image, np.ndarray):
        raise TypeError("image doit être un tableau NumPy")
    if image.dtype != np.uint8 or image.ndim != 3 or image.shape[2] != 4:
        raise ValueError("Format attendu : uint8 BGRA8 (hauteur, largeur, 4)")
    height, width, _ = image.shape
    if height == 0 or width == 0:
        raise ValueError("L’image doit être non vide")
    if selection is not None and not isinstance(selection, Rect):
        raise TypeError("selection doit être un Rect ou None")

    region = selection if selection is not None else Rect(0, 0, width, height)
    if region.x + region.width > width or region.y + region.height > height:
        raise ValueError("La sélection dépasse les limites de l’image")

    rows = slice(region.y, region.y + region.height)
    columns = slice(region.x, region.x + region.width)
    result = image.copy(order="C")
    result[rows, columns, :3] = 255 - image[rows, columns, :3]
    return result
```

### Étape B — Déclarer l’opération et valider ses paramètres

Dans [operations.py](backend/app/models/operations.py), ajoutez `"invert_colors"` à l’ensemble `FILTER_OPERATIONS` existant. `IMAGE_OPERATIONS` et `ALL_OPERATIONS` l’incluront ensuite automatiquement.

Dans `normalize_parameters()`, ajoutez ce cas après la vérification initiale de l’opération et avant le traitement final des paramètres imbriqués :

```python
    if operation == "invert_colors":
        _keys(params, set())
        return {}
```

Les clés supplémentaires sont ainsi refusées. Pour un filtre avec paramètres, utilisez les validations existantes telles que `_number`, `_integer` et `_color`, avec les bornes adaptées à votre outil.

[operation_service.py](backend/app/operation_service.py) recherche les noms de `FILTER_OPERATIONS` dans le module `filters`. Pour cet exemple, le routage fonctionne dès que la fonction et la déclaration existent. Une fonction placée dans un autre module demande aussi d’adapter ce routage.

### Étape C — Décrire la commande dans le schéma JSON

Dans [ipc-v1.schema.json](shared/contracts/ipc-v1.schema.json), le tableau `oneOf` contient une branche par commande/opération.

1. Repérez la branche dont `properties.message_type.const` vaut `"run_operation"` et `properties.operation.const` vaut `"to_grayscale"`.
2. Dupliquez cette branche complète dans `oneOf` et remplacez uniquement son `properties.operation.const` par `"invert_colors"`.
3. Gardez les paramètres vides, la sélection facultative et les champs de sortie : `result_image_id`, `file_name`, `output_buffer`.
4. Dans la branche `run_operation` / `apply_to_selection`, dupliquez également le cas `to_grayscale` de `properties.params.oneOf`, puis remplacez son `properties.operation.const` par `"invert_colors"`. Cela garde le schéma cohérent avec `FILTER_OPERATIONS`, qui autorise aussi le filtre comme opération imbriquée.

Pour un filtre avec paramètres, décrivez leurs clés, types et bornes dans les deux cas correspondants. Le schéma et `normalize_parameters()` doivent rester cohérents : le serveur valide les commandes avec le code Python, sans charger automatiquement ce fichier JSON.

### Étape D — Ajouter le choix dans WinUI

Dans [ProcessingPanel.xaml](ui/Views/ProcessingPanel.xaml), ajoutez cet élément au `ComboBox` nommé `OperationPicker` :

```xml
<ComboBoxItem Content="Inverser les couleurs" Tag="invert_colors"/>
```

Le `Tag` doit correspondre exactement au nom Python déclaré. `Content` est le libellé visible dans l’application.

Dans la méthode `Refresh()` de [ProcessingPanel.xaml.cs](ui/Views/ProcessingPanel.xaml.cs), remplacez l’affectation de `ParametersPanel.Visibility` par :

```csharp
ParametersPanel.Visibility = operation is "to_grayscale" or "invert_colors"
    ? Visibility.Collapsed
    : Visibility.Visible;
```

`GetFilterParameters()` renvoie déjà un dictionnaire vide pour cet outil sans paramètres. Le bouton **Apply**, la sélection, le retour d’image et la miniature utilisent le parcours existant.

Pour proposer également le filtre dans **Apply filter to selection**, ajoutez le même élément au `ComboBox` `NestedOperation` et prenez-le en charge dans l’implémentation de `apply_to_selection`. Ce helper reste à implémenter ; le choix direct **Inverser les couleurs** gère déjà une sélection grâce à son argument `selection`.

### Étape E — Vérifier le résultat et le raccordement

Créez `backend/tests/test_invert_colors.py`. Voici un premier test avec une couleur connue, une sélection et de la transparence :

```python
import numpy as np
from app.models.image_types import Rect
from app.processing.filters import invert_colors


def test_invert_colors_selection_alpha_and_original():
    pixels = np.array([[[10, 20, 30, 40], [0, 127, 255, 80]]], dtype=np.uint8)
    original = pixels.copy()
    pixels.flags.writeable = False

    result = invert_colors(pixels, Rect(1, 0, 1, 1))

    expected = np.array([[[10, 20, 30, 40], [255, 128, 0, 80]]], dtype=np.uint8)
    np.testing.assert_array_equal(result, expected)
    np.testing.assert_array_equal(pixels, original)
    assert result.dtype == np.uint8
    assert result.flags.c_contiguous
    assert not np.shares_memory(result, pixels)
```

Complétez avec l’image entière, les sélections hors limites, les types invalides et une entrée non contiguë. Ajoutez aussi `"invert_colors": {}` à `CASES` dans [test_operations.py](backend/tests/test_operations.py) : ses tests paramétrés vérifient le schéma et le raccordement avec une fonction substituée. Le test de votre véritable algorithme reste donc nécessaire.

## 6. Ajouter des paramètres ou un autre type de résultat

Pour un nouveau paramètre, par exemple une intensité :

1. Définissez son nom, son type, ses bornes et son éventuelle valeur par défaut dans le contrat Python.
2. Validez-le dans `normalize_parameters()` et décrivez-le dans le schéma JSON, y compris pour l’appel imbriqué si le filtre y est disponible.
3. Ajoutez un contrôle dans `ProcessingPanel.xaml`, lisez sa valeur dans `GetFilterParameters()` ou `GetOptions()`, puis gérez sa visibilité dans `Refresh()`.
4. Testez les valeurs limites et les paramètres manquants ou inconnus. La validation Python reste nécessaire même si le contrôle WinUI limite la saisie.

Pour une **nouvelle analyse**, telle qu’un histogramme, l’ajout à `ANALYSIS_OPERATIONS` seul ne suffit pas. Le code actuel connaît explicitement la pipette et la palette. Il faut adapter le routage, la validation et la sérialisation Python dans `operation_service.py`, la réception dans `ipc/receiver.py`, puis le schéma et les fichiers C# suivants :

| Fichier | Adaptation |
| --- | --- |
| [IpcMessages.cs](ui/Contracts/IpcMessages.cs) | Structure du résultat reçu |
| [OperationOptions.cs](ui/Models/OperationOptions.cs) | Classification `IsAnalysis` et modèle `OperationResult` |
| [NamedPipeClient.cs](ui/Services/NamedPipeClient.cs) | Type de réponse attendu pour l’opération |
| [ImageProcessingService.cs](ui/Services/ImageProcessingService.cs) | Lecture et validation du résultat |
| [ProcessingPanel.xaml.cs](ui/Views/ProcessingPanel.xaml.cs) | Paramètres et libellé du bouton d’analyse |
| [MainWindow.xaml.cs](ui/MainWindow.xaml.cs), [MainViewModel.cs](ui/ViewModels/MainViewModel.cs) et vues concernées | État et affichage de l’analyse |

Les trois transformations Rotation/Scale disposent maintenant d’un parcours avec dimensions de sortie variables (voir leur contrat ci-dessous). Les autres opérations conservent les dimensions source ; un futur recadrage devra déclarer ses propres dimensions de sortie.

Les interfaces d’export, d’historique, de prévisualisation et de plugins sont réservées dans [EditingContracts.cs](ui/Contracts/EditingContracts.cs). Une nouvelle fonctionnalité de ce type nécessite son implémentation, son raccordement et son interface utilisateur.

## 7. Lancer et tester les changements

Les commandes suivantes s’exécutent dans **CMD**, depuis la racine du projet. Remplacez `D:\imagine-neo` si le projet se trouve ailleurs.

```cmd
cd /d D:\imagine-neo
scripts\test_backend.cmd
```

Après avoir créé le fichier de test de l’exemple, vous pouvez lancer uniquement celui-ci :

```cmd
scripts\test_backend.cmd tests/test_invert_colors.py -q
```

Après modification du C# ou du XAML, fermez l’interface, puis reconstruisez et relancez :

```cmd
scripts\build_ui.cmd
scripts\run_all.cmd
```

Si vous avez changé le contrat ou le raccordement entre Python et C#, lancez également :

```cmd
scripts\test_interop.cmd
scripts\test_ui.cmd Debug run_all
```

Le test d’interface parcourt une liste explicite d’opérations dans `MainWindow.xaml.cs`. Ajoutez-y le nouvel outil et vérifiez les attentes de [run_ui_smoke.py](backend/tests/run_ui_smoke.py) pour le couvrir automatiquement. Si vous utilisez le mode `fixtures`, adaptez aussi [fixture_backend.py](backend/tests/fixture_backend.py). Ce mode vérifie le raccordement avec des résultats de test.

Pour la vérification manuelle, importez une image couleur avec transparence, appliquez l’outil à toute l’image, puis à une petite sélection. Comparez le résultat à l’original et contrôlez les pixels hors sélection.

Les scripts utilisent l’environnement Conda `photolab`. Si une dépendance est nécessaire, mettez à jour [environment.yml](backend/environment.yml) et, pour une dépendance applicative, [pyproject.toml](backend/pyproject.toml), puis lancez `scripts\setup_conda.cmd`.

## 8. Résoudre les problèmes courants

| Symptôme | Point à vérifier |
| --- | --- |
| **Coming soon** | La fonction renvoie `None` ou lève `NotImplementedError` |
| `Unknown operation` | Nom absent des ensembles Python ou `Tag` différent du nom déclaré |
| `Unexpected or missing operation parameters` | Clés envoyées par le C# différentes de celles acceptées dans `normalize_parameters()` |
| `invalid_result` | Type, dimensions ou contenu hors sélection incorrects ; pour une analyse, vérifier aussi son type de retour |
| `opencv_error` | Lire le diagnostic : type des arguments, nombre de canaux ou région fournie à OpenCV |
| Les changements Python ne sont pas visibles | Cliquer sur **Reconnect** ou relancer la session pour charger les sources actuelles |
| Le nouvel outil n’apparaît pas | Vérifier `OperationPicker`, reconstruire l’UI et lancer la bonne configuration Debug/Release |
| Les couleurs ou la transparence sont incorrectes | Vérifier l’ordre BGRA/RGBA et la conservation du canal alpha |

## Variantes OpenCV dans Blur et Border

Les menus **Blur type** et **Border type** sont raccordés au protocole, en accès direct et dans **Filter selection**. Le flou Gaussian existant est conservé. Les nouveaux flous et les bordures restent à implémenter dans `backend/app/processing/filters.py` ; le choix d’une variante non implémentée affiche **Coming soon**, sans appliquer un Gaussian à sa place.

Les signatures conservent leurs arguments positionnels et ajoutent des options nommées :

```python
def apply_blur(image, radius=5.0, selection=None, *,
               blur_type="gaussian", kernel_size=5, diameter=9,
               sigma_color=75.0, sigma_space=75.0):
    ...

def apply_border(image, thickness, color, selection=None, *,
                 border_type="constant"):
    ...
```

| Choix Blur | `blur_type` | Paramètres transmis | API OpenCV prévue |
| --- | --- | --- | --- |
| Gaussian | `gaussian` | `radius` : sigma positif | `cv2.GaussianBlur`, noyau automatique |
| Average / Box | `average` | `kernel_size` : entier de 1 à 255 | `cv2.blur` |
| Median | `median` | `kernel_size` : entier impair de 3 à 255 | `cv2.medianBlur` |
| Bilateral | `bilateral` | `diameter` : entier de 1 à 255 ; `sigma_color`, `sigma_space` : nombres positifs ≤ 1000 | `cv2.bilateralFilter` |

L’UI n’envoie que les paramètres utiles au flou choisi. Les valeurs des autres arguments restent celles de la signature et doivent être ignorées. Pour implémenter une variante, remplacez son refus `NotImplementedError` par votre traitement ; conservez le Gaussian existant. Bilateral doit recevoir les trois canaux BGR, avec sauvegarde et restauration séparées de l’alpha.

| Choix Border | `border_type` | Constante OpenCV |
| --- | --- | --- |
| Constant | `constant` | `cv2.BORDER_CONSTANT` |
| Replicate | `replicate` | `cv2.BORDER_REPLICATE` |
| Reflect | `reflect` | `cv2.BORDER_REFLECT` |
| Reflect 101 | `reflect_101` | `cv2.BORDER_REFLECT_101` / `BORDER_DEFAULT` |
| Wrap | `wrap` | `cv2.BORDER_WRAP` |

`thickness` et `color` restent transmis pour toutes les bordures ; utilisez la couleur uniquement pour `constant`. Le panneau couleur se masque pour les autres modes. `BORDER_DEFAULT` est un alias de Reflect 101 ; `BORDER_TRANSPARENT` et `BORDER_ISOLATED` ne sont pas proposés comme modes de remplissage indépendants.

**Le contrat d’image ne change pas : mêmes dimensions, original intact et pixels extérieurs à la sélection préservés.** Une bordure reste donc intérieure. Pour les modes non constants, travaillez sur une copie du rectangle intérieur et complétez-la jusqu’à la taille de la région avec `cv2.copyMakeBorder` ; refusez une épaisseur qui ne laisse aucun intérieur. Ne renvoyez pas directement une image agrandie, qui serait refusée par le contrat existant.

Les anciens appels sans `blur_type` ou `border_type` restent valides (Gaussian et Constant par défaut). Une fois vos branches implémentées, relancez l’application ou reconnectez le backend ; aucune autre modification UI/IPC n’est nécessaire.



## Dessin UI : composition Python

La fonction `backend/app/processing/drawing.py::apply_drawing(image, mask)` est implémentée. `image` et `mask` sont des tableaux `uint8` de même forme `(height, width, 4)`, en **BGRA8 à alpha droit**. Le masque contient les couleurs B/G/R et la couverture/opacité A ; A=0 signifie aucun dessin, y compris après gommage. Les coordonnées correspondent aux pixels de l'image, indépendamment du zoom.

La composition utilise « source over » : le masque est devant l'image. L'alpha de sortie est `Af + Ab * (1 - Af)` ; les couleurs sont pondérées par ces alphas, puis ramenées à l'alpha droit. Le calcul s'effectue sur les valeurs sRGB stockées, comme dans l'aperçu UI. Les canaux sont arrondis à l'entier le plus proche ; les égalités vont vers le haut. Les intermédiaires entiers 32 bits évitent le débordement des uint8 et sont traités par blocs pour limiter la mémoire temporaire.

Le résultat est une copie BGRA8 complète, contiguë et indépendante. Les entrées ne sont pas modifiées, y compris si elles sont en lecture seule ou non contiguës. Les pixels où le masque a A=0 restent identiques octet par octet, même leurs couleurs cachées lorsque l'image est transparente. Un masque opaque remplace intégralement le pixel. La gomme de l'UI retire du masque en attente, pas de l'image déjà appliquée.

Le protocole `run_operation / apply_drawing` conserve des `params` vides, un `mask_buffer` et un `output_buffer` distincts. Les mappings restent ouverts jusqu'à la réponse. Un résultat valide crée une nouvelle version et vide le brouillon ; undo/redo permet de parcourir ces versions. Une erreur conserve le brouillon.

Tests Python : `scripts\test_backend.cmd tests/test_drawing.py -q`. Le test WinUI en mode production vérifie les pixels composés, l'application explicite et undo/redo ; le mode fixtures vérifie le transport avec un callback substitué. `scripts/test_native_drawing.ps1` vérifie le geste souris, l'application réelle et undo/redo avant et après application.

Après une modification Python, utiliser **Reconnect** ou relancer l'application pour charger le code actualisé.


## Rotation et scale : contrat prêt à implémenter

Fichier : `backend/app/processing/transforms.py`. Les trois fonctions restent vides :

```python
def rotate_image(image: ImageArray, angle_degrees: float) -> ImageArray:
    pass

def scale_image(image: ImageArray, scale_factor: float) -> ImageArray:
    pass

def rotate_scale_image(image: ImageArray, angle_degrees: float, scale_factor: float) -> ImageArray:
    pass
```

Entrée : copie indépendante `uint8` BGRA8 à alpha droit, de forme `(height, width, 4)`. L'opération porte sur l'image entière ; aucun `selection` ni masque n'est transmis. `angle_degrees` est positif dans le sens horaire, dans [−360, 360]. `scale_factor` est uniforme, dans [0.01, 8] ; 1 signifie 100 %. L'outil Rotation reçoit seulement l'angle ; Scale seulement le facteur ; Rotation + scale reçoit les deux.

Le helper `app.models.transforms.output_size(operation, params, width, height)` calcule les dimensions de sortie **sans transformer de pixels**. Il est utilisé par la validation du transport et concorde avec `ImageTransform.OutputSize` dans l'UI. Avec θ l'angle et s le facteur :

- largeur = max(1, ceil(s × (largeur_source × |cos θ| + hauteur_source × |sin θ|) − 1e−9)) ;
- hauteur = max(1, ceil(s × (largeur_source × |sin θ| + hauteur_source × |cos θ|) − 1e−9)).

La tolérance de 1e−9 élimine les résidus numériques aux angles usuels. À 90°, une image 128 × 96 devient 96 × 128 ; à 90° et 200 %, elle devient 192 × 256. Les dimensions résultantes doivent respecter les limites IPC de 32768 pixels par côté et 512 Mio.

Retour : un nouveau tableau BGRA8 contigu de forme `(hauteur_sortie, largeur_sortie, 4)`, **aux dimensions exactes prévues**. L'image d'entrée reste intacte. Les zones découvertes sont transparentes. Le centre de l'image source correspond au centre de l'image de sortie ; la convention précise des centres de pixels est donnée dans la docstring du module. Pour éviter les franges sombres, une future interpolation doit traiter les couleurs prémultipliées et l'alpha, puis restituer l'alpha droit.

Le service alloue le mapping de sortie à la nouvelle taille avant l'appel, valide le tableau renvoyé et publie les dimensions, l'empreinte et la miniature. Un retour de la taille source après un redimensionnement sera refusé. `None` ou `NotImplementedError` conserve l'aperçu ; aucun faux résultat n'est créé. Les algorithmes de transformation ne sont implémentés ni dans les fonctions Python ni dans les helpers de contrat. L'UI utilise seulement une matrice Win2D pour l'aperçu.

Après implémentation, reconnecter ou relancer le backend. Mettre à jour les tests qui attendent volontairement les placeholders : `backend/tests/test_transforms.py`, `ui/MainWindow.TransformSmokeTests.cs`, `backend/tests/run_ui_smoke.py` et `scripts/test_native_transforms.ps1`. Les tests de transport avec callbacks substitués restent utiles mais ne prouvent pas la justesse des futurs algorithmes.

## Plugins

Les nouveaux traitements utilisent une classe ImagePlugin avec `apply(image, selection, masks)`.
Le YAML racine déclare les dossiers ; chacun contient son propre `plugins.yaml`. Les résultats sont une image BGRA ou une liste de tailles libres. WinUI les affiche comme des images distinctes, sans superposition. Le paramètre `masks` contient uniquement les masques cochés : tableaux OpenCV `uint8` de forme `(H, W)` aux dimensions de l’image. Le champ YAML commun `output_kind: mask` ajoute un générateur au panneau Masks avec un formulaire de paramètres généré.
Voir le [guide de creation de plugins](docs/PLUGINS.md), le catalogue [plugins.yaml](plugins/plugins.yaml) et le [plugin de demonstration](plugins/denoise/plugin.py).
