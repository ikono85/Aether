using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.Models;

/// <summary>Une connexion TCP réellement ouverte, telle que la rapporte la pile Windows.</summary>
public partial class ActiveConnection : ObservableObject
{
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public string State { get; init; } = "";

    /// <summary>
    /// Nom inverse (PTR) de l'hôte distant, résolu en arrière-plan. Vide tant que la
    /// résolution n'a pas abouti : beaucoup d'IP n'ont pas de PTR, c'est normal.
    /// </summary>
    [ObservableProperty] private string _remoteHost = "";

    /// <summary>Identité stable d'une connexion, utilisée pour ne pas la recréer à chaque relevé.</summary>
    public string Key => $"{Pid}|{LocalAddress}:{LocalPort}|{RemoteAddress}:{RemotePort}";

    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";
    public string RemoteEndpoint => $"{RemoteAddress}:{RemotePort}";

    /// <summary>Ce qu'on affiche pour l'hôte distant : le PTR s'il existe, l'IP sinon.</summary>
    public string RemoteDisplay => RemoteHost.Length > 0 ? RemoteHost : RemoteAddress;

    /// <summary>Service usuel derrière le port distant, pour lire la table sans la décoder.</summary>
    public string ServiceHint => RemotePort switch
    {
        80 => "HTTP",
        443 => "HTTPS",
        53 => "DNS",
        22 => "SSH",
        25 or 465 or 587 => "SMTP",
        110 or 995 => "POP3",
        143 or 993 => "IMAP",
        445 => "SMB",
        3389 => "RDP",
        3478 or 19302 => "STUN/voix",
        1935 => "RTMP",
        27015 or 27016 or 27017 => "jeu (Source)",
        _ => ""
    };

    partial void OnRemoteHostChanged(string value) => OnPropertyChanged(nameof(RemoteDisplay));
}
