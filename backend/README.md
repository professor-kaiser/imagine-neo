# Backend Imagine Neo

Pour ajouter un nouvel outil de bout en bout, consultez le [guide de développement en français](../README_DEVELOPPEMENT.md).

Processus Python 3.12 persistant pour Windows. Il reçoit des commandes via Named Pipe et conserve les images dans un registre NumPy. Les dix fonctions sont raccordées à l’interface : seules leurs implémentations Python restent à compléter. `to_grayscale` fonctionne déjà ; `apply_blur` contient votre implémentation en cours et huit autres corps restent à `pass` avec leurs guides OpenCV.

## Configuration — CMD Windows

Depuis la racine :

```cmd
cd /d D:\imagine-neo
scripts\setup_conda.cmd
scripts\run_backend.cmd
```

Ou explicitement avec Conda :

```cmd
cd /d D:\imagine-neo
call .tools\miniforge3\condabin\conda.bat activate photolab
cd backend
python -m app.main
```

`environment.yml` déclare Python 3.12, NumPy 2.2, OpenCV 4.12, pywin32 311 et les dépendances de test pytest/jsonschema. L’environnement est nommé `photolab`, sans `venv`. Le script de configuration utilise Conda pour gérer les binaires OpenCV ; il n’est pas nécessaire de lancer `pip install` sur ce projet.

## Répartition du code

| Module | Responsabilité |
| --- | --- |
| `app.main` | Configuration du serveur et journalisation de sa disponibilité |
| `app.launcher` | Démarrage supervisé : réponse `pong` avant ouverture de l’UI |
| `app.runtime_info` | Empreinte des sources au démarrage et identité du processus pour détecter un backend périmé |
| `ipc.named_pipe_server` | Serveur Win32 local pywin32, flux d’octets et reconstitution des lignes |
| `ipc.protocol` | Validation stricte JSON, identités, version, dimensions et format |
| `ipc.receiver` | Réception, dispatch, confirmations, erreurs par requête |
| `ipc.error_responses` | Normalisation des exceptions Python/OpenCV en réponses JSON à code textuel |
| `ipc.shared_memory_io` | Ouverture explicite des objets mémoire Windows existants |
| `models` | Dataclasses et type `ImageArray`, BGRA8 uint8 `(height, width, 4)` |
| `models.operations` | Identifiants préparés et validation des paramètres scalaires, couleurs et rectangles |
| `operation_service` | Appel de la fonction correspondante, copies des originaux et validation des résultats |
| `storage.image_registry` | Copie propriétaire et immuable des images, quotas, récupération/suppression |
| `processing` | Niveaux de gris fonctionnels ; flou utilisateur en cours et huit contrats à compléter |

## Transport et durée de vie

Le nom du pipe est commun au client C# et au serveur Python : `PhotoLab_v1_<utilisateur>`. `--pipe-name NOM` ou `PHOTOLAB_PIPE_NAME` permet de créer une session distincte.

Ce nom par utilisateur reste celui du backend lancé séparément. Le lancement complet `run_all.cmd` choisit maintenant un pipe `PhotoLab_session_<UUID>` si aucun nom n’est fourni explicitement : chaque nouvelle session charge le code Python actuel. Un `pong` inclut `backend_revision` (SHA-256 des fichiers `app/**/*.py` au démarrage) et `backend_pid`. L’UI et le lanceur comparent cette empreinte aux sources locales avant réutilisation. Une empreinte absente ou ancienne entraîne une nouvelle session privée, sans arrêter le processus externe. Le bouton **Reconnect** permet aussi de charger vos changements puis de restaurer les images ouvertes. L’empreinte n’est pas recalculée dans le vieux processus : il ne peut donc pas prétendre avoir chargé une modification faite après son démarrage.

Chaque message JSON UTF-8 se termine par LF et contient `protocol_version: 1` et un `request_id`. Le format des imports et réponses est décrit dans `..\shared\contracts\ipc-v1.schema.json`. Les erreurs de JSON, de dimensions ou de mapping produisent une réponse structurée ; les commandes suivantes restent recevables. Un dépassement de 64 Kio ferme la connexion fautive après une erreur. La reconnexion conserve le registre tant que le processus reste lancé.

