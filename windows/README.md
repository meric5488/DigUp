# Fouineur 2 pour Windows

Recherche unifiée, entièrement locale, dans :

- **Zimbra** : boîte personnelle, boîtes supplémentaires (comptes délégués) et dossiers partagés montés ;
- **Microsoft 365** : OneDrive, bibliothèques SharePoint, canaux Teams (un document par fil) et conversations Teams (un document par conversation et par jour) ;
- **Fichiers** : dossiers locaux, lecteurs réseau et partages UNC (`\\serveur\partage`).

Pour chaque source, on choisit précisément quoi indexer (sites, bibliothèques, dossiers, équipes, canaux, conversations, dossiers mail) avec des cases à cocher. Cocher un élément inclut son contenu présent et futur ; décocher le retire de l'index dès la fermeture des réglages.

Les résultats sont classés par catégorie (Mails, Teams, SharePoint, OneDrive, Fichiers), s'affichent en **liste** ou en **vignettes**, avec un **aperçu** (les gestionnaires d'aperçu de l'Explorateur Windows pour les documents, une vue lisible pour les mails et Teams). Double-clic ou Entrée : ouverture depuis l'emplacement d'origine (fichier, document Office dans l'application de bureau, fil Teams, mail dans la messagerie par défaut). Ctrl+Entrée : ouvrir l'emplacement.

## Compiler

Prérequis : Windows 10/11 x64, **SDK .NET 10** (`winget install Microsoft.DotNet.SDK.10`).

```powershell
cd Fouineur
dotnet run --project tests\Fouineur.Tests   # 33 tests
dotnet run --project src\Fouineur.App -c Release
# version distribuable :
.\scripts\publier.ps1
```

## Recherche

| Syntaxe | Effet |
|---|---|
| `budget 2026` | tous les mots (préfixes acceptés, accents ignorés) |
| `"plan de charge"` | expression exacte |
| `-brouillon` | exclure un mot |
| `de:martin` | auteur / expéditeur |
| `titre:devis` | dans le titre ou l'objet |
| `type:pdf,docx` | par extension |

Si un modèle est installé, la recherche « par le sens » complète les mots exacts (résultats marqués *par le sens*).

## Microsoft 365 : inscription d'application

L'application **Fouinneur** dans Entra ID doit être un client public :

1. **Authentification › Ajouter une plateforme › Applications mobiles et de bureau**, avec les URI de redirection :
   - `ms-appx-web://microsoft.aad.brokerplugin/<ID d'application>` (connexion via le compte Windows, WAM) ;
   - `https://login.microsoftonline.com/common/oauth2/nativeclient`.
2. **Autoriser les flux clients publics** : Oui.
3. **Autorisations Microsoft Graph déléguées** : `User.Read`, `Files.Read.All`, `Sites.Read.All`, `Team.ReadBasic.All`, `Channel.ReadBasic.All`, `ChannelMessage.Read.All`, `Chat.Read`.
   `ChannelMessage.Read.All` exige le **consentement administrateur**.

Dans Fouineur : Sources › Microsoft 365 › ID d'application, puis **Se connecter**.

## Zimbra

Adresse du serveur (`https://mail.exemple.fr`), identifiant et mot de passe (chiffré par DPAPI pour la session Windows). La double authentification est gérée : le code est demandé une fois, puis le jeton est conservé jusqu'à son expiration.
Boîtes supplémentaires : ajouter l'adresse du compte délégué (droit sur la boîte entière). Les dossiers partagés montés dans votre boîte apparaissent dans l'arborescence.
Sélection par défaut d'une boîte : tout sauf Corbeille et Courrier indésirable.
« Ouvrir » télécharge le message (.eml) et l'ouvre dans la messagerie par défaut ; clic droit › Ouvrir dans le webmail utilise le gabarit réglable (`{base}`, `{id}`, `{account}`, `{folder}`).

## Recherche par le sens (facultatif)

Réglages › Recherche par le sens :
- **EmbeddingGemma ONNX** (recommandé, rien ne sort du poste) : copier depuis Hugging Face `onnx-community/embeddinggemma-300m-ONNX` les fichiers `onnx/model.onnx`, `onnx/model.onnx_data` et `tokenizer.model` dans `%LOCALAPPDATA%\Fouineur\models\embeddinggemma` (ou lancer `scripts\telecharger-modele.ps1`). DirectML utilise la carte graphique si possible.
- **Ollama** : `ollama pull embeddinggemma`, puis choisir Ollama.

## Données

Tout est dans `%LOCALAPPDATA%\Fouineur` : `index.sqlite` (index unique : texte intégral FTS5 + vecteurs), `settings.json`, `secrets` (chiffré DPAPI), `msal-*.cache`, `cache\` (aperçus téléchargés, mails .eml), `logs\`.

## Architecture

```
src/Fouineur.Core     net10.0, sans dépendance NuGet (testé sous Linux)
  Index/              SQLite natif (P/Invoke), FTS5 + vecteurs, fusion RRF
  Selection/          règles inclure/exclure (la plus proche gagne, héritage)
  Extraction/         texte, HTML, Word/Excel/PowerPoint, OpenDocument, RTF, EML
  Embedding/          interface, Ollama, découpage, vectorisation en tâche de fond
  Connectors/         Local (fichiers + UNC), M365 (Graph delta), Zimbra (SOAP JSON, SyncRequest)
  Sync/               orchestration, recherche
src/Fouineur.App      WPF .NET 10 (thème Fluent), MSAL + WAM, ONNX Runtime DirectML, PdfPig, IFilter, DPAPI,
                      aperçus Windows (IPreviewHandler), miniatures (IShellItemImageFactory)
tests/Fouineur.Tests  33 tests (index, sélection, extraction, connecteurs avec faux serveurs)
```

## À vérifier en conditions réelles

- Tokenisation d'EmbeddingGemma avec `Microsoft.ML.Tokenizers` (LlamaTokenizer sur `tokenizer.model`).
- Double authentification Zimbra 10.1 et URL de webmail.
- Delta des canaux Teams : les réponses ajoutées à un ancien fil sont-elles signalées par le delta ?
- Gros volumes (plusieurs centaines de milliers d'éléments) : temps de vectorisation et mémoire des vecteurs.
