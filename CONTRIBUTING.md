# Contribuer à Imagine Neo

Le projet est en développement. Décrivez le problème et son résultat attendu dans une issue,
avec la version de Windows, la commande utilisée et les étapes de reproduction.
Évitez les images personnelles ; les [images de démonstration](docs/site/assets/original.png) permettent de partager un cas reproductible.

Pour développer, suivez l’installation du [README](README.md), puis créez une branche.
Les nouvelles fonctionnalités de traitement utilisent le système de plugins ;
consultez [PLUGINS.md](docs/PLUGINS.md) et [AGENTS.md](AGENTS.md).

Préservez la copie de l’original, les pixels hors sélection, l’alpha lorsque le contrat l’exige et l’historique.
Utilisez les abstractions communes de l’interface pour les paramètres.

Exécutez les scripts de vérification adaptés dans `scripts/`.
La suite complète a des échecs connus documentés dans [STATUS.md](docs/STATUS.md) :
indiquez dans votre pull request les commandes exécutées et les nouveaux échecs éventuels.

Pour une modification du site, lancez `python scripts/verify_publication.py` dans l’environnement `photolab`
et testez le comparateur au clavier comme à la souris.

Les détails de maintenance sont regroupés dans [FURTHER](FURTHER/README.md).
Pour modifier le tutoriel de plugin, vérifiez aussi les [exemples exécutables](docs/examples/README.md)
avec `python scripts/verify_docs.py` dans photolab.
