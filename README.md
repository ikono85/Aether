# AETHER — PC Operating System

Un centre de contrôle PC nouvelle génération. Interface « Cyber Minimalism » : verre,
halos, lignes lumineuses fines, accent dynamique qui change selon l'état du PC
(🟢 optimal · 🟠 charge élevée · 🔴 problème).

## Lancer

Requiert le SDK .NET 10 (build ciblée `net10.0-windows`, WPF).

AETHER **demande les droits administrateur au démarrage** (`app.manifest` :
`requireAdministrator`) : la lecture des températures CPU et carte mère passe par un pilote
noyau, et les modules d'optimisation et de services écrivent dans HKLM.

Depuis un terminal non élevé, `dotnet run` échoue avec « L'opération demandée nécessite une
élévation ». Compiler puis lancer avec l'invite UAC :

```bash
dotnet build
```

```bash
Start-Process ".\bin\Debug\net10.0-windows\Aether.exe" -Verb RunAs
```

Ou ouvrir le terminal « en tant qu'administrateur », puis `dotnet run`.

### Tests

```bash
dotnet test tests/Aether.Tests/Aether.Tests.csproj
```

Couvrent l'écriture atomique des journaux, la restauration du registre (liste blanche, entrées
forgées rejetées), le journal des services (suffixes de session), le diagnostic réseau, la
table TCP et le lancement des utilitaires système.

### Distribution

```bash
dotnet publish Aether.csproj -c Release -r win-x64 --self-contained true -o publish\win-x64
```

Puis signer `Aether.exe` (Authenticode) et compiler `installer/Aether.iss` avec Inno Setup 6.
L'installation dans `Program Files` est requise pour le démarrage automatique : AETHER refuse
de créer une tâche élevée vers un exécutable modifiable sans droits administrateur.

### Données et journaux

| Emplacement | Contenu |
|---|---|
| `%ProgramData%\Aether` (administrateurs uniquement) | `restore.json`, `service-changes.json`, `dns-backups.json` — relus en administrateur, donc protégés et revalidés |
| `%AppData%\Aether` | `settings.json`, `service-preferences.json` |
| `%LocalAppData%\Aether\logs` | journal quotidien (7 jours), lignes `AUDIT` pour chaque modification système |

Les écritures sont atomiques (copie `.bak`) ; un journal illisible est conservé en `.corrupt-…`
et bloque les modifications au lieu d'être écrasé. Une seule instance d'AETHER peut tourner.

### Compte standard (PC de travail)

Sur un compte sans droits administrateur, l'invite UAC demande les identifiants d'un **autre
compte** : AETHER tourne alors sous ce compte-là, et `%TEMP%`, `%LOCALAPPDATA%` ou `HKCU`
désignent ses dossiers et son registre, pas ceux de la personne connectée.

- **Cleanup Engine** en tient compte : `Services/Optimization/SessionUser.cs` retrouve
  l'utilisateur de la session Windows (WTS → SID → `ProfileList`) et nettoie aussi son
  `%TEMP%` et ses dumps.
- **Limite connue** — Gaming Boost (mode Jeu), Visual FX Off, Game Bar Off (partie
  utilisateur) et Startup Manager écrivent encore dans le `HKCU` du compte élevé : dans ce
  cas, ils n'ont pas d'effet pour l'utilisateur connecté.

Sur un poste géré par une entreprise, vérifier avec le service informatique avant
d'appliquer des modifications système (services, télémétrie, réseau).

### Dépendances

| Paquet | Rôle |
|---|---|
| `CommunityToolkit.Mvvm` | `[ObservableProperty]` et `[RelayCommand]` |
| `LibreHardwareMonitorLib` | températures, charges, ventilateurs, puissances |
| `System.Management` | WMI (`Win32_Service.ChangeStartMode`) |
| `System.ServiceProcess.ServiceController` | état et contrôle des services Windows |

## Architecture (MVVM)
- `Services/HardwareService.cs` — télémétrie matérielle réelle via **LibreHardwareMonitor**
  (températures, charges, mémoire, disques, carte mère, ventilateurs, puissances).
