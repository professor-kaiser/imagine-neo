# Architecture technique

[Documentation technique](README.md) · [Backend et plugins](../backend/README.md) · [Contrat des plugins](../docs/PLUGINS.md)

## Modules et responsabilités

| Emplacement | Responsabilité |
| --- | --- |
| `ui/Controls/` | Canvas, sélection, zoom/pan et contrôles de couleur |
| `ui/Views/` | Catalogue et panneau de paramètres commun |
| `ui/Models/` et `ui/ViewModels/` | Documents, masques, transformations et état observable |
| `ui/Services/` | Import/export, historique, processus Python, pipes et mappings |
| `backend/app/plugins/` | API publique, catalogue YAML, factory, validation et exécution |
| `plugins/` | Classes de traitement et générateurs de masques |
| `backend/app/ipc/` | Protocole, Named Pipe, dispatch et mémoire partagée |
| `backend/app/storage/` | Registre d’images propriétaire et quotas |
| `backend/app/processing/` | Opérations historiques et contrats existants |
| `shared/contracts/` | Schéma JSON IPC version 1 |

## Session backend et rechargement

Le backend Python persiste pendant la session.
Le lancement complet choisit une session propre si aucun pipe explicite n’est imposé.
Une réponse `pong` annonce le PID et l’empreinte SHA-256 des sources chargées.
Le client vérifie cette empreinte pour éviter de réutiliser du code périmé.

**Reconnect** recharge le backend et le catalogue, puis restaure les documents ouverts.
Modifier Python ne recharge pas automatiquement un processus déjà lancé.
Les identifiants techniques `PhotoLab`, `photolab` et les variables `PHOTOLAB_*` sont conservés.

Variables utiles depuis CMD :

| Variable | Usage |
| --- | --- |
| `PHOTOLAB_CONDA` | Chemin de conda.bat pour les scripts |
| `PHOTOLAB_PYTHON` | Interpréteur du backend pour les chemins de lancement qui le consultent |
| `PHOTOLAB_ROOT` | Racine du projet |
| `PHOTOLAB_PIPE_NAME` | Session/pipe explicite |

`run_all.cmd` active toujours l’environnement `photolab` et déduit son interpréteur depuis Conda.

## JSON pour les commandes, mémoire partagée pour les pixels

Le transport utilise un Named Pipe Windows local, flux d’octets.
Les messages JSON UTF-8 se terminent par LF et contiennent `protocol_version: 1` et `request_id`.
Le serveur gère lectures partielles et messages groupés. Une ligne supérieure à 64 Kio ferme la connexion fautive après erreur.

Les pixels ne sont pas encodés dans JSON.
C# crée un mapping nommé ; Python ouvre ce mapping existant avec les API Windows.
Les octets commencent à l’offset zéro, sans en-tête ajouté.

Format image : `uint8 (H, W, 4)`, BGRA8, alpha droit, stride `width * 4`.
Le rendu Win2D utilise une copie prémultipliée ; elle ne remplace pas les données originales.

Le schéma est [ipc-v1.schema.json](../shared/contracts/ipc-v1.schema.json).
Les contraintes arithmétiques de taille/stride et les contrats de pixels sont aussi validés par le code.

## Catalogue et opérations de plugins

Le catalogue enfant déclare le Python, la classe et les paramètres.
Le chargeur crée un namespace par racine et vérifie les chemins, les identifiants,
l’héritage ImagePlugin, les signatures et les annotations.
Les erreurs de dossier/définition sont exposées dans `plugin_errors`.
Le préflight `app.check_plugins` rend ces erreurs bloquantes pour le lanceur complet.

La découverte repose sur `list_plugins` → `plugins_listed`.
L’exécution utilise `run_operation` avec `operation: run_plugin`.
Le client commun transmet `plugin_id`, `arguments`, sélection et buffers des masques choisis.
Le constructeur reçoit les arguments validés ; `apply` reçoit des copies contiguës indépendantes.

Pour les lots et dimensions libres :

1. `plugin_results_ready` annonce les résultats, dimensions, identités et SHA-256.
2. WinUI alloue les mappings et demande `read_plugin_results`.
3. Le backend transfère les résultats calculés, sans réexécuter le plugin.
4. Le client vérifie les empreintes, publie les versions puis libère le lot avec `release_plugin_results`.

Une erreur de contrat annule le lot ; une déconnexion libère les lots temporaires.
Le mode historique avec un seul `output_buffer` reste accepté pour une sortie compatible.

## Masques et conservation des pixels

Les masques applicatifs sont des tableaux OpenCV `uint8 (H,W)`.
Le transport BGRA les encode avec B=G=R et A=255 ; le backend valide puis décode l’enveloppe.
Le plugin reçoit les masques mono-canal, jamais des calques couleur arbitraires.

Les paramètres de sélection sont des coordonnées de l’image entière.
Un résultat de mêmes dimensions conserve les pixels hors sélection.
Une image de dimensions différentes est une sortie indépendante, sans collage dans le rectangle d’origine.
La combinaison de masques relève du plugin ; le helper public propose une union intersectée avec le rectangle.

Pour les générateurs de masques, `output_kind: mask` garde la même enveloppe BGRA opaque de sortie.
`mask_outside_value` vaut 0 ou 255 et est un paramètre commun du moteur, pas un argument du constructeur.

## Registre, historique et limites

Le registre possède des copies indépendantes et protège les originaux.
Limites : 32 768 pixels par dimension, 512 Mio par image,
1 Gio / 128 images dans le registre, 64 résultats par lot.
Des copies supplémentaires CPU/GPU existent : le moteur n’est pas un système de grandes images par tuiles.

Le front gère l’historique des dessins en attente, des images appliquées et des masques.
`Save as` exporte la version appliquée en PNG ou JPEG.
La session ne constitue pas une sauvegarde de projet persistante.

## Erreurs et portée des vérifications

Le backend normalise les exceptions en erreurs JSON à code textuel.
Une exception OpenCV utilise `opencv_error`, jamais son attribut natif numérique `.code`.
Une réponse incorrecte ne doit pas créer une version ni fermer une session valide.

Les tests d’interop utilisent les mêmes classes C# de pipe/mapping que l’interface.
Les tests avec fixtures injectent des retours déterministes pour vérifier le transport et l’UI ;
ils ne prouvent pas les algorithmes de production.
Voir [DEVELOPPEMENT.md](DEVELOPPEMENT.md) et [STATUS.md](../docs/STATUS.md).
