# AETHER — PC Operating System

Un centre de contrôle PC nouvelle génération. Interface « Cyber Minimalism » : verre,
halos, lignes lumineuses fines, accent dynamique qui change selon l'état du PC
(🟢 optimal · 🟠 charge élevée · 🔴 problème · 🟣 analyse IA).

## Lancer
```bash
dotnet run
```
Requiert le SDK .NET 8+ (build ciblée `net8.0-windows`, WPF).

**Lancer en administrateur** pour disposer de toutes les mesures et actions : la lecture
des températures CPU passe par un pilote noyau, et les modules d'optimisation et de
services écrivent dans HKLM. Sans élévation, AETHER fonctionne mais affiche « — » sur les
capteurs inaccessibles et signale les actions qu'il ne peut pas appliquer — il n'invente
jamais de valeur de remplacement.

### Dépendances

| Paquet | Rôle |
|---|---|
| `CommunityToolkit.Mvvm` | `[ObservableProperty]` et `[RelayCommand]` |
| `LibreHardwareMonitorLib` | températures et charges matérielles |
| `System.Management` | WMI (`Win32_Service.ChangeStartMode`) |
| `System.ServiceProcess.ServiceController` | état et contrôle des services Windows |

## Architecture (MVVM)
- `Services/HardwareService.cs` — télémétrie matérielle réelle via **LibreHardwareMonitor**
  (températures, charges, mémoire, disques).
- `Services/NetworkService.cs` — mesures réseau réelles (traceroute, ping, débit d'interface),
  **source unique** des chiffres réseau du Dashboard comme de l'onglet Network.
- `Services/SecurityScanner.cs` — analyse en lecture seule des processus et du démarrage.
- `Services/Optimization/` — moteur d'optimisation réel + journal de restauration.
- `Services/WindowsServices/` — catalogue, gestionnaire (ServiceController + WMI),
  journal des changements et préférences du module Services.
- `ViewModels/` — `MainViewModel` pilote l'accent global + la navigation ; une VM par onglet.
- `Views/` — Dashboard, Network, Security, Optimization, Services, Performance, Settings.
- `Themes/Theme.xaml` — palette carbone/graphite + styles verre.
- `MainWindow.xaml` — chrome custom, **noyau système** animé + navigation.

## Onglets
- **Dashboard** — silhouette PC centrale + modules holographiques flottants (CPU/GPU/RAM/SSD/NET) + SYSTEM STATUS.
- **Network** — connexion neuronale (PC au centre, serveurs autour, flux animés) + ping/débit/packet loss.
- **Security** — scanner circulaire animé + carte de menace (Safe/Attention/Threat).
- **Optimization** — modules d'optimisation **réels** (voir ci-dessous).
- **Services** — état réel des services Windows, désactivation unitaire ou par profil (voir ci-dessous).
- **Performance** — graphes live CPU/GPU + monitoring température vivant.
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

Points d'attention :

- **Les sondes ACPI de la carte mère sont ignorées.** Sur beaucoup de PC fixes elles
  renvoient une valeur aberrante (16,9 °C sur la machine de référence) qui ne reflète pas
  le die du processeur.
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
`%AppData%\Aether\restore.json` : le bouton **RESTAURER** revient en arrière, y compris
après un redémarrage d'AETHER. Chaque module affiche ce qu'il a réellement fait — ou
pourquoi il n'a rien pu faire.

| Module | Action réelle | Admin | Réversible |
|---|---|:--:|:--:|
| Gaming Boost | Plan d'alimentation « Performances élevées » + mode Jeu | — | ✅ |
| Cleanup Engine | Supprime `%TEMP%`, dumps, cache IE (+ `Windows\Temp`) — épargne les fichiers de moins de 24 h | partiel | ❌ |
| Startup Manager | Désactive les programmes tiers au démarrage (`StartupApproved`) | — | ✅ |
| Network Accelerator | Vide le cache DNS + auto-tuning TCP sur `normal` | partiel | ✅ |
| Storage Optimizer | `defrag /O` — TRIM sur SSD, défragmentation sur HDD | ✅ | n/a |
| Memory Compressor | `EmptyWorkingSet` sur les processus accessibles | — | n/a |
| Visual FX Off | Profil « meilleures performances » + transparence coupée | — | ✅ |
| USB Power Keep | Suspension sélective USB off (AC + DC) + clés `EnhancedPowerManagementEnabled` | partiel | ✅ |
| Game Bar Off | Game Bar + Game DVR désactivés | partiel | ✅ |
| Overlay Scanner | **Détection seule** : recense les overlays actifs et indique où les couper | — | n/a |
| Service Trimmer | 6 services non essentiels en démarrage manuel | ✅ | ✅ |
| Telemetry Block | DiagTrack, dmwappushservice, tâches CEIP, `AllowTelemetry=0` | ✅ | ✅ |

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
  `%AppData%\Aether\service-changes.json` sous la forme
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
