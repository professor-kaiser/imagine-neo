# Publication GitHub et visibilité Google

## Dépôt et démo

- Dépôt public : https://github.com/professor-kaiser/imagine-neo
- Site et démo : https://professor-kaiser.github.io/imagine-neo/
- Sitemap : https://professor-kaiser.github.io/imagine-neo/sitemap.xml
- Branche de publication : `main`.
- Le workflow Pages déploie `docs/site/` après vérification du site.
- Le workflow de vérification se limite au périmètre décrit dans [STATUS.md](STATUS.md).

Le README français et anglais, une description précise et les topics GitHub aident à
découvrir le projet sur GitHub et les moteurs de recherche.
Un tag de version désigne un instant du code ; les topics sont des mots-clés de découverte.

## Vérification de propriété préparée

La balise HTML fournie par le propriétaire le 8 novembre 2026 est ajoutée dans le
`head` de la page. Après le déploiement, revenez dans la propriété Search Console
et cliquez **Valider**. Cette balise prouve la propriété ; elle ne demande pas à elle seule l’indexation.

Après validation, ouvrez **Sitemaps** et envoyez `sitemap.xml`,
puis **Inspection de l’URL** pour la page d’accueil et **Demander une indexation**.
Le compte Google du propriétaire doit effectuer ces actions.

## Google Search Console

La page contient un titre descriptif, une description, une URL canonique, les directives
`index, follow`, des métadonnées de partage et des données structurées `SoftwareApplication`.
Les fonctionnalités sont aussi décrites en texte HTML accessible sans JavaScript.

Ces éléments rendent le site prêt pour l’exploration. **Google décide de l’indexation et du classement** :
aucune position ni apparition immédiate n’est garantie.

Une fois la page en ligne :

1. Ouvrez [Google Search Console](https://search.google.com/search-console).
2. Ajoutez une propriété **Préfixe d’URL** avec `https://professor-kaiser.github.io/imagine-neo/`.
   Vous ne contrôlez pas le domaine `github.io` : utilisez le préfixe d’URL.
3. Choisissez la vérification par **fichier HTML**. Placez le fichier fourni
   par Google dans `docs/site/`, avec son nom et son contenu exacts, puis publiez-le.
   On peut aussi utiliser la balise HTML fournie par Google, dans le `head` d’`index.html`.
4. Après validation, soumettez `https://professor-kaiser.github.io/imagine-neo/sitemap.xml`.
5. Dans **Inspection de l’URL**, inspectez `https://professor-kaiser.github.io/imagine-neo/` puis cliquez **Demander une indexation**.
6. Consultez ensuite les rapports d’indexation pour voir l’état réel.

La vérification exige l’accès à votre compte Google et un fichier ou une balise fournis par Google.
Aucun code de vérification fictif n’est ajouté.

Le fichier `robots.txt` d’un moteur s’applique à la racine du domaine.
Un fichier `/imagine-neo/robots.txt` ne contrôlerait pas `github.io` :
le site utilise donc la directive meta robots dans son propre HTML.

## Après une modification

Mettez à jour le code et la documentation, puis exécutez les contrôles.
Après un push sur `main`, attendez les workflows dans l’onglet **Actions**.
Le dossier `docs/site/assets/` contient les résultats de démonstration versionnés.
Régénérez-les si le traitement utilisé change :

```cmd
conda activate photolab
python scripts\build_demo.py
python scripts\verify_publication.py
```

Les outils locaux, les compilations, les sauvegardes de travail et les documents personnels
sont exclus du dépôt par `.gitignore`.
