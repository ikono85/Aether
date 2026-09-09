using System.ServiceProcess;
using Aether.Models;

namespace Aether.Services.WindowsServices;

/// <summary>
/// Description des services proposés à la désactivation. Rien n'est lu ni écrit ici :
/// c'est la table de référence (catégorie, risque, condition d'usage, conséquence
/// concrète, type de démarrage d'origine) que <see cref="WindowsServiceManager"/>
/// confronte ensuite à l'état réel de la machine.
/// </summary>
public static class WindowsServiceCatalog
{
    public static IReadOnlyList<WindowsServiceInfo> All { get; } = Build();

    public static string CategoryLabel(WindowsServiceCategory c) => c switch
    {
        WindowsServiceCategory.Xbox => "Xbox et jeu",
        WindowsServiceCategory.Printing => "Impression",
        WindowsServiceCategory.HyperV => "Virtualisation Hyper-V",
        WindowsServiceCategory.Sensors => "Capteurs",
        WindowsServiceCategory.Bluetooth => "Bluetooth",
        WindowsServiceCategory.Geolocation => "Géolocalisation",
        WindowsServiceCategory.Phone => "Téléphone et appareils liés",
        WindowsServiceCategory.Diagnostics => "Diagnostics",
        WindowsServiceCategory.Telemetry => "Télémétrie",
        WindowsServiceCategory.WindowsSearch => "Recherche Windows",
        WindowsServiceCategory.NetworkSharing => "Partage réseau",
        WindowsServiceCategory.RemoteDesktop => "Bureau à distance",
        _ => "Divers"
    };

    public static string CategoryIcon(WindowsServiceCategory c) => c switch
    {
        WindowsServiceCategory.Xbox => "🎮",
        WindowsServiceCategory.Printing => "🖨️",
        WindowsServiceCategory.HyperV => "🖥️",
        WindowsServiceCategory.Sensors => "📡",
        WindowsServiceCategory.Bluetooth => "🔵",
        WindowsServiceCategory.Geolocation => "📍",
        WindowsServiceCategory.Phone => "📱",
        WindowsServiceCategory.Diagnostics => "🩺",
        WindowsServiceCategory.Telemetry => "🛰️",
        WindowsServiceCategory.WindowsSearch => "🔍",
        WindowsServiceCategory.NetworkSharing => "🌐",
        WindowsServiceCategory.RemoteDesktop => "💻",
        _ => "⚙️"
    };

    public static string ImpactLabel(ImpactLevel i) => i switch
    {
        ImpactLevel.Safe => "SANS RISQUE",
        ImpactLevel.Moderate => "IMPACT MODÉRÉ",
        _ => "DÉCONSEILLÉ"
    };