- `Models/HardwareSensor.cs` — un capteur individuel (température, ventilateur ou puissance).
- `Services/NetworkService.cs` — mesures réseau réelles (traceroute, ping, débit d'interface),
  **source unique** des chiffres réseau du Dashboard comme de l'onglet Network.
- `Services/Optimization/` — moteur d'optimisation réel + journal de restauration
  + `SessionUser` (utilisateur réellement connecté).
- `Services/WindowsServices/` — catalogue, gestionnaire (ServiceController + WMI),
  journal des changements et préférences du module Services.
- `ViewModels/` — `MainViewModel` pilote l'accent global + la navigation ; une VM par onglet.
- `Views/` — Dashboard, Network, Optimization, Services, Performance, Settings.
- `Themes/Theme.xaml` — palette carbone/graphite + styles verre.
- `MainWindow.xaml` — chrome custom, **noyau système** animé + navigation.

## Historique, protection et diagnostic

- **Onglet Historique (Ctrl+7)** : liste unifiée de tout ce qu'AETHER a modifié (optimisations,
  services, DNS), restauration élément par élément ou « Tout restaurer », export JSON. Il affiche
  aussi 24 h de mesures réseau : disponibilité, coupures détaillées, latence par tranche de
  5 minutes, export CSV (`%LocalAppData%\Aether\history`, 7 jours conservés).
- **Point de restauration Windows** avant la première modification durable de chaque session
  (réglable). Windows n'en accepte qu'un par 24 h : AETHER compare les points avant et après et
  l'indique au lieu de prétendre l'avoir créé.
- **Startup Manager** : choix des programmes, un par un, avant toute désactivation.
- **Diagnostic** (Paramètres) : « Copier le diagnostic » ou « Exporter un rapport » (ZIP avec
  journaux). Adresses IP publiques, nom du PC et nom d'utilisateur masqués ; rien n'est envoyé.
  Un plantage ou un arrêt forcé est détecté au lancement suivant, qui propose le rapport.
- **Accueil** au premier lancement : ce qu'AETHER fait, ce qu'il envoie (pings, test de débit à
  la demande), et les choix correspondants.
- **Mises à jour** : vérification facultative (désactivée par défaut) des versions publiées sur
  GitHub, une fois par jour. Rien n'est téléchargé ni installé automatiquement : un installeur
  exécuté en administrateur devra d'abord être signé et vérifié.

### Ligne de commande

| Commande | Effet | Code de sortie |
|---|---|---|
| `Aether.exe --restore-all` | Rétablit toutes les modifications (utilisé par le désinstalleur) | 0 succès · 1 échec partiel |
| `Aether.exe --apply <module>` | Applique une optimisation sans interface | 2 argument invalide |
| `Aether.exe --revert <module>` | Annule une optimisation | 3 AETHER déjà ouvert |

`tests/integration/RestoreRoundTrip.ps1` applique puis annule chaque module réversible et
compare le registre et les réglages d'alimentation avant/après — **à lancer dans une VM jetable**.

## Accessibilité et raccourcis

- **Ctrl+1 à Ctrl+6** : Tableau de bord, Réseau, Optimisation, Services, Performance, Paramètres ;
  **Ctrl+7** : Historique.
- Navigation au clavier (Tab) avec un indicateur de focus visible ; interrupteurs de services
  activables à la barre d'espace.
- Noms accessibles sur les boutons à icône (barre de titre, navigation, cartes, services).
- Aucun état n'est signalé par la seule couleur : un libellé l'accompagne (NORMAL / ÉLEVÉ /
  CRITIQUE, ACTIVÉ / ÉCHEC…).
- Les animations continues s'arrêtent avec « Effets visuels » ou si les animations sont
  désactivées dans les paramètres d'accessibilité de Windows.

## Architecture technique

- `CompositionRoot.cs` : injection de dépendances (Microsoft.Extensions.DependencyInjection) ;
  les onglets Réseau, Optimisation et Services sont créés à leur première ouverture.
- `Services/Dialogs/IDialogService.cs` : les ViewModels demandent les confirmations par cette
  interface et n'ouvrent plus de fenêtre eux-mêmes.
- `Themes/Theme.xaml` : palette, teintes, jetons de typographie et de rayons, focus clavier ;
  `Themes/VisualEffects.cs` pilote toutes les ombres et lueurs.

## Onglets
- **Dashboard** — silhouette PC centrale + modules holographiques flottants (CPU/GPU/RAM/SSD/NET) + SYSTEM STATUS.
- **Network** — connexion neuronale (PC au centre, serveurs autour, flux animés) + ping/débit/packet loss.
- **Optimization** — modules d'optimisation **réels**, un bouton « Activer » par carte (voir ci-dessous).
- **Services** — état réel des services Windows, désactivation unitaire ou par profil (voir ci-dessous).
- **Performance** — graphes live CPU/GPU, cartes température CPU/GPU/RAM et panneau
  **CAPTEURS** : toutes les températures, ventilateurs et puissances détectés.
