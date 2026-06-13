# Correctifs de production AtmLogAgent

Ce document décrit les correctifs appliqués pour rendre l'agent exploitable dans un contexte ATM bancaire. Il complète la documentation d'architecture existante en détaillant les problèmes corrigés, les risques associés, les changements techniques et les contrôles à effectuer avant déploiement.

## 1. Binding de configuration `AtmAgent`

### Problème constaté
Le fichier `appsettings.json` structure la configuration sous la section racine `AtmAgent`, mais l'hôte du service bindait `AgentConfiguration` depuis la racine complète du fichier. Dans ce cas, les propriétés requises comme `Transmission`, `Security`, `Monitoring` ou `Retention` pouvaient rester non alimentées.

### Risque ATM
Un service Windows ou systemd pouvait démarrer avec une configuration incohérente, puis échouer lors de la création de services critiques comme le chiffrement, le buffer SQLite, le monitoring ou la transmission SFTP. Sur un ATM, cela se traduit par un agent présent mais non opérationnel, donc une perte de visibilité sur les journaux de transaction.

### Correctif appliqué
`Program.cs` récupère désormais explicitement `builder.Configuration.GetSection("AtmAgent")`, vérifie que la section existe et bind `AgentConfiguration` depuis cette section uniquement. La résolution autonome d'identité utilise la même section pour éviter tout écart entre pré-résolution et configuration finale injectée dans DI.

### Impact opérationnel
Tous les fichiers de configuration doivent utiliser la section racine suivante:

```json
{
  "AtmAgent": {
    "Atm": {},
    "Transmission": {},
    "Security": {},
    "LogDiscovery": {},
    "Update": {},
    "Monitoring": {},
    "Retention": {}
  }
}
```

Si cette section est absente, l'agent échoue explicitement au démarrage avec un message clair au lieu de continuer dans un état partiel.

## 2. Retry du buffer local SQLite

### Problème constaté
Lorsqu'une entrée de log échouait à la transmission, elle était marquée `Failed`. La méthode de défilement du buffer ne relisait pourtant que les entrées `Pending`. Une coupure SFTP pouvait donc sortir définitivement une ligne du cycle de transmission après un seul échec logique.

### Risque ATM
La promesse "zéro perte" était compromise. Une ligne de journal pouvait rester chiffrée dans SQLite sans jamais être retransmise, ce qui est dangereux pour les rapprochements de transaction, les investigations d'incident et les exigences d'audit.

### Correctif appliqué
Le buffer remet maintenant les entrées échouées en état retryable tant que `retry_count < MaxRetryAttempts`. Après le dernier essai autorisé, l'entrée passe explicitement à `Abandoned`. Les lectures de backlog incluent les anciennes entrées `Failed` encore sous le seuil de retry afin de récupérer les bases déjà existantes.

### Impact opérationnel
Une indisponibilité réseau temporaire n'exclut plus les logs de la file. Le monitoring `PendingEntriesCount` compte les entrées retryables et reflète mieux la charge restante. Une entrée réellement impossible à transmettre est isolée en `Abandoned` après le quota configuré.

## 3. Synchronisation fichier, compression et checksum

### Problème constaté
La synchronisation complète construisait un `FileSyncRecord` avec `Compressed = true` dès que la configuration demandait la compression. Le service SFTP ne compressait cependant que si `CompressBeforeTransmit == true` et `record.Compressed == false`. La compression pouvait donc être contournée. De plus, la vérification d'intégrité utilisait le chemin et le checksum du fichier original même lorsque le payload transmis aurait dû être un `.gz`.

### Risque ATM
Le serveur central pouvait recevoir un fichier différent de celui vérifié, ou ne pas recevoir de compression malgré la configuration. Les rapprochements de checksum côté serveur devenaient ambigus, surtout sur des liens ATM lents.

### Correctif appliqué
`TransmitFileAsync` retourne maintenant un `FileTransmissionResult` contenant:

- `RemotePath`: chemin réellement utilisé côté SFTP, incluant `.gz` si applicable.
- `PayloadChecksum`: checksum SHA-256 du payload réellement transmis.
- `PayloadSizeBytes`: taille du payload transmis.
- `Compressed`: indicateur de compression effective.

Le `SyncWorker` vérifie désormais le checksum sur le chemin réel retourné par la transmission.

### Impact opérationnel
La vérification d'intégrité correspond exactement au fichier reçu par le serveur. En cas de compression activée, le checksum porte sur l'archive `.gz`, ce qui correspond au contenu réellement stocké côté SFTP.

## 4. Validation stricte de la clé serveur SSH