    private static List<WindowsServiceInfo> Build()
    {
        var list = new List<WindowsServiceInfo>();

        void Add(string name, string display, WindowsServiceCategory category, ImpactLevel impact,
                 string condition, string consequence,
                 ServiceStartMode defaultMode = ServiceStartMode.Manual, bool perUser = false)
            => list.Add(new WindowsServiceInfo
            {
                ServiceName = name,
                DisplayName = display,
                Category = category,
                ImpactLevel = impact,
                Condition = condition,
                ConsequenceText = consequence,
                DefaultStartType = defaultMode,
                IsPerUserService = perUser
            });

        // ---------------------------------------------------------------- Xbox
        const string XboxCondition = "Vous ne jouez à aucun jeu Xbox / Microsoft Store et n'utilisez pas de manette Xbox.";

        Add("XblAuthManager", "Gestionnaire d'authentification Xbox Live", WindowsServiceCategory.Xbox, ImpactLevel.Safe,
            XboxCondition,
            "Empêche la connexion au compte Xbox Live : les jeux du Microsoft Store et le Xbox Game Pass ne pourront plus vous authentifier.");

        Add("XblGameSave", "Sauvegarde de jeux Xbox Live", WindowsServiceCategory.Xbox, ImpactLevel.Safe,
            XboxCondition,
            "Désactive la synchronisation des sauvegardes de jeux dans le cloud Xbox. Les sauvegardes locales restent intactes.");

        Add("XboxNetApiSvc", "Service réseau Xbox Live", WindowsServiceCategory.Xbox, ImpactLevel.Safe,
            XboxCondition,
            "Coupe le multijoueur et les fonctions réseau des jeux Xbox Live (invitations, sessions en ligne).");

        Add("XboxGipSvc", "Service pour accessoires de jeu Xbox", WindowsServiceCategory.Xbox, ImpactLevel.Moderate,
            "Vous n'utilisez pas de manette Xbox (filaire, sans fil ou Bluetooth).",
            "Empêche la reconnaissance des manettes Xbox et de leurs accessoires. Les manettes d'autres marques ne sont pas concernées.");

        Add("GameInputSvc", "Service GameInput", WindowsServiceCategory.Xbox, ImpactLevel.Moderate,
            "Vous ne jouez pas avec une manette ou un périphérique de jeu récent.",
            "Coupe la couche d'entrée GameInput : certaines manettes et volants récents peuvent ne plus être détectés par les jeux.");

        Add("BcastDVRUserService", "Diffusion et captures de jeu", WindowsServiceCategory.Xbox, ImpactLevel.Safe,
            "Vous n'enregistrez ni ne diffusez vos parties via la Xbox Game Bar.",
            "Désactive l'enregistrement et la diffusion de jeu de la Xbox Game Bar (Game DVR). Les logiciels tiers comme OBS continuent de fonctionner.",
            perUser: true);

        // ---------------------------------------------------------- Impression
        Add("Spooler", "Spouleur d'impression", WindowsServiceCategory.Printing, ImpactLevel.Moderate,
            "Vous n'imprimez jamais et n'exportez jamais en PDF via une imprimante virtuelle.",
            "Bloque l'impression sur toutes les imprimantes USB, réseau et Wi-Fi, ainsi que Microsoft Print to PDF et Microsoft XPS Document Writer.",
            ServiceStartMode.Automatic);

        Add("Fax", "Télécopie", WindowsServiceCategory.Printing, ImpactLevel.Safe,
            "Vous n'envoyez ni ne recevez de fax depuis ce PC.",
            "Empêche l'envoi et la réception de fax via un modem ou une imprimante multifonction. Sans effet sur l'impression classique.");

        Add("PrintNotify", "Notifications d'impression", WindowsServiceCategory.Printing, ImpactLevel.Safe,
            "Les notifications de bourrage papier ou de fin d'encre ne vous sont pas utiles.",
            "Supprime les notifications et fenêtres d'événements des imprimantes (bourrage, manque de papier). L'impression elle-même continue de fonctionner.");

        // ------------------------------------------------------------- Hyper-V
        const string HyperVConsequence =
            "Casse l'intégration entre Windows et les machines virtuelles Hyper-V, Windows Sandbox, WSL 2 ou Docker Desktop (moteur Hyper-V) : synchronisation de l'heure, arrêt propre, échange de données.";
        const string HyperVCondition =
            "Ce PC n'est pas une machine virtuelle Hyper-V et vous n'utilisez ni WSL 2, ni Docker Desktop, ni Windows Sandbox.";

        Add("vmickvpexchange", "Échange de données Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmicheartbeat", "Pulsation Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmicvss", "Requête de cliché instantané Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmicshutdown", "Arrêt de l'invité Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmicrdv", "Virtualisation Bureau à distance Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmicguestinterface", "Interface de service invité Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);
        Add("vmictimesync", "Synchronisation date/heure Hyper-V", WindowsServiceCategory.HyperV, ImpactLevel.Moderate, HyperVCondition, HyperVConsequence);

        // ------------------------------------------------------------ Capteurs
        Add("SensorDataService", "Service de données de capteur", WindowsServiceCategory.Sensors, ImpactLevel.Safe,
            "PC fixe sans écran tactile, sans capteur de luminosité ni accéléromètre.",
            "Coupe la remontée des données de capteurs : rotation automatique de l'écran, luminosité adaptative et boussole cessent de fonctionner.");

        Add("SensrSvc", "Service de surveillance des capteurs", WindowsServiceCategory.Sensors, ImpactLevel.Safe,
            "PC fixe sans capteur de luminosité ambiante.",
            "Désactive l'adaptation automatique de la luminosité de l'écran à la lumière ambiante.");

        // ----------------------------------------------------------- Bluetooth
        const string BluetoothConsequence =
            "Empêche Windows de détecter, appairer et gérer tout périphérique Bluetooth (casque, souris, clavier, manette, smartphone).";
        const string BluetoothCondition = "Ce PC n'a pas de Bluetooth, ou vous n'utilisez aucun périphérique Bluetooth.";

        Add("bthserv", "Service de prise en charge Bluetooth", WindowsServiceCategory.Bluetooth, ImpactLevel.Moderate,
            BluetoothCondition, BluetoothConsequence);

        Add("BthAvctpSvc", "Service AVCTP Bluetooth", WindowsServiceCategory.Bluetooth, ImpactLevel.Moderate,
            BluetoothCondition,
            BluetoothConsequence + " Les commandes de lecture (play/pause, volume) des casques et enceintes cessent en particulier de répondre.");

        Add("BluetoothUserService", "Service Bluetooth utilisateur", WindowsServiceCategory.Bluetooth, ImpactLevel.Moderate,
            BluetoothCondition, BluetoothConsequence, perUser: true);

        // ----------------------------------------------------- Géolocalisation
        Add("lfsvc", "Service de géolocalisation", WindowsServiceCategory.Geolocation, ImpactLevel.Moderate,
            "Aucune application n'a besoin de connaître votre position.",
            "Coupe l'accès à la position de l'appareil pour Cartes, Météo, Localiser mon appareil et les applications de navigation.");

        // ------------------------------------------------------------ Téléphone
        Add("PhoneSvc", "Service de téléphonie", WindowsServiceCategory.Phone, ImpactLevel.Safe,
            "Vous ne passez pas d'appels depuis ce PC et n'utilisez pas Mobile connecté.",
            "Désactive la gestion des appels téléphoniques depuis Windows (application Téléphone, appels relayés depuis un smartphone).");

        Add("CDPSvc", "Plateforme d'appareils connectés", WindowsServiceCategory.Phone, ImpactLevel.Moderate,
            "Vous n'utilisez ni Mobile connecté, ni le Presse-papiers cloud, ni la reprise d'activité entre appareils.",
            "Coupe la liaison entre ce PC et vos autres appareils Microsoft : Mobile connecté, partage de proximité, presse-papiers synchronisé et reprise d'activité.",
            ServiceStartMode.Automatic);

        Add("CDPUserSvc", "Plateforme d'appareils connectés (utilisateur)", WindowsServiceCategory.Phone, ImpactLevel.Moderate,
            "Vous n'utilisez ni Mobile connecté, ni le partage de proximité.",
            "Même effet que la plateforme d'appareils connectés, appliqué à votre session : partage de proximité et presse-papiers synchronisé cessent de fonctionner.",
            ServiceStartMode.Automatic, perUser: true);

        // ---------------------------------------------------------- Diagnostics
        Add("DPS", "Service de stratégie de diagnostic", WindowsServiceCategory.Diagnostics, ImpactLevel.NotRecommended,
            "Vous n'utilisez jamais les utilitaires de résolution de problèmes de Windows.",
            "Peut empêcher le fonctionnement correct des outils de résolution des problèmes de Windows. Désactivation déconseillée.",
            ServiceStartMode.Automatic);

        Add("WdiServiceHost", "Hôte du service de diagnostic", WindowsServiceCategory.Diagnostics, ImpactLevel.Moderate,
            "Vous n'utilisez pas les diagnostics réseau ou audio automatiques de Windows.",
            "Les assistants de dépannage (réseau, son, alimentation) ne pourront plus analyser ni corriger les problèmes automatiquement.");

        Add("WdiSystemHost", "Hôte du système de diagnostic", WindowsServiceCategory.Diagnostics, ImpactLevel.Moderate,
            "Vous n'utilisez pas les diagnostics système automatiques de Windows.",
            "Empêche les diagnostics système internes de s'exécuter : certains dépannages Windows échoueront sans explication.");

        // ----------------------------------------------------------- Télémétrie
        Add("DiagTrack", "Expériences des utilisateurs connectés et télémétrie", WindowsServiceCategory.Telemetry, ImpactLevel.Safe,
            "Vous ne souhaitez pas envoyer de données d'utilisation à Microsoft.",
            "Réduit la collecte de données de diagnostic envoyées à Microsoft, mais ne l'élimine pas totalement.",
            ServiceStartMode.Automatic);

        Add("dmwappushservice", "Service de routage de messages WAP Push", WindowsServiceCategory.Telemetry, ImpactLevel.Safe,
            "Ce PC n'est pas géré par une entreprise via une solution de gestion d'appareils (MDM).",
            "Coupe le routage des messages de gestion à distance. Sans effet sur un PC personnel ; peut casser l'administration d'un poste d'entreprise.");

        // ------------------------------------------------------ Recherche Windows
        Add("WSearch", "Windows Search", WindowsServiceCategory.WindowsSearch, ImpactLevel.Moderate,
            "Vous cherchez rarement des fichiers, ou vous utilisez un outil de recherche tiers (Everything…).",
            "Rend les recherches du menu Démarrer et de l'Explorateur non indexées et donc plus lentes ; Outlook peut aussi ralentir ses recherches.",
            ServiceStartMode.Automatic);

        // -------------------------------------------------------- Partage réseau
        Add("FDResPub", "Publication des ressources de découverte de fonctions", WindowsServiceCategory.NetworkSharing, ImpactLevel.Safe,
            "Ce PC n'a pas besoin d'être visible par les autres machines du réseau local.",
            "Rend ce PC invisible dans le voisinage réseau des autres ordinateurs. Vos propres accès aux partages distants continuent de fonctionner.");

        Add("SSDPSRV", "Découverte SSDP", WindowsServiceCategory.NetworkSharing, ImpactLevel.Safe,
            "Vous n'utilisez ni DLNA, ni Chromecast, ni appareil UPnP sur votre réseau.",
            "Empêche la détection automatique des appareils UPnP du réseau : téléviseurs DLNA, enceintes connectées et box multimédia n'apparaîtront plus.");

        Add("upnphost", "Hôte de périphérique UPnP", WindowsServiceCategory.NetworkSharing, ImpactLevel.Safe,
            "Ce PC n'a pas besoin de se présenter comme périphérique UPnP au réseau.",
            "Ce PC cesse de se publier comme appareil UPnP : le partage de médias vers un téléviseur ou une console ne fonctionnera plus.");

        Add("lmhosts", "Assistance NetBIOS sur TCP/IP", WindowsServiceCategory.NetworkSharing, ImpactLevel.Safe,
            "Votre réseau local n'utilise pas de matériel ou de partages anciens reposant sur NetBIOS.",
            "Coupe la résolution de noms NetBIOS. Certains NAS anciens et partages Windows historiques peuvent devenir inaccessibles par leur nom.");

        Add("LanmanServer", "Serveur (partage de fichiers)", WindowsServiceCategory.NetworkSharing, ImpactLevel.NotRecommended,
            "Ce PC ne partage aucun dossier ni imprimante avec d'autres machines.",
            "Bloque l'accès aux partages réseau SMB, aux NAS et aux imprimantes réseau.",
            ServiceStartMode.Automatic);

        Add("LanmanWorkstation", "Station de travail (client SMB)", WindowsServiceCategory.NetworkSharing, ImpactLevel.NotRecommended,
            "Ce PC n'accède à aucun partage réseau, NAS ou lecteur mappé.",
            "Bloque l'accès aux partages réseau SMB, aux NAS et aux imprimantes réseau. Les lecteurs réseau mappés disparaîtront.",
            ServiceStartMode.Automatic);

        // ----------------------------------------------------- Bureau à distance
        Add("TermService", "Services Bureau à distance", WindowsServiceCategory.RemoteDesktop, ImpactLevel.Moderate,
            "Personne ne se connecte à ce PC en Bureau à distance.",
            "Empêche toute connexion Bureau à distance (RDP) entrante vers ce PC. Vos connexions sortantes vers d'autres machines restent possibles.");

        Add("SessionEnv", "Configuration des services Bureau à distance", WindowsServiceCategory.RemoteDesktop, ImpactLevel.Safe,
            "Personne ne se connecte à ce PC en Bureau à distance.",
            "Empêche la configuration des sessions Bureau à distance entrantes. Sans effet si vous n'hébergez pas de session RDP.");

        Add("UmRdpService", "Redirecteur de port en mode utilisateur (RDP)", WindowsServiceCategory.RemoteDesktop, ImpactLevel.Safe,
            "Personne ne se connecte à ce PC en Bureau à distance.",
            "Coupe la redirection des imprimantes, disques et presse-papiers dans les sessions Bureau à distance entrantes.");

        Add("RasMan", "Gestionnaire de connexions d'accès à distance", WindowsServiceCategory.RemoteDesktop, ImpactLevel.Moderate,
            "Vous n'utilisez aucun VPN et aucune connexion par modem ou 4G.",
            "Casse toutes les connexions VPN (y compris les VPN tiers) ainsi que les connexions par modem, PPPoE et carte 4G/5G.");

        Add("RasAuto", "Gestionnaire de connexions automatiques d'accès à distance", WindowsServiceCategory.RemoteDesktop, ImpactLevel.Safe,
            "Vous n'avez pas besoin que Windows relance seul une connexion VPN ou distante.",
            "Windows ne rétablira plus automatiquement une connexion distante quand une application en a besoin. La connexion manuelle reste possible.");

        // ---------------------------------------------------------------- Divers
        Add("stisvc", "Acquisition d'images Windows (WIA)", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous n'utilisez ni scanner, ni appareil photo relié en USB.",
            "Empêche Windows de piloter les scanners et appareils photo : la numérisation depuis un logiciel tiers cessera de fonctionner.");

        Add("SCardSvr", "Carte à puce", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous n'utilisez ni carte à puce, ni lecteur de carte d'identité ou de santé.",
            "Coupe la prise en charge des cartes à puce : lecteurs de carte d'identité, carte Vitale et authentification par carte professionnelle cessent de fonctionner.");

        Add("wmiApSrv", "Adaptateur de performance WMI", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Aucun outil de supervision tiers ne lit les compteurs de performance de ce PC.",
            "Les compteurs de performance ne seront plus exposés aux outils de supervision tiers. Le Gestionnaire des tâches n'est pas affecté.");

        Add("WpcMonSvc", "Contrôle parental", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Aucun compte enfant ni limite de temps d'écran n'est configuré sur ce PC.",
            "Désactive le contrôle parental Microsoft Family : limites de temps d'écran, filtrage de contenu et rapports d'activité cessent de s'appliquer.");

        Add("PimIndexMaintenanceSvc", "Indexation des données de contact", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous n'utilisez ni l'application Courrier, ni Calendrier, ni Contacts de Windows.",
            "Les contacts et rendez-vous ne seront plus indexés : la recherche dans Courrier, Calendrier et Contacts deviendra incomplète.",
            perUser: true);

        Add("RemoteRegistry", "Registre à distance", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Aucun administrateur ne modifie le registre de ce PC depuis le réseau.",
            "Empêche toute modification du registre de ce PC depuis une autre machine. Recommandé pour la sécurité ; sans effet sur un usage personnel.",
            ServiceStartMode.Disabled);

        Add("WbioSrvc", "Service biométrique Windows", WindowsServiceCategory.Miscellaneous, ImpactLevel.Moderate,
            "Vous n'utilisez ni lecteur d'empreinte, ni reconnaissance faciale.",
            "Désactive Windows Hello par empreinte digitale et par reconnaissance faciale : il faudra vous connecter par mot de passe ou code PIN.");

        Add("MapsBroker", "Gestionnaire de cartes téléchargées", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous n'utilisez pas les cartes hors connexion de l'application Cartes.",
            "Coupe l'accès aux cartes hors connexion pour les applications qui les utilisent. Les cartes en ligne dans un navigateur ne sont pas concernées.",
            ServiceStartMode.Automatic);

        Add("SharedAccess", "Partage de connexion Internet (ICS)", WindowsServiceCategory.Miscellaneous, ImpactLevel.Moderate,
            "Vous ne partagez pas la connexion de ce PC et n'utilisez pas le point d'accès mobile.",
            "Désactive le point d'accès mobile Wi-Fi et le partage de connexion Internet. Certains VPN et outils de virtualisation s'appuient aussi sur ce service.");

        Add("SharedRealitySvc", "Service de réalité mixte partagée", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous n'utilisez pas de casque Windows Mixed Reality.",
            "Coupe la prise en charge de la réalité mixte partagée. Sans aucun effet si vous n'avez pas de casque Windows Mixed Reality.");

        Add("sppsvc", "Protection logicielle (activation Windows)", WindowsServiceCategory.Miscellaneous, ImpactLevel.NotRecommended,
            "Aucune : ce service gère l'activation de Windows et d'Office.",
            "Casse la validation de la licence Windows et Office : le système peut se déclarer non activé et désactiver la personnalisation. Désactivation fortement déconseillée.",
            ServiceStartMode.Automatic);

        Add("WMPNetworkSvc", "Partage réseau du Lecteur Windows Media", WindowsServiceCategory.Miscellaneous, ImpactLevel.Safe,
            "Vous ne diffusez pas votre bibliothèque multimédia vers d'autres appareils du réseau.",
            "Le partage de la bibliothèque Windows Media vers les téléviseurs et consoles du réseau cesse de fonctionner. La lecture locale n'est pas affectée.");

        Add("SysMain", "SysMain (SuperFetch)", WindowsServiceCategory.Miscellaneous, ImpactLevel.NotRecommended,
            "Activité disque anormalement élevée sur un ancien disque dur mécanique.",
            "À désactiver uniquement en cas de problème (activité disque élevée, ancien disque dur HDD). Sur un PC avec SSD, il est recommandé de le laisser activé.",
            ServiceStartMode.Automatic);

        return list;
    }
}