- **Settings** — configuration du cockpit.

## Télémétrie — d'où viennent les chiffres

Aucune valeur affichée n'est simulée. Chaque mesure vient d'un capteur réel, et **une
mesure qu'aucun capteur ne fournit reste absente** : l'interface affiche « — » plutôt
qu'un chiffre de remplacement.

### Matériel — `Services/HardwareService.cs`

`LibreHardwareMonitorLib` est piloté directement par AETHER : il n'est pas nécessaire que
l'application LibreHardwareMonitor tourne en parallèle. Le pilote noyau est déchargé à la
fermeture de la fenêtre.

| Module | Mesure | Capteur |
|---|---|---|
| CPU | température, charge | `Core (Tctl/Tdie)` ou `CPU Package` · `CPU Total` |
| GPU | température, charge | `GPU Core` (repli `GPU Hot Spot`) |
| RAM | température, occupation, Go utilisés/total | sonde des barrettes DIMM · `Total Memory` |
| SSD | température, activité | SMART · `Total Activity` (le plus chaud si plusieurs disques) |
| Réseau | occupation du lien | calculée depuis `NetworkService` (voir plus bas) |

Exemple de relevé réel (AMD Ryzen 5 5500 · GTX 1660 · 2 SSD) :

```
CPU      temp=65   usage=3%    [AMD Ryzen 5 5500]
GPU      temp=58   usage=6%    [NVIDIA GeForce GTX 1660]
RAM      temp=45   usage=56%   [8,8 / 15,9 Go]
SSD      temp=33   usage=1%    [2 disques · le plus chaud]
NETWORK  temp=—    usage=0%    [Ethernet 2 · 1000 Mb/s]
```

### Panneau CAPTEURS (onglet Performance)

En plus des modules ci-dessus, AETHER recopie **chaque capteur** exposé par
LibreHardwareMonitor (carte mère et contrôleurs inclus, sous-composants Super I/O compris),
groupé par composant et rafraîchi chaque seconde :

| Section | Type LHM | Unité | Plage retenue |
|---|---|---|---|
| Températures | `Temperature` | °C | 0 – 125 (couleur à 72 °C et 85 °C) |
| Ventilateurs | `Fan` | tr/min | 0 – 10 000 |
| Consommation | `Power` | W | 0 – 2 000 |

- Un capteur n'apparaît qu'après une **vraie mesure** (> 0) : les entrées non branchées
  (0 tr/min permanent, -55 °C, 127 °C…) ne polluent pas la liste.
- `Distance to TjMax` est écarté : c'est un écart avant la limite thermique, pas une température.
- Une section vide affiche « Aucun capteur détecté sur ce PC ». Exemple : sur un HP ProDesk
  400 G4 (carte mère HP 82A2), la vitesse des ventilateurs est gérée par le contrôleur HP et
  n'est pas lisible ; la consommation remonte `CPU Package`, `CPU Cores`, `CPU Memory` et
  `GPU Power`.

Points d'attention :

- **Les sondes ACPI de la carte mère ne pilotent pas la carte CPU.** Sur beaucoup de PC
  fixes elles renvoient une valeur aberrante (16,9 °C sur la machine de référence) qui ne
  reflète pas le die du processeur ; elles restent visibles dans le panneau CAPTEURS.