La mémoire partagée contient uniquement les octets BGRA8, sans en-tête. `OpenFileMappingW` garantit qu’un mapping absent est rejeté, plutôt que créé implicitement. `MapViewOfFile` ouvre la plage ; NumPy copie les données avant la fermeture de la vue. Le registre réalise sa propre copie et interdit les écritures sur les originaux. L’UI peut fermer son mapping après `image_imported` sans invalider le registre.

`read_shared_image` lit ; `write_shared_image` écrit dans un mapping **déjà existant**, notamment pour renvoyer les pixels du filtre de niveaux de gris à l’UI. L’UI reste propriétaire de ce mapping jusqu’à la confirmation de traitement et à la lecture du résultat.

Les réponses d’import incluent un SHA-256 des octets. Les tests C#/Python et WinUI comparent cette empreinte au buffer original ; aucune donnée pixel n’est encodée dans le JSON. Les images restent en mémoire, sans persistance après arrêt du processus.

## Commandes prises en charge

`ping` → `pong`, `import_image` → `image_imported`, `run_operation` → `image_processed` / `color_picked` / `palette_extracted`, `remove_image` → `image_removed`, `list_images` → `images_listed`, `shutdown` → `shutdown_ack`. La commande historique `to_grayscale` reste acceptée.

`run_operation` reçoit `image_id`, `operation` et `params`. Pour les opérations image, il reçoit aussi `result_image_id`, `file_name`, `output_buffer` et une `selection` facultative (`x`, `y`, `width`, `height`). Les analyses n’allouent pas de mapping de sortie. Une sélection absente ou `null` signifie l’image entière, sauf les remplacements qui exigent un rectangle cible. Les paramètres JSON sont transformés en `ColorRGBA`/`Rect` avant l’appel de vos fonctions. Les images source et cible sont copiées afin d’isoler le registre de vos écritures. Une erreur d’écriture du résultat annule son enregistrement.

Une fonction qui renvoie `None`, comme un corps à `pass`, produit une erreur `not_implemented` que l’UI affiche comme **Coming soon**. Une forme/dtype incorrecte, des pixels modifiés hors sélection, une couleur mal typée ou des fréquences de palette incohérentes produisent `invalid_result`. Les exceptions de votre fonction sont rapportées sans arrêter la session backend.

Les exceptions `cv2.error` produisent toujours le code textuel `opencv_error`, jamais leur attribut natif numérique `.code`. Le diagnostic original reste dans `message`, limité en longueur pour respecter la limite de transport. Cette normalisation corrige l’erreur C# « The JSON value could not be converted to System.String », au champ `$.code`. La session reste utilisable après un échec d’algorithme ; aucun résultat n’est ajouté et les originaux sont conservés.

Une importation est limitée à 512 Mio, 32768 pixels par dimension, quatre canaux, stride `width * 4` et taille `height * stride`. Le registre est limité par défaut à 1 Gio et 128 images. Une copie transitoire supplémentaire peut exister pendant l’import.

## Vérification

```cmd
cd /d D:\imagine-neo
scripts\test_backend.cmd
scripts\test_interop.cmd
scripts\build_ui.cmd
scripts\test_ui.cmd
```

Les 100 tests Python passent sur ce PC. Ils couvrent les vrais mappings/pipes Windows, les niveaux de gris, le routage de chaque fonction, les réponses d’erreur OpenCV et le démarrage d’une session à jour. Le harnais C# comporte 22 vérifications, dont une vraie exception OpenCV suivie d’une conversion en gris, le diagnostic d’un ancien serveur et la création d’une session privée lorsqu’une empreinte est absente ou périmée. Le test WinUI normal teste les dix contrôles ; `scripts\test_ui.cmd Debug run_all fixtures` vérifie aussi leurs retours positifs avec un backend de test. Ce mode n’implémente aucun algorithme de production : il injecte en mémoire des copies d’images et des valeurs d’analyse déterministes uniquement pour vérifier le raccordement.