### Problème constaté
Le champ `ValidateServerCertificate` existait dans la configuration mais n'était pas réellement utilisé pour refuser une clé serveur non épinglée. En l'absence de `ServerCertificatePinning`, la connexion pouvait accepter le comportement par défaut de SSH.NET.

### Risque ATM
Un agent ATM pourrait transmettre des journaux à un serveur SFTP usurpé si le réseau est compromis, mal routé ou mal configuré. En contexte bancaire, la validation de la clé serveur SSH doit être explicite.

### Correctif appliqué
Le service SFTP applique désormais les règles suivantes:

- `ValidateServerCertificate = false`: connexion autorisée, mais warning sécurité fort.
- `ValidateServerCertificate = true` et `ServerCertificatePinning` vide: connexion refusée.
- `ValidateServerCertificate = true` et fingerprint renseigné: connexion autorisée uniquement si la clé serveur correspond.

Le fingerprint est normalisé en supprimant `:` et `-`, puis comparé en hexadécimal minuscule.

### Impact opérationnel
Avant production, l'équipe infrastructure doit fournir le fingerprint de la clé hôte SSH du serveur SFTP. La valeur doit être placée dans `Security.ServerCertificatePinning`.

## 5. Durcissement des mises à jour automatiques

### Problème constaté
Le service de mise à jour pouvait appliquer une archive si `UpdatePublicKeyPath` n'était pas configuré. L'extraction ZIP utilisait aussi une extraction directe vers le dossier agent.

### Risque ATM
Une mise à jour non signée ou une archive ZIP malveillante pouvait modifier des fichiers hors du répertoire cible. Sur une flotte ATM, cela représente un risque de compromission à grande échelle.

### Correctif appliqué
`ApplyUpdateAsync` impose maintenant:

- URL de téléchargement en HTTPS.
- `UpdatePublicKeyPath` configuré et lisible.
- signature présente.
- vérification RSA/SHA-256 obligatoire.
- extraction ZIP contrôlée entrée par entrée.
- rejet des chemins sortant du répertoire d'installation.

La configuration exemple désactive `EnableAutoUpdate` tant qu'une clé publique de signature n'est pas provisionnée.

### Impact opérationnel
Pour activer les mises à jour automatiques, il faut déposer la clé publique de l'éditeur sur l'ATM et renseigner `Update.UpdatePublicKeyPath`. Sans cette étape, les updates restent désactivées ou refusées.

## 6. Installateurs Windows et Linux alignés

### Problème constaté
Les installateurs généraient des schémas de configuration différents. Le script Windows documenté utilisait une structure `Sftp`, `Security`, `Paths`, incompatible avec le modèle réel `AtmAgent`. Un autre installateur exigeait encore manuellement `BankName`, `Country`, `City` et `AtmId`, contrairement au principe d'identité automatique.

### Correctif appliqué
Les installateurs génèrent désormais une configuration `AtmAgent` complète. L'identité ATM est initialisée à `AUTO` par défaut. Les clés SSH sont séparées de la clé locale de chiffrement:

- clé SSH: `agent_ed25519`
- clé AES locale: `agent.key`

Les installateurs exigent aussi le fingerprint SFTP en production et désactivent l'auto-update tant que la clé publique d'update n'est pas provisionnée.

### Impact opérationnel
Windows:

```powershell
.\scripts\windows\install.ps1 `
  -SftpHost sftp.example.bank `
  -SftpUser atm-agent `
  -SftpHostKeyFingerprint 0123456789abcdef...
