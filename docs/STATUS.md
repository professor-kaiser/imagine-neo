# État de la version de publication

Constat sur Windows, le **8 novembre 2026**, avant préparation de la publication.

## Suite complète du backend

Commande : `scripts\test_backend.cmd`.

**444 tests collectés : 422 réussites, 22 échecs.**

- 16 échecs concernent la segmentation k-means : résultats recadrés/transparents,
  sélection et masques, espace colorimétrique, région vide et limites de clusters.
- 3 échecs concernent les variantes de flou. Les tests Average/Median attendent encore
  des fonctions non implémentées ; la variante bilatérale n’a pas de raccordement complet.
- 3 échecs concernent rotation/scale. Le code produit désormais des résultats,
  alors que ces tests attendent encore `OperationNotImplementedError`.

Les 6 échecs liés à des attentes de placeholders et au bilatéral ne constituent pas
une validation des nouveaux algorithmes. Les 16 échecs de segmentation signalent
des écarts au contrat testé. Le projet est présenté comme une version en développement.

## Périmètre de la démo et du workflow de publication

La démo utilise `to_grayscale` et la classe `DenoisePlugin` réelle du projet.
`scripts/build_demo.py` génère une scène synthétique déterministe, sans photographie externe,
puis vérifie les entrées, la conservation de l’alpha et les pixels hors sélection.

Le workflow `publication.yml` exécute les tests niveaux de gris, dessin et plugins,
puis vérifie les liens et métadonnées du site et l’exactitude des résultats précalculés.
Son statut porte uniquement sur ce périmètre.
La suite complète reste disponible avec `scripts/test_backend.cmd`.

Le site ne fait tourner ni Python ni WinUI dans le navigateur.
Le comparateur affiche des PNG calculés par le moteur du projet.
La capture de l’atelier de dessin provient d’un test réel de l’interface ; elle peut précéder les derniers changements.

## Fonctionnalités encore incomplètes

- Filtre bilatéral.
- Séparation des canaux, pipette et extraction de palette.
- Helper générique `apply_to_selection`.
- Conformité de la segmentation aux contrats testés.
- Sauvegarde et restauration d’un projet complet avec historique et masques.

Le code de traitement existant est publié avec cet état explicite.