Votre flou actuel provoque encore une erreur d’algorithme. `scripts\test_ui.cmd Release run_all --expect-error apply_blur=opencv_error` vérifie explicitement son affichage et la continuité de la session avec votre vrai backend. Ce test a réussi après la correction ; le diagnostic reste dans `artifacts\ui-error-report.json`. Sans cette option, toute erreur de traitement reste un échec du test normal. Après avoir corrigé votre fonction, utilisez à nouveau le test sans `--expect-error`.

Les tests `Debug run_ui` et `Debug run_all` ont aussi réussi avec `--default-pipe --expect-error apply_blur=opencv_error`, en laissant l’ancien backend utilisateur lancé. Ils vérifient le chemin de démarrage habituel et l’empreinte des sources effectivement chargées. Le diagnostic comparatif conservé dans `artifacts\blur-existing-backend.json` montre l’ancien `code: -5` ; `artifacts\blur-fresh-backend.json` montre `code: "opencv_error"` avec les mêmes paramètres et le code actuel. Le registre préexistant est resté inchangé.

## Traitements réservés

`to_grayscale` dans `filters.py` est fonctionnelle. Elle valide les entrées, utilise `cv2.COLOR_BGRA2GRAY` puis `cv2.COLOR_GRAY2BGRA`, restaure l’alpha et insère uniquement la région convertie dans une copie complète. Huit fonctions de `selection_ops.py`, `filters.py` et `color_tools.py` restent à `pass`. `apply_blur` contient votre code, inchangé par cette correction du transport.

Dans ce flou, votre correction utilise bien `cv2.blur(roi, (radius, radius))` et réinsère le résultat dans la sélection. Le rayon transmis reste flottant alors que le noyau de `cv2.blur` attend deux entiers. Le contrat existant décrit un sigma gaussien, utilisé par `cv2.GaussianBlur` avec `ksize=(0, 0)` et `sigmaX/sigmaY=radius`. Le test direct avec un rayon entier produit une image de bonnes dimensions mais modifie aussi son alpha ; cette préservation reste à assurer. Ces corrections d’algorithme vous restent réservées. Votre fichier et les anciennes indications « not implemented » de sa docstring ont été laissés intacts.

## Implémenter vos fonctions avec OpenCV

**Vous complétez le flou et écrivez les huit algorithmes encore vides.** Ils possèdent une section anglaise `Implementation guide (OpenCV)` et un commentaire `TODO`. Remplacez leur `pass` par votre implémentation, ou poursuivez votre code pour le flou. `to_grayscale`, déjà fonctionnelle, sert d’exemple avec validation, conversion, préservation de l’alpha et copie du résultat.

Pour les fonctions prévues, il n’y a plus de raccordement à écrire : ni nouveau message, ni modification de schéma, ni changement C#/XAML. Après modification, fermez l’UI et le backend, puis relancez `scripts\run_all.cmd`. Le redémarrage charge vos nouvelles fonctions ; aucune compilation C# n’est nécessaire. Gardez les noms, signatures et types de retour existants. **Implémenter `apply_blur` seul suffit pour le bouton Flou** : les filtres directs ne passent pas par le helper encore vide `apply_to_selection`.

| Fonction | Paramètres fournis par l’UI |
| --- | --- |
| `to_grayscale` | `image`, `selection` |
| `apply_blur` | `image`, `radius`, `selection` ; options nommées `blur_type`, `kernel_size`, `diameter`, `sigma_color`, `sigma_space` |
| `apply_border` | `image`, `thickness` (int), `color` (`ColorRGBA`), `selection` ; option nommée `border_type` |
| `apply_contours` | `image`, `threshold1`, `threshold2` (float), `selection` |
| `apply_color_separation` | `image`, `mode` (`red`, `green`, `blue`, `alpha`), `selection` |
| `replace_selection_with_color` | `image`, `selection`, `color` (`ColorRGBA`) |
| `replace_selection_with_region` | `image`, `selection`, `source_image` (tableau complet), `source_region` (`Rect`) |
| `pick_color` | `image`, `x`, `y` (int) ; aucune sélection |
| `extract_palette` | `image`, `selection`, `max_colors` (int, 1 à 256 pour l’IPC) |
| `apply_to_selection` | `image`, `selection`, `operation` (nom du filtre), `params` (dict typé) |

