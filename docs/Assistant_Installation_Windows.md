# Assistant d'installation Windows AtmLogAgent

Ce document décrit le nouvel assistant graphique `AtmLogAgent.SetupWizard.exe`. Il est destiné aux installations sur ATM Windows, où un administrateur doit saisir les paramètres SFTP et les informations d'exploitation non détectables dans une interface visuelle, comme dans un installateur Windows classique.

---

## 1. Objectif

Les ATM bancaires sont majoritairement sous Windows. Une installation uniquement par script PowerShell est efficace pour les déploiements automatisés, mais moins adaptée à une intervention terrain où un administrateur doit renseigner :

- le serveur SFTP client ;
- le port SFTP ;
- l'utilisateur SFTP ;
- l'empreinte de clé hôte SFTP ;
- les informations banque/pays/ville lorsque le provisioning automatique n'est pas disponible ;
- les chemins de logs ATM si l'auto-détection ne suffit pas ;
- les URL de supervision et de mise à jour.

Le projet contient maintenant deux modes complémentaires :

- `Install-AtmLogAgent.ps1` pour l'installation silencieuse ou automatisée ;
- `AtmLogAgent.SetupWizard.exe` pour l'installation interactive avec formulaire.

---

## 2. Projet ajouté

Le nouveau projet est :

```text
src/AtmLogAgent.SetupWizard/
```

Il s'agit d'une application WinForms `.NET 8` ciblant :

```text
net8.0-windows
```

Le manifeste Windows demande une exécution administrateur :

```text
requestedExecutionLevel=requireAdministrator
```

Cette élévation est nécessaire pour :

- copier les fichiers dans `C:\Program Files\AtmLogAgent` ;
- écrire dans `C:\ProgramData\AtmLogAgent` ;
- créer ou mettre à jour le service Windows ;
- écrire la variable d'environnement de service `ATMAGENT_DATA_DIR` dans le registre Windows.

---

## 3. Champs de l'assistant

### 3.1 Onglet Installation

| Champ | Description | Valeur par défaut |
|---|---|---|
| Répertoire d'installation | Dossier contenant `AtmLogAgent.Service.exe`, dépendances et `appsettings.json` | `C:\Program Files\AtmLogAgent` |
| Répertoire de données | Dossier persistant calculé automatiquement pour clés, buffer SQLite, positions et logs | `C:\ProgramData\AtmLogAgent` |
| Nom du service Windows | Nom SCM du service | `AtmLogAgent` |
| Générer une clé SSH ED25519 | Crée `agent_ed25519` si absent | activé |
| Installer le service Windows | Crée ou met à jour le service | activé |
| Démarrer le service | Démarre le service après configuration | activé |

### 3.2 Onglet Identité ATM

| Champ | Description |
|---|---|
| Banque | Nom de la banque. `AUTO` laisse l'agent utiliser le provisioning ou la résolution automatique. |
| Pays | Pays d'exploitation. |
| Ville | Ville d'exploitation. |
| Identifiant ATM | Non saisi par l'administrateur. La valeur reste `AUTO` et l'agent utilise le numéro de série, la MAC ou le hostname au démarrage. |
| Fabricant | Fabricant ATM, par exemple `NCR` ou `Diebold`. |
| Modèle | Modèle informatif de l'ATM. |

### 3.3 Onglet SFTP

| Champ | Description |
|---|---|
| Hôte SFTP | DNS ou IP du serveur SFTP client. |
| Port SFTP | Port TCP, généralement `22`. |
| Utilisateur SFTP | Compte SFTP fourni par le client. |
| Empreinte clé hôte SFTP | Fingerprint attendu pour le pinning SSH. |

L'empreinte est obligatoire. Le bot refuse une connexion SFTP lorsque `ValidateServerCertificate=true` et qu'aucun fingerprint n'est configuré.

### 3.4 Onglet Avancé

| Champ | Description |
|---|---|
| URL heartbeat | Endpoint de supervision. |
| URL serveur de mise à jour | Endpoint de mise à jour. Les mises à jour restent désactivées par défaut. |
| Chemins de logs ATM | Un chemin par ligne. Laisser vide active l'auto-détection. |

---

## 4. Fichiers générés

Après validation, l'assistant génère ou met à jour :

