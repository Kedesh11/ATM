# 🏦 ATM Log Agent

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=for-the-badge&logo=linux&logoColor=black)

**AtmLogAgent** est un service natif C#/.NET 8 de très haute disponibilité conçu pour la collecte, la sécurisation, et la transmission temps réel des journaux de bord (logs matériels) des distributeurs automatiques de billets (ATM/DAB).

## 🌟 Fonctionnalités Clés

- 🤖 **100% Autonome (Zero-Touch)** : Résout automatiquement son identité (MAC Address, Localisation géographique via IP/TimeZone). Aucun fichier de configuration manuel requis au déploiement.
- 📡 **Transmission Résiliente** : Transfert par pool de connexions SFTP (`ConcurrentBag`) empêchant les corruptions. Reprise automatique des téléchargements après coupure réseau.
- 🔒 **Haute Sécurité Bancaire (PCI-DSS)** :
  - Buffer local propulsé par **SQLite WAL chiffré en AES-256-GCM**.
  - Clés privées (ED25519) hachées par `SHA-256`.
  - Intégration de `DPAPI` pour la protection des données sur les environnements Windows matériels.
- 📜 **Parsing Avancé** : Décode et masque à la volée les fichiers propriétaires `.jrn` des fabricants (NCR, Diebold Nixdorf).

---

## 🚀 Processus d'Installation de bout-en-bout (Windows ATM)

L'installation sur les terminaux bancaires (NCR, Diebold, Wincor) équipés de Windows peut maintenant se faire avec un assistant graphique. L'administrateur saisit les paramètres SFTP et les informations non détectables dans une interface visuelle, puis l'assistant génère la configuration, les clés et le service Windows. L'identifiant ATM et le répertoire de données restent automatiques.

Le mode PowerShell silencieux reste disponible pour les déploiements automatisés.

### Étape 1 : Préparation du bundle Windows

Depuis la racine du projet :

```powershell
.\scripts\windows\publish-setup.ps1 -SelfContained
```

Le dossier généré contient notamment :

- `AtmLogAgent.Service.exe`
- `AtmLogAgent.SetupWizard.exe`

### Étape 2 : Déploiement sur l'ATM

Transférez le dossier `publish\windows-setup\` sur l'ATM cible.

1. Ouvrez un terminal **PowerShell en tant qu'Administrateur**.
2. Lancez l'assistant :
   ```powershell
   .\AtmLogAgent.SetupWizard.exe
   ```
3. Renseignez les champs SFTP, supervision et chemins de logs si l'auto-détection ne suffit pas.
4. Cliquez sur `Installer`.

**Que fait l'assistant ?**
- Il copie l'exécutable dans le dossier sécurisé `C:\Program Files\AtmLogAgent\`.
- Il crée l'environnement de configuration dans `C:\ProgramData\AtmLogAgent\`.
- Il génère une clé SSH ED25519 `agent_ed25519`.
- Il génère `appsettings.json` avec la section `AtmAgent`.
- Il configure le pinning de clé hôte SFTP.
- Il inscrit l'Agent en tant que **Service Windows** auprès du SCM (Service Control Manager) avec un lancement automatique.
- Il démarre le service immédiatement.

### Étape 3 : Autorisation de l'ATM sur le Serveur Central (SFTP)
Pour que l'ATM puisse transmettre ses logs, le serveur SFTP central doit reconnaître sa clé de sécurité.
1. Récupérez le fichier de clé publique généré par le script sur l'ATM :
   `C:\ProgramData\AtmLogAgent\keys\agent_ed25519.pub`
2. Copiez le contenu de ce fichier et ajoutez-le dans le fichier `~/.ssh/authorized_keys` de l'utilisateur `atmagent` sur votre serveur SFTP.

### Étape 4 : Vérification
Dès l'étape 3 complétée, l'Agent (qui tourne en arrière-plan) détectera son identité, s'authentifiera sur le serveur SFTP, et commencera la transmission des logs instantanément de manière cryptée et sécurisée.

Vous pouvez vérifier le bon fonctionnement en observant les logs de l'Agent sur l'ATM :
`C:\ProgramData\AtmLogAgent\Logs\agent-xxxx.log`

---

## 📖 Documentation d'Architecture

Pour une plongée en profondeur dans l'architecture logicielle, la conformité de sécurité (DPAPI, AES) et les mécanismes de retry/résilience, consultez la **[Documentation Officielle](docs/AtmLogAgent_Documentation.md)**.

Pour le détail des correctifs de durcissement appliqués pour l'exploitation en production ATM (configuration `AtmAgent`, retry buffer, pinning SFTP, mises à jour signées, installateurs), consultez **[Correctifs de Production](docs/Correctifs_Production_AtmLogAgent.md)**.

Pour la procédure détaillée de l'interface graphique Windows, consultez **[Assistant d'installation Windows](docs/Assistant_Installation_Windows.md)**.
