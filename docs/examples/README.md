# Exemples de plugins

Le dossier [tint/](tint/plugin.py) contient un plugin de teinte complet, avec sa classe
Python et son [YAML enfant](tint/plugins.yaml).
Son paramètre `color_bgra` génère une palette avec alpha ; `amount` génère un champ numérique.

Pour l’ajouter dans l’application, copiez le dossier sous `plugins/tint/` et ajoutez
`- tint` à la liste **existante** dans `plugins/plugins.yaml`.
Le [tutoriel principal](../../README.md#créer-un-plugin-de-bout-en-bout) détaille les étapes.

Le [catalogue d’exemples](plugins.yaml) sert uniquement à la vérification de la documentation.
Il ne modifie pas le catalogue de production.

Depuis la racine, dans l’environnement Conda `photolab` :

```cmd
python scripts\verify_docs.py
```

Ce contrôle utilise le chargeur et l’exécuteur réels du projet dans un catalogue isolé.