Les fonctions image renvoient le tableau complet BGRA8 `uint8` de mêmes dimensions, pas seulement la région. La pipette doit renvoyer `ColorRGBA`. La palette doit renvoyer une liste de `ColorFrequency` de couleurs distinctes, avec des comptes couvrant toute la région et `frequency = count / nombre_de_pixels`. Les valeurs doivent respecter les docstrings ; le transport ne corrige pas silencieusement un résultat incorrect.

En Python, OpenCV travaille directement avec des tableaux NumPy : une image `np.ndarray` n’indique donc pas l’absence d’OpenCV. Aucun objet `cv::Mat` C++ ni conversion JSON n’est nécessaire. Les imports `import cv2` et `import numpy as np` sont déjà placés en tête des modules.

| Fichier | Fonction à compléter | API ou démarche indiquée |
| --- | --- | --- |
| [filters.py](app/processing/filters.py) | `to_grayscale` — implémentée | `cv2.cvtColor`, `COLOR_BGRA2GRAY`, `COLOR_GRAY2BGRA`, restauration de l’alpha |
| [filters.py](app/processing/filters.py) | `apply_blur` | `cv2.GaussianBlur`, rayon interprété comme sigma en pixels, alpha préservé |
| [filters.py](app/processing/filters.py) | `apply_border` | Quatre bandes intérieures avec `cv2.rectangle` et `cv2.FILLED` |
| [filters.py](app/processing/filters.py) | `apply_contours` | Conversion en gris, `cv2.Canny`, retour BGRA8 et restauration de l’alpha |
| [filters.py](app/processing/filters.py) | `apply_color_separation` | `cv2.split`, `cv2.merge` ; modes `red`, `green`, `blue`, `alpha` |
| [selection_ops.py](app/processing/selection_ops.py) | `apply_to_selection` | Validation, copie de la région, routage explicite vers vos filtres OpenCV |
| [selection_ops.py](app/processing/selection_ops.py) | `replace_selection_with_color` | `cv2.rectangle` avec `cv2.FILLED`, couleur réordonnée en BGRA |
| [selection_ops.py](app/processing/selection_ops.py) | `replace_selection_with_region` | `cv2.copyTo`, régions de tailles identiques, copie de l’alpha |
| [color_tools.py](app/processing/color_tools.py) | `pick_color` | Région d’un pixel et `cv2.cvtColor` avec `COLOR_BGRA2RGBA` |
| [color_tools.py](app/processing/color_tools.py) | `extract_palette` | `cv2.kmeans` sur des échantillons RGBA `float32`, comptage et fréquences avec NumPy |

Les guides précisent les formes intermédiaires des tableaux, l’ordre des canaux, les coordonnées des rectangles, les limites de la sélection et le comportement de l’alpha. Attention : les canaux d’entrée sont **B, G, R, A** ; `ColorRGBA` expose **R, G, B, A**. Les coordonnées du rectangle sont `(x, y)` mais l’indexation NumPy est `[y, x]`.

Testez `to_grayscale` dans l’UI avec **Niveaux de gris → Appliquer**. Une nouvelle image « nom — Gris » apparaît pour comparaison avec l’original. Étudiez ensuite `tests/test_grayscale.py` avant d’implémenter un autre filtre. La pipette et la palette utilisent **Analyze** et affichent leurs résultats dans le panneau d’analyse ; cliquer une couleur de palette actualise le sélecteur de couleur.

Les signatures et constantes OpenCV proposées ont été vérifiées dans la version **4.12.0** de l’environnement Conda `photolab`. Aucun paquet supplémentaire n’est nécessaire. Les huit fonctions à `pass` et le flou en cours ne sont pas présentés comme des algorithmes validés : les tests vérifient leur raccordement, leurs paramètres et la gestion de leurs retours. Ajoutez des tests de résultat pour chaque algorithme que vous écrirez.

Voir le [README principal](../README.md) pour la compilation sans IDE, les références officielles, les commandes de lancement et les limites des tests d’interface.

## Plugins

Les nouveaux traitements utilisent une classe ImagePlugin et une configuration YAML.
Voir le [guide de creation de plugins](../docs/PLUGINS.md), le catalogue [plugins.yaml](../plugins/plugins.yaml) et le [plugin de demonstration](../plugins/denoise/plugin.py).