```text
C:\Program Files\AtmLogAgent\appsettings.json
C:\ProgramData\AtmLogAgent\provisioning.conf
C:\ProgramData\AtmLogAgent\keys\agent_ed25519
C:\ProgramData\AtmLogAgent\keys\agent_ed25519.pub
C:\ProgramData\AtmLogAgent\Logs\
C:\ProgramData\AtmLogAgent\Backups\
```

La configuration générée respecte la racine :

```json
{
  "AtmAgent": {
  }
}
```

Le service Windows est configuré avec :

```text
AtmLogAgent.Service.exe --configdir "C:\Program Files\AtmLogAgent"
```

La variable runtime suivante est écrite dans la clé de registre du service :

```text
ATMAGENT_DATA_DIR=C:\ProgramData\AtmLogAgent
```

---

## 5. Sécurité appliquée

### 5.1 Clés SSH

L'assistant crée une paire ED25519 :

```text
agent_ed25519
agent_ed25519.pub
```

La clé privée reste dans `C:\ProgramData\AtmLogAgent\keys`. La clé publique est affichée à la fin de l'installation afin que l'administrateur puisse la transmettre ou l'installer côté serveur SFTP client.

### 5.2 ACL Windows

Le répertoire de données est restreint à :

- `BUILTIN\Administrators` ;
- `NT AUTHORITY\SYSTEM`.

Cela protège :

- la clé SSH privée ;
- la clé AES locale ;
- le buffer SQLite ;
- les positions de lecture ;
- les journaux d'audit.

### 5.3 Pinning SFTP

Le champ `Empreinte clé hôte SFTP` est obligatoire. L'assistant normalise la valeur en retirant :

- `MD5:` ;
- les `:` ;
- les `-`.

La valeur finale doit être une empreinte MD5 hexadécimale de 32 caractères. L'objectif est d'éviter les erreurs de saisie courantes et de produire une valeur compatible avec la comparaison effectuée par l'agent via SSH.NET.

---

## 6. Publication du bundle Windows

Un script de publication a été ajouté :

```text
scripts/windows/publish-setup.ps1
```

### Publication framework-dependent

```powershell
.\scripts\windows\publish-setup.ps1
```

Ce mode exige que le runtime .NET 8 soit installé sur l'ATM.

### Publication self-contained

```powershell
.\scripts\windows\publish-setup.ps1 -SelfContained
```

Ce mode embarque le runtime .NET dans le dossier de publication. Il est plus volumineux mais plus simple sur les ATM verrouillés.

### Sortie générée

Par défaut :

```text
publish\windows-setup\
```

Ce dossier doit contenir notamment :

```text
AtmLogAgent.Service.exe
AtmLogAgent.SetupWizard.exe
```

Sur l'ATM, lancer en administrateur :

```text
AtmLogAgent.SetupWizard.exe
```

---

## 7. Procédure terrain recommandée

1. Publier le bundle Windows depuis le poste de build.
2. Copier le dossier `publish\windows-setup` sur l'ATM.
3. Lancer `AtmLogAgent.SetupWizard.exe` en administrateur.
4. Saisir les informations SFTP fournies par le client.
5. Saisir uniquement les informations d'exploitation non automatiques. L'identifiant ATM et le répertoire de données sont déterminés par l'agent.
6. Cliquer sur `Installer`.
7. Copier la clé publique affichée dans le serveur SFTP client.
8. Vérifier que le service `AtmLogAgent` est démarré.

Commandes de contrôle :

```powershell
Get-Service AtmLogAgent
Get-Content C:\ProgramData\AtmLogAgent\Logs\agent-*.log -Tail 100
```

---

## 8. Limites connues

- L'assistant ne remplace pas un MSI signé. Il fournit une expérience graphique interne au projet.
- La génération de clé SSH utilise `ssh-keygen`, disponible sur Windows récents avec OpenSSH Client. Si l'outil est absent, l'installation signale l'erreur.
- Le test de connexion SFTP n'est pas encore intégré dans l'interface. La validation complète reste faite au démarrage de l'agent et via les logs.
- Les mises à jour automatiques restent désactivées par défaut. Elles ne doivent être activées que lorsque la chaîne de signature et le serveur de mise à jour sont prêts.
