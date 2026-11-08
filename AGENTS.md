# Conventions imagine-neo

- Les nouvelles fonctionnalités de traitement d'image doivent utiliser le système de plugins :
  un sous-dossier dans `plugins/`, une classe dérivée de `app.plugins.ImagePlugin`,
  un `plugins.yaml` dans ce sous-dossier et son chemin dans `plugins/plugins.yaml`.
- Le YAML enfant déclare le chemin relatif au dossier du plugin, le nom de classe
  et les paramètres du constructeur. Le moteur fournit l'image entière, la sélection
  nullable et les calques BGRA à `apply(image, selection, masks)`.
- Le retour est une image BGRA ou une liste non vide d'images BGRA de dimensions libres.
- Ne pas ajouter de bouton, de formulaire spécifique ou de branche par plugin dans WinUI.
  Étendre les abstractions communes si le contrat doit évoluer.
- Préserver la copie de l'original, les pixels hors sélection et l'historique.
- Consulter `docs/PLUGINS.md` et le plugin `plugins/denoise/plugin.py`.
- Vérifier les modifications avec les scripts appropriés dans `scripts/`.