```

Linux:

```bash
export ATM_SFTP_HOST=sftp.example.bank
export ATM_SFTP_USER=atm-agent
export ATM_SFTP_HOSTKEY=0123456789abcdef...
sudo bash src/AtmLogAgent.Installer/install.sh
```

## 7. Répertoire de données cohérent

### Problème constaté
Le buffer SQLite utilisait `ATMAGENT_DATA_DIR`, mais le fichier de positions du watcher utilisait directement `CommonApplicationData`.

### Risque ATM
Sur Linux systemd ou sur un ATM durci, le service pouvait avoir le droit d'écrire le buffer mais pas le fichier de positions. Après redémarrage, l'agent pouvait relire depuis une mauvaise position et générer doublons ou trous de collecte.

### Correctif appliqué
`LogWatcherService` utilise maintenant `ATMAGENT_DATA_DIR` si la variable est définie, comme `LocalBufferService`.

### Impact opérationnel
Le service Linux définit `Environment=ATMAGENT_DATA_DIR=/var/lib/atm-log-agent`. Buffer et positions sont donc regroupés dans le même espace de données autorisé par systemd.

## 8. Build offline et warning NuGet `NU1900`

### Problème constaté
Le projet `Core` traitait tous les warnings comme des erreurs. En environnement sans accès Internet, le SDK .NET peut produire `NU1900` lorsqu'il ne parvient pas à récupérer les données de vulnérabilités NuGet.

### Risque ATM
Un build ou une validation locale dans un réseau bancaire fermé pouvait échouer sans erreur de code. Cela complique la maintenance et les déploiements contrôlés.

### Correctif appliqué
`NU1900` est neutralisé côté projet et l'audit NuGet réseau est désactivé pour le build local. L'audit de vulnérabilités doit être exécuté comme étape CI séparée dans un environnement ayant accès aux sources NuGet approuvées.

### Impact opérationnel
Le projet peut être construit offline avec les packages déjà restaurés. Pour les pipelines connectés, ajouter une étape dédiée:

```bash
dotnet list package --vulnerable
```

## 9. Tests ajoutés

### Couverture ajoutée
Les tests vérifient maintenant:

- la présence de la section `AtmAgent` dans `appsettings.json`;
- l'exigence de pinning SSH dans la configuration exemple;
- la désactivation par défaut des updates automatiques sans clé publique;
- le retour en file retryable après un échec de transmission;
- l'abandon explicite après `MaxRetryAttempts`.

### Validation exécutée
Les validations suivantes ont été exécutées:

```bash
dotnet restore AtmLogAgent.sln --ignore-failed-sources -p:NuGetAudit=false
dotnet build AtmLogAgent.sln --no-restore --verbosity minimal
dotnet test AtmLogAgent.sln --no-restore --verbosity minimal
```

Résultat des tests: 159 tests passés, 0 échec.

## 10. Assistant graphique Windows pour installation ATM

### Problème constaté
Les ATM bancaires sont souvent sous Windows et les paramètres de connexion SFTP varient selon le client, le pays, la banque ou l'environnement de recette/production. Une installation exclusivement par script PowerShell oblige l'administrateur à manipuler des paramètres en ligne de commande, ce qui augmente le risque d'erreur de saisie sur:

- l'hôte SFTP;
- le port SFTP;
- l'utilisateur SFTP;
- l'empreinte de clé hôte;
- les informations banque/pays/ville lorsque le provisioning automatique n'est pas disponible;
- les chemins de logs propres au modèle ATM.

### Correctif appliqué
Un projet WinForms a été ajouté:

```text
src/AtmLogAgent.SetupWizard/
```

Il produit l'exécutable:

```text
AtmLogAgent.SetupWizard.exe
```

L'assistant fournit des champs visuels pour:

- le chemin d'installation;
- le nom du service Windows;
- l'identité banque/pays/ville si elle doit être forcée;
- la configuration SFTP;
- le fingerprint de clé hôte SFTP;
- les URL de supervision et de mise à jour;
- les chemins de logs ATM.

L'identifiant ATM n'est pas saisi par l'administrateur. Il reste à `AUTO` et l'agent le résout au démarrage via le numéro de série matériel, l'adresse MAC ou le hostname. Le répertoire de données est lui aussi défini automatiquement à partir de `CommonApplicationData`, puis exposé au service via `ATMAGENT_DATA_DIR`.

### Actions réalisées par l'assistant
Lorsqu'un administrateur clique sur `Installer`, l'assistant:

1. crée les répertoires `C:\Program Files\AtmLogAgent` et `C:\ProgramData\AtmLogAgent`;
2. copie les binaires du dossier de publication vers le répertoire d'installation;
3. génère `appsettings.json` avec la racine `AtmAgent`;
4. génère `provisioning.conf`;
5. génère une clé SSH `agent_ed25519` si elle n'existe pas;
6. applique des ACL restrictives au répertoire de données;
7. crée ou met à jour le service Windows;
8. écrit `ATMAGENT_DATA_DIR` dans l'environnement du service;
9. démarre le service si l'option est cochée.

### Publication du bundle Windows
Un script de publication a été ajouté:

```powershell
.\scripts\windows\publish-setup.ps1 -SelfContained
```

Il produit un dossier contenant à la fois:

- `AtmLogAgent.Service.exe`;
- `AtmLogAgent.SetupWizard.exe`.

### Impact opérationnel
L'administrateur ATM peut désormais installer le bot avec une interface visuelle, sans modifier manuellement JSON ou scripts PowerShell. Le mode silencieux reste disponible avec `Install-AtmLogAgent.ps1` pour les déploiements de masse.

Documentation détaillée: [Assistant d'installation Windows](Assistant_Installation_Windows.md).
