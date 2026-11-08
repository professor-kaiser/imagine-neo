# Plugins Imagine Neo

Les traitements s'ajoutent dans `plugins/`, avec une classe dérivée de
`app.plugins.ImagePlugin` et un YAML enfant. Les formulaires WinUI sont générés
à partir du catalogue : aucune branche ni interface spécifique par plugin.

## Démarrer avec un exemple complet

Le [README principal](../README.md#créer-un-plugin-de-bout-en-bout) explique la création d’un plugin
de bout en bout. Le [README du backend](../backend/README.md) montre la liaison YAML → contrôles WinUI → constructeur Python,
notamment `color_bgra`, `color_hsv`, listes de choix et champs numériques.
Les [fichiers d’exemple](examples/README.md) peuvent être copiés sous `plugins/` et exécutés dans un catalogue isolé.

Ce guide est la référence détaillée des entrées, des masques, des paramètres et des résultats.

## Organisation

`plugins/plugins.yaml` déclare les dossiers :

```yaml
version: 1
plugins:
- laplacian
- denoise
- masks
- paint_mask
```

Chaque dossier contient son `plugins.yaml`. Exemple :

```yaml
version: 1
plugins:
  - id: luminosite
    name: Luminosité
    description: Ajoute une valeur aux canaux de couleur.
    file: plugin.py
    class: LuminositePlugin
    output_kind: image
    parameters:
      decalage:
        type: integer
        label: Décalage
        default: 20
        min: -255
        max: 255
```

`file` est relatif au dossier du YAML enfant. Les chemins absolus et sortants
sont refusés. Les noms de dossiers/modules sont des identifiants Python.
Les imports relatifs fonctionnent sans exécuter les éventuels `__init__.py`.
Les identifiants sont uniques et suivent `[a-z][a-z0-9_]*`.
`enabled: false` désactive une définition. Les erreurs sont isolées par dossier.
**Reconnect** recharge les Python/YAML et restaure les images de la session.

## Contrat Python : image BGRA et masques OpenCV

```python
import numpy as np
from app.plugins import ImagePlugin, validate_image_selection, apply_masked_result

class LuminositePlugin(ImagePlugin):
    def __init__(self, decalage: int):
        self.decalage = decalage

    def apply(self, image, selection, masks):
        validate_image_selection(image, selection, masks)
        result = image.copy()
        result[:, :, :3] = np.clip(
            result[:, :, :3].astype(np.int16) + self.decalage, 0, 255
        ).astype(np.uint8)
        return apply_masked_result(image, result, selection, masks)
```

L'appel est toujours `instance.apply(image, selection, masks)` :

- `image` : image entière NumPy `uint8`, forme `(H, W, 4)`, **BGRA**,
  alpha droit. L'image n'est pas découpée avant l'appel.
- `selection` : `Rect(x, y, width, height)` ou `None`, en coordonnées image.
- `masks` : liste de tableaux NumPy **`uint8`, forme `(H, W)`**, exactement
  aux dimensions de l'image. Zéro exclut un pixel ; toute valeur non nulle l'active.
  Utilisables directement par OpenCV, par exemple
  `cv2.bitwise_and(image, image, mask=masks[0])`.

**Changement de contrat** : les anciens calques BGRA ne sont plus les entrées
`masks`. Un plugin qui utilisait `masks[i][:, :, 3]` doit maintenant utiliser
`masks[i]`. Les buffers BGRA ne sont acceptés que comme enveloppe de transport
décrite plus bas.

L'utilisateur coche les masques dans **Properties → Masks for this transformation**.
Aucun masque n'est choisi automatiquement. Les choix sont mémorisés par plugin
pendant la session ; seuls les masques présents sur l'image et compatibles sont envoyés.
L'ordre est celui de la liste **Masks**, de haut en bas.
Un masque de taille incompatible reste disponible pour l'historique mais est désactivé
dans le choix des entrées. Il n'est jamais redimensionné implicitement.

Le moteur fournit des copies contiguës indépendantes et instancie le plugin à chaque
appel. Modifier les entrées ne modifie ni l'original ni l'historique.
`validate_image_selection` vérifie l'image, la sélection et les masques.
`validate_image` vérifie une image BGRA seule.

`apply_masked_result(image, result, selection, masks)` conserve les pixels d'origine
hors de l'union des masques, intersectée avec la sélection. Une liste vide traite
toute la sélection (ou toute l'image) ; une liste contenant un masque entièrement
noir ne traite aucun pixel. Denoise et Laplacian utilisent cette convention.
Un autre plugin peut combiner les masques autrement ou les analyser individuellement ;
le moteur ne suppose pas une combinaison universelle.

## Création et édition des masques

L'onglet **Masks** remplace l'ancienne pile de calques superposés.
Un masque ne change ni l'image affichée, ni son alpha, ni l'export.

1. Importez l'image source dans la bibliothèque, puis sélectionnez l'image à traiter.
2. Dans **Masks**, cliquez sur **New mask** et choisissez la source.
3. Choisissez le mode :
   - **Image as mask** : tous les pixels colorés non nuls et visibles deviennent
     blancs, les autres noirs ; inversion optionnelle.
   - **Convert image to mask** : liste déroulante `gray`, `blue`, `green`,
     `red`, `alpha` ou `hsv`, bornes inclusives, inversion et noyau de flou
     gaussien impair (1 désactive le flou).
4. Tracez éventuellement une sélection rectangulaire avec **Select region**. Le panneau
   indique les coordonnées et dimensions utilisées ; **Clear selection** revient à toute l’image.
5. **Create mask** enregistre le masque et affiche son aperçu noir/blanc.
6. Dans les propriétés d'un plugin, cochez les masques à transmettre puis appliquez.

Chaque borne garde ses champs numériques, synchronisés avec le contrôle visuel.
Pour les canaux simples, une **barre de valeur colorée** est toujours visible :
noir vers rouge, vert, bleu ou blanc pour `gray`. Le canal `alpha` utilise
un dégradé transparent vers opaque sur un damier. La barre accepte le clic, le
glissement et les touches du clavier, avec un pas de 1.

En mode **hsv**, chaque borne propose une **roue circulaire teinte/saturation**
et un curseur de luminosité, sous **HSV color wheel**. Les triplets
**H : 0–179, S/V : 0–255** restent modifiables et synchronisés.
Les composantes numériques restent exactes, même pour une couleur noire ou désaturée.
La roue règle H/S et reste colorée quelle que soit la valeur de V. La barre **Value (V)**
règle uniquement la luminosité ; l’aperçu sous la barre montre la couleur effective.
Déplacer la roue à V=0 ou modifier V ne réinitialise pas les autres composantes.

La teinte est circulaire : une borne H inférieure supérieure à la borne H supérieure
sélectionne l'intervalle passant par zéro. Exemple pour les rouges :
`lower_hsv: [170, 100, 100]`, `upper_hsv: [10, 255, 255]`.
S et V doivent toujours avoir des bornes croissantes. Le flou HSV est appliqué aux
couleurs BGR avant conversion, pour ne pas mélanger artificiellement les teintes
situées de part et d'autre de zéro.

La source doit avoir les dimensions exactes de la cible, comme les masques OpenCV.
Les pixels transparents restent exclus dans les modes couleur ; le canal `alpha`
permet explicitement de sélectionner selon la transparence.

Sélectionner un masque recharge ses paramètres et son rectangle de création. Ajustez ensuite
la sélection pour changer sa région avec **Update mask**. Sans sélection, toute l’image est
utilisée. Le masque reste de taille `(H, W)`. **Outside selection** choisit la valeur hors
rectangle : **Black (0)** par défaut ou **White (255)**. **Invert** agit dans le rectangle,
indépendamment de ce choix. Sélectionnez un masque, choisissez White (255), puis cliquez
sur **Update mask** pour rendre son extérieur blanc. Le réglage est sans effet sans sélection. Le rectangle appartient à l’image cible, même si la source vient d’une
autre image de la bibliothèque. Le rectangle et la valeur hors sélection sont conservés
avec les versions Undo/Redo.

**Update mask** régénère ses valeurs
depuis la source sauvegardée, même après retrait de cette source de la bibliothèque.
On peut renommer, dupliquer, supprimer (y compris le dernier masque) et réordonner.
Les versions et les masques sont conservés par **Undo/Redo** ; la sélection des entrées
d'un plugin reste un choix de travail, sans créer de version.
Les masques persistent dans les versions transformées. Appliquez ou effacez un dessin
en attente avant de les modifier.

## Peindre un masque sur une image

Dans **Masks**, choisissez une image de référence, puis **Paint a mask** dans
**Creation mode**. Le canevas affiche cette référence pendant la peinture, sans
modifier les pixels du document.

- **Brush / Eraser** ajoutent ou retirent la couverture du masque.
- **Selection (Rect)** dans **Paint tool**, ou **Select region**, permet de tracer ou
  redimensionner la région sans quitter Masks. Revenez à Brush/Eraser pour peindre.
  Le contour reste visible pendant la peinture et l’aperçu est limité au rectangle.
  Les traits restent éditables ; dans la sélection, seuls les pixels peints deviennent
  actifs. À l’extérieur, **Outside selection** fixe la valeur à 0 ou 255. Clear selection
  permet de réutiliser les traits sur toute l’image.
- **Brush size** règle la taille en pixels de l'image, indépendamment du zoom.
- **Preview color** choisit le noir ou le blanc pour voir le dessin sur la référence.
  Cette couleur ne change pas le masque : chaque pixel couvert devient **255**,
  chaque pixel non couvert **0**. Le plugin transforme aussi les bords anticrénelés
  couverts en blanc.
- **Clear mask painting** efface le brouillon ; Undo restaure les traits.
- **Create mask** enregistre le masque ; sélectionnez-le ensuite pour continuer
  à peindre et utilisez **Update mask**.

Les brouillons de masque sont séparés du dessin normal et conservés par image
pendant la session. Dans le mode Paint, Undo/Redo parcourt les modifications du
brouillon, puis les versions enregistrées. Un masque enregistré recharge ses traits
comme base de travail ; les retouches ne modifient pas les versions précédentes.
La référence, les traits et les paramètres d'aperçu sont conservés avec le masque.
La référence doit avoir les dimensions du document cible.

Appliquez ou effacez un dessin normal en attente avant de passer à la peinture de
masque. Les masques peints se cochent dans les propriétés des plugins exactement
comme les masques obtenus par seuillage.

## Plugins générateurs de masques

Le champ commun `output_kind` vaut `image` par défaut ou `mask`.
Les générateurs `mask` apparaissent dans le sélecteur de mode du panneau Masks.
Le même formulaire YAML sert aux générateurs et aux transformations.
Consultez `plugins/masks/plugin.py` et son YAML.

Le champ commun `input_kind` vaut `image` par défaut. Un générateur de masque peut
annoncer `input_kind: painting` pour activer l'éditeur de peinture commun. Dans ce
cas, `apply(image, selection, masks)` reçoit le dessin BGRA sur fond transparent
comme image d'entrée ; la référence visuelle reste intacte. Le plugin
`plugins/paint_mask/plugin.py` transforme sa couverture alpha en masque binaire.
Aucune branche WinUI ne dépend de l'identifiant de ce plugin.

Le retour reste une image BGRA : pour un masque, **B=G=R=valeur du masque** et
**alpha=255**. WinUI extrait un octet par pixel, conservé séparément des pixels image.
Un générateur utilisé par l'atelier doit retourner une seule image aux dimensions
de sa source. Pour un appel Python avec sélection, le moteur met à zéro les pixels
du masque hors sélection par défaut. Le paramètre commun optionnel `mask_outside_value`
de `run_plugin` accepte **0** ou **255** et permet de choisir un extérieur blanc.
Il est placé à côté de `plugin_id` et `arguments`, jamais dans les paramètres du constructeur.
Il est réservé aux générateurs `output_kind: mask` et ne modifie pas les pixels dans le Rect.
Sans sélection, il n’a aucun effet. Un retour de dimensions différentes est refusé lorsqu’un
rectangle est fourni, car ses coordonnées ne pourraient pas être appliquées sans ambiguïté.

Les générateurs peuvent utiliser `app.plugins.encode_mask(values, selection, outside_value=0)` :
ce helper convertit un tableau `uint8 (H, W)` en BGRA opaque et remplit l’extérieur avec
la valeur choisie (0 ou 255).
Les générateurs image, seuil et peinture l’utilisent aussi lors d’appels Python directs.
Le moteur impose également cette restriction aux plugins tiers.

## Résultats des transformations

Retournez une image BGRA ou une **liste non vide d'images BGRA**.
Le moteur normalise en liste ; tailles libres et vues NumPy sont acceptées.
Les tableaux vides, types incorrects, tuples et listes invalides sont refusés.
Limites : 32 768 pixels par côté, 512 Mio par image, 64 résultats par lot ;
le registre conserve sa limite de 1 Gio / 128 images.

Chaque résultat devient une **image distincte dans la bibliothèque**, avec ses propres
dimensions, miniature et historique. Les résultats multiples ne sont pas superposés.
Tous les résultats sont préparés avant publication ; une erreur annule le lot.

Pour un résultat aux dimensions de l'entrée et une sélection, le moteur conserve
exactement les pixels extérieurs. Un résultat de taille différente est une image
indépendante, sans collage ni redimensionnement ; sa sélection est effacée.
L'original reste disponible. Chaque résultat peut être annulé jusqu'à sa source.

Les masques, paramètres et sources sont conservés pendant la session ; **Save as**
exporte les pixels de l'image en PNG/JPEG, pas un projet avec masques.

## Segmentation k-means : état actuel

Le plugin de segmentation propose un mode k-means avec nombre de clusters et espace
colorimétrique. Son implémentation actuelle possède des échecs de validation connus :
sélection et masques, transparence, dimensions des segments et limites de clusters.
Consultez [STATUS.md](STATUS.md) avant de l’utiliser comme référence d’implémentation.

Le contrat commun permet de retourner des images de dimensions libres ; il ne prouve pas
que chaque plugin existant satisfait déjà toutes les exigences de ses tests.
Pour démarrer un nouveau plugin, utilisez plutôt le [tutoriel de teinte](../README.md#créer-un-plugin-de-bout-en-bout)
et [DenoisePlugin](../plugins/denoise/plugin.py), qui montrent les copies, l’alpha et les masques.

## Paramètres du constructeur

| Type YAML | Type Python | Champ généré |
| --- | --- | --- |
| text | str | Texte, ou liste déroulante avec `choices` |
| integer | int | Nombre entier et barre colorée si `palette_channel` est déclaré |
| number | float | Nombre décimal |
| boolean | bool | Case à cocher |
| color_hsv | list[int] | Palette circulaire HSV et trois entiers H/S/V |
| color_bgra | list[int] | Palette couleur avec alpha et quatre entiers B/G/R/A |
| hsv | list[int] | Alias compatible de color_hsv |

Les clés correspondent exactement aux paramètres du constructeur.
Champs : `type`, `label`, `description`, `default`, `min`, `max`, `choices`,
`visible_when`, `palette_channel`.
Sans défaut YAML, un paramètre est obligatoire ; sans paramètres, utilisez
`parameters: {}` et omettez le constructeur. Noms réservés :
`image`, `selection`, `masks`, `self`.

Noms inconnus, types incorrects, booléens numériques, nombres non finis et hors limites
sont refusés. Nombres dans ±9 007 199 254 740 991 ; texte limité à 4 096 caractères.
Les annotations correspondent au YAML. Paramètres nommés après `*` acceptés ;
`*args`, `**kwargs` et paramètres exclusivement positionnels refusés.
Les contraintes entre paramètres sont vérifiées dans le constructeur avec `ValueError`.

Les extensions sont communes à tous les plugins :

- `choices` : liste non vide de chaînes uniques pour un champ `text`. Les valeurs
  libres sont refusées côté Python aussi bien que dans la liste WinUI.
- `visible_when: {channel: [hsv]}` : affiche le champ selon la valeur d'un autre
  paramètre à choix. Plusieurs conditions sont combinées par ET. Le champ doit
  avoir un défaut ; lorsqu'il est masqué, WinUI omet sa valeur et le binder prend
  le défaut. Son ancien texte numérique invalide ne bloque donc pas le mode actif.
- `palette_channel: channel` : associe un entier borné entre 0 et 255 à une barre colorée
  selon le canal sélectionné dans un autre paramètre à choix. Valeurs reconnues :
  `gray`, `blue`, `green`, `red`, `alpha`. Les bornes HSV utilisent le type `hsv`.
- `color_hsv` (ou `hsv`) : liste de trois entiers, transportée dans un tableau JSON puis copiée
  par le binder. H est borné à 179, S/V à 255. Les booléens, nombres décimaux,
  mauvaises dimensions et valeurs hors plage sont refusés.
  `app.plugins.validate_hsv(value)` est également disponible pour les constructeurs.
- `color_bgra` : liste de quatre entiers `[B, G, R, A]`, chacun entre 0 et 255.
  Le canal alpha et les couleurs des pixels transparents sont conservés. Le binder
  fournit une copie indépendante ; `app.plugins.validate_bgra(value)` permet aussi
  une validation explicite dans un constructeur.

Exemple : voir `lower`, `upper`, `lower_hsv` et `upper_hsv` dans
`plugins/masks/plugins.yaml`. Les contrôles sont générés sans branche par plugin.

Exemple de paramètres YAML, valables pour n'importe quel plugin :

```yaml
parameters:
  accent:
    type: color_hsv
    label: Couleur HSV
    default: [120, 255, 255]
  tint:
    type: color_bgra
    label: Couleur BGRA
    default: [0, 0, 255, 128]
```

Le constructeur reçoit les listes dans le même ordre de canaux :

```python
def __init__(self, accent: list[int], tint: list[int]):
    self.accent = accent  # H 0..179, S/V 0..255
    self.tint = tint      # B, G, R, alpha 0..255
```

C'est le **type YAML**, pas le nom du paramètre, qui choisit la palette.
Les noms `accent` et `tint` peuvent être remplacés par les noms de votre constructeur.
Omettre `default` rend la couleur obligatoire ; l'interface refuse une couleur
incomplète avant l'appel. Les listes de mauvaise taille, booléens, décimaux et
valeurs hors limites sont également refusés côté Python.

## Transport et erreurs

1. `list_plugins` fournit le catalogue avec `output_kind` et `input_kind`.
2. `run_operation/run_plugin` transmet les paramètres, la sélection et uniquement
   les `mask_buffers` cochés.
3. Le transport existant BGRA8 encode chaque masque avec B=G=R et alpha=255.
   Le backend vérifie cette enveloppe et décode un tableau contigu **(H, W)**
   avant tout appel de plugin. Les anciens calques couleur sont refusés.
4. `plugin_results_ready` annonce dimensions, identifiants et SHA-256.
5. WinUI alloue les mappings ; `read_plugin_results` transfère sans réexécution.
6. Empreintes vérifiées, `release_plugin_results` libère le lot, même sur erreur.
   Une déconnexion libère aussi les lots temporaires.

Les pixels restent en mémoire partagée, jamais dans JSON. Le mode historique à un
`output_buffer` reste accepté pour une sortie unique de taille compatible.

## Lancement et vérification

`scripts\run_all.cmd [Debug|Release]` compile l'interface, valide le catalogue,
puis démarre une session backend à jour et WinUI.
`--no-build` réutilise une compilation existante ; `--check` compile et vérifie sans
ouvrir l'interface. Une erreur de compilation/catalogue bloque le lancement.

```bat
scripts\test_backend.cmd tests/test_segmentation.py tests/test_mask_outside_value.py tests/test_mask_selection.py tests/test_paint_masks.py tests/test_hsv_masks.py tests/test_masks.py tests/test_plugins.py tests/test_protocol.py tests/test_server.py
scripts\build_ui.cmd Debug
scripts\test_interop.cmd
scripts\test_ui.cmd Debug run_all fixtures
```

Les tests couvrent masques OpenCV, création/édition, canaux, seuils, inversion, flou,
choix par plugin, transport, dimensions, pixels hors masque/sélection, résultats
multiples indépendants, nettoyage sur erreur et historique.