- **LibreHardwareMonitor expose plusieurs composants `Memory`** : « Total Memory » (la RAM
  physique), « Virtual Memory » (le fichier d'échange, écarté) et une entrée par barrette
  pour la température. Le code ne retient que la RAM physique pour l'occupation.
- Sans droits administrateur, le pilote noyau ne se charge pas et les températures CPU
  affichent « — ».

### Réseau — `Services/NetworkService.cs`

Une seule source alimente le Dashboard **et** l'onglet Network : débits lus sur les
compteurs de l'interface active, latence et perte mesurées par ping sur 6 maillons
(gateway, DNS, ISP, peer, CDN, cloud) découverts par traceroute TTL croissant.

L'occupation du lien affichée sur le Dashboard est le débit mesuré rapporté à la vitesse
négociée de l'interface (par exemple 15 Mb/s sur un lien gigabit → 1,5 %).

**Mesure de la perte de paquets.** Les sondes sont volontairement lentes et espacées :
2 maillons en parallèle au maximum, 250 ms entre chaque ping, timeout de 2 s. Une rafale
d'ICMP simultanés se perd elle-même et fait mesurer la sonde plutôt que le réseau.

Un maillon qui ne répond pas n'est pas comptabilisé comme de la perte :

| État du maillon | Affiché | Compté dans la perte |
|---|---|:--:|
| Répond | `43 ms` | ✅ |
| Résolu mais silencieux | `ICMP filtré` | ❌ |
| Traceroute incomplet | `non découvert` | ❌ |

Si aucun maillon n'est mesurable, la perte affiche « — » et non « 0 % ».

Relevé de référence au repos :

```
GATEWAY  192.168.1.254                   2 ms   loss=0%
DNS      1.1.1.1                        55 ms   loss=0%
ISP      …intf.routers.proxad.net       35 ms   loss=0%
PEER     15169-3356-par.sp.lumen.tech   35 ms   loss=0%
CDN      1.1.1.1                        38 ms   loss=0%
CLOUD    8.8.8.8                        38 ms   loss=0%
=> PACKET LOSS : 0 % (sur 6 maillons)
```

Sous téléchargement, le RTT peut monter à 130-215 ms alors qu'il est à 35 ms au repos :
c'est du bufferbloat sur la box, pas un défaut de mesure, et AETHER le rapporte tel quel.

## Optimisation — ce qui est réellement exécuté

Chaque module de l'onglet **Optimization** exécute une action système réelle
(`Services/Optimization/`). Avant toute modification, l'état précédent est écrit dans
`%ProgramData%\Aether\restore.json`, y compris après un redémarrage d'AETHER. Chaque module
affiche ce qu'il a réellement fait — ou pourquoi il n'a rien pu faire.

### Utilisation : 1 carte = 1 bouton

Il n'y a pas de sélection ni de bouton global : le bouton de la carte agit immédiatement
sur ce seul module.

| Bouton | Quand | Effet |
|---|---|---|
| ⚡ Activer | module inactif | applique l'optimisation |
| ⏻ Désactiver | module réversible appliqué | restaure l'état sauvegardé |
| ↻ Relancer | action **PONCTUELLE** déjà exécutée | l'exécute à nouveau |
| ⏳ En cours… | pendant l'exécution | — |

- État affiché sur la carte : INACTIF · EN COURS… · **ACTIVÉ** (vert) · DÉSACTIVÉ ·
  SANS EFFET · ÉCHEC (rouge).
- Badges : **ADMIN** (droits administrateur requis) · **PONCTUEL** (Cleanup, Storage,
  Overlay : rien à désactiver).
- Toute action durable demande confirmation (réglage « Confirmer les modifications durables ») ;
  pour Startup Manager, la liste des programmes concernés est affichée.
- Une seule exécution à la fois (les actions partagent le magasin de restauration) : les
  autres boutons sont grisés pendant ce temps.
- **↩ TOUT DÉSACTIVER** annule d'un coup tous les modules appliqués ; **✕ ANNULER**
  interrompt l'exécution en cours.

| Module | Action réelle | Admin | Réversible |
|---|---|:--:|:--:|
| Gaming Boost | Plan d'alimentation « Performances élevées » + mode Jeu | — | ✅ |
| Cleanup Engine | Supprime `%TEMP%`, dumps, cache IE (+ `Windows\Temp`) — y compris ceux de l'utilisateur connecté si AETHER est élevé sous un autre compte ; épargne les fichiers de moins de 24 h, ne suit pas les jonctions | partiel | ❌ |
| Startup Manager | Désactive les programmes tiers au démarrage (`StartupApproved`) | — | ✅ |
| Storage Optimizer | `defrag /O` — TRIM sur SSD, défragmentation sur HDD | ✅ | n/a |
| Visual FX Off | Profil « meilleures performances » + transparence coupée | — | ✅ |
| USB Power Keep | Suspension sélective USB off (AC + DC) + clés `EnhancedPowerManagementEnabled` | partiel | ✅ |
| Game Bar Off | Game Bar + Game DVR désactivés | partiel | ✅ |
| Overlay Scanner | **Détection seule** : recense les overlays actifs et indique où les couper | — | n/a |

Modules retirés : **Memory Compressor** (`EmptyWorkingSet` provoquait ensuite des défauts de
page et dégradait les performances) et **Network Accelerator** (`autotuninglevel=normal` est
déjà la valeur par défaut ; le vidage DNS est dans l'onglet Network). Ce dernier reste affiché
uniquement s'il avait été appliqué, pour pouvoir l'annuler.

Modules déplacés dans l'onglet **Services** : **Service Trimmer** (profil « Services rarement
utiles ») et **Telemetry Block** (profil « Télémétrie Windows »). Deux mécanismes modifiant les
mêmes services avec deux journaux distincts affichaient des états contradictoires ; l'onglet
Services montre l'état réel et les conséquences de chaque changement. Les anciennes cartes
restent affichées si elles avaient été appliquées, le temps de les annuler.

Les modules marqués **Admin** sont ignorés (et signalés comme tels) si AETHER ne tourne
pas en administrateur ; le bandeau orange propose de relancer avec élévation.

## Services Windows

L'onglet **Services** (`Services/WindowsServices/`) lit l'état réel de 58 services système
et permet de les désactiver un par un ou par profil. Rien n'est deviné : le statut et le
type de démarrage viennent de `ServiceController`, et l'écriture passe par WMI
(`Win32_Service.ChangeStartMode`).

### Fonctionnement

- **Lecture** — au chargement, le catalogue est confronté à la machine. Un service absent
  de l'édition installée (Hyper-V sur une édition Famille, par exemple) est simplement
  masqué, sans erreur.
- **Services par utilisateur** — les services à suffixe de session
  (`CDPUserSvc_d1b94`, `BcastDVRUserService_…`) sont résolus à l'exécution. WMI refuse de
  les configurer (code 21) : AETHER écrit alors la valeur `Start` du service **modèle**
  dans le registre, que Windows applique à la prochaine ouverture de session — la ligne
  l'indique explicitement.
- **Confirmation systématique** — toute désactivation ouvre une fenêtre qui décrit
  concrètement ce qui va cesser de fonctionner, avec un badge 🟢 / 🟡 / 🔴 et un bouton
  destructif dès que l'impact dépasse « sans risque ». Une case permet de ne plus
  confirmer les services 🟢 (préférence persistée).
- **Restauration** — chaque changement est journalisé dans
  `%ProgramData%\Aether\service-changes.json` (nom de catalogue, stable d'une session à l'autre) sous la forme
  `{ServiceName, OldStartType, NewStartType, Timestamp}`. Le bouton ↩ d'une ligne et
  « TOUT RESTAURER » remettent le type de démarrage que Windows avait à l'origine — le
  premier `OldStartType` enregistré, pour qu'un aller-retour ne fasse pas perdre l'état
  initial.

### Catégories

Xbox et jeu · Impression · Virtualisation Hyper-V · Capteurs · Bluetooth · Géolocalisation ·
Téléphone et appareils liés · Diagnostics · Télémétrie · Recherche Windows · Partage réseau ·
Bureau à distance · Divers

### Profils prédéfinis

| Profil | Effet |
|---|---|
| PC gaming sans Xbox | Coupe l'écosystème Xbox Live et les captures de jeu, manettes et réseau intacts |
| Sans virtualisation | Coupe les 7 services d'intégration Hyper-V |
| Poste isolé sans réseau | Coupe découverte réseau, partage et accès distant |
| Services rarement utiles | Télécopie, registre à distance, mode démonstration, cartes hors connexion, partage Windows Media, téléphonie |
| Télémétrie Windows | DiagTrack et dmwappushservice |

Un profil affiche la liste complète des conséquences avant d'appliquer quoi que ce soit.

### Niveaux de risque

- 🟢 **Sans risque** — aucun effet tant que la condition d'usage affichée est vraie.
- 🟡 **Impact modéré** — casse une fonctionnalité identifiable (Bluetooth, impression,
  VPN, Windows Hello…).
- 🔴 **Déconseillé** — `sppsvc` (activation Windows), `DPS` (dépannage), `SysMain`,
  `LanmanServer` / `LanmanWorkstation` (accès SMB). Ces services circulent dans les listes
  de « tweaks » alors que les désactiver casse des fonctions essentielles. Ils restent
  activables, mais la confirmation dit exactement ce qui se casse.

### Avertissement affiché dans l'application

Sur un PC moderne équipé d'un SSD, désactiver ces services n'apporte qu'un gain de
performance marginal : l'intérêt réel est de réduire la surface d'attaque et les tâches de
fond inutiles. Pour la vie privée, les paramètres de confidentialité de Windows sont plus
efficaces que la désactivation du service de télémétrie.

Les modifications exigent que AETHER soit lancé en administrateur ; sans élévation, la page
reste en lecture seule et propose de relancer.
