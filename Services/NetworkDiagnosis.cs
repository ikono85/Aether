using Aether.Models;

namespace Aether.Services;

/// <summary>Verdict lisible : 0 = sain (vert), 1 = à surveiller (orange), 2 = problème (rouge).</summary>
public record Verdict(int Severity, string Title, string Detail);

/// <summary>
/// Corrèle les maillons déjà mesurés pour désigner OÙ se situe un problème. Aucune mesure
/// supplémentaire n'est effectuée : tout repose sur les RTT, pertes et gigues existants.
///
/// La logique suit la chaîne : si le premier saut est déjà mauvais, rien de ce qui suit
/// n'est exploitable — un ISP « lent » derrière une gateway lente ne dit rien sur le FAI.
/// Les règles sont donc ordonnées du plus proche au plus lointain.
/// </summary>
public static class NetworkDiagnosis
{
    /// <summary>Au-delà, un premier saut local (box) est anormalement lent : Wi-Fi faible, CPL saturé.</summary>
    private const double LocalRttWarn = 12;
    private const double LocalRttBad = 30;

    /// <summary>Au-delà, la collecte côté opérateur est en cause.</summary>
    private const double IspRttWarn = 40;
    private const double IspRttBad = 80;

    /// <summary>Gigue au-delà de laquelle une connexion « saccade » même à ping correct.</summary>
    private const double JitterWarn = 12;
    private const double JitterBad = 30;

    public static Verdict Evaluate(NetworkService net)
    {
        var gw = net.Gateway;
        var isp = net.Isp;
        var measurable = net.Nodes.Where(n => n.IsMeasurable).ToList();

        if (measurable.Count == 0)
            return new Verdict(1, "DIAGNOSTIC INDISPONIBLE",
                "Aucun maillon ne répond à l'ICMP. Le pare-feu local ou le réseau filtre les pings : " +
                "les latences ne peuvent pas être mesurées. Le test de débit, lui, reste valable.");

        // --- 1. Le lien local d'abord : il conditionne tout le reste.
        if (gw.IsMeasurable && !gw.Reachable)
            return new Verdict(2, "LIEN LOCAL COUPÉ",
                $"La passerelle {gw.Host} ne répond plus. Le problème est entre ce PC et la box : " +
                "câble, Wi-Fi hors de portée ou box redémarrée. Rien au-delà n'est joignable.");

        if (gw.IsMeasurable && gw.Loss > 5)
            return new Verdict(2, "PERTES SUR LE LIEN LOCAL",
                $"{gw.Loss:0}% de perte jusqu'à la box. Le segment local est en cause (Wi-Fi saturé, " +
                "CPL, câble abîmé) — inutile de chercher plus loin tant que ce chiffre n'est pas à zéro.");

        if (gw.IsMeasurable && gw.Reachable && gw.Rtt > LocalRttBad)
            return new Verdict(2, "LIEN LOCAL LENT",
                $"{gw.Rtt:0} ms pour joindre la box, alors qu'un lien filaire répond en 1 à 2 ms. " +
                "Typique d'un Wi-Fi faible ou d'un canal encombré. Rapprochez-vous ou passez en filaire.");

        // --- 2. Puis l'opérateur, maintenant qu'on sait que le local est sain.
        if (isp.IsMeasurable && isp.Reachable && isp.Rtt > IspRttBad)
            return new Verdict(2, "COLLECTE OPÉRATEUR DÉGRADÉE",
                $"La box répond en {gw.Rtt:0} ms mais le premier routeur du FAI ({isp.Host}) met " +
                $"{isp.Rtt:0} ms. Le problème est côté opérateur, pas chez vous.");

        // --- 3. Enfin le transit : local et collecte sont bons, mais le lointain ne suit pas.
        var far = new[] { net.Cdn, net.Cloud, net.Peer }.Where(n => n.IsMeasurable && n.Reachable).ToList();
        if (far.Count > 0 && gw.IsMeasurable && gw.Reachable && gw.Rtt <= LocalRttWarn)
        {
            double worst = far.Max(n => n.Rtt);
            if (worst > 150)
                return new Verdict(1, "TRANSIT LOINTAIN LENT",
                    $"Le réseau local et la collecte sont sains, mais les destinations publiques " +
                    $"répondent jusqu'à {worst:0} ms. Routage ou peering dégradé : cela concerne " +
                    "certaines destinations, pas toute la connexion.");
        }

        // --- 4. Pertes et gigue : la connexion « marche » mais devient inconfortable.
        double loss = measurable.Average(n => n.Loss);
        if (loss > 5)
            return new Verdict(2, "PERTES DE PAQUETS",
                $"{loss:0.#}% de perte en moyenne sur {measurable.Count} maillon(s). Visioconférence " +
                "et jeu en ligne seront hachés même si le débit reste élevé.");

        var jittered = measurable.Where(n => n.Jitter >= 0).ToList();
        if (jittered.Count > 0)
        {
            var worst = jittered.OrderByDescending(n => n.Jitter).First();
            if (worst.Jitter > JitterBad)
                return new Verdict(2, "CONNEXION INSTABLE",
                    $"Gigue de {worst.Jitter:0.#} ms sur {worst.Label}. La latence moyenne est " +
                    "acceptable mais varie fortement : c'est ce qui fait « lagguer » un jeu ou " +
                    "décrocher une visio, pas le ping affiché.");

            if (worst.Jitter > JitterWarn)
                return new Verdict(1, "LÉGÈRE INSTABILITÉ",
                    $"Gigue de {worst.Jitter:0.#} ms sur {worst.Label}. Sans conséquence en navigation, " +
                    "perceptible en jeu compétitif ou en visioconférence.");
        }

        if (gw.IsMeasurable && gw.Reachable && gw.Rtt > LocalRttWarn)
            return new Verdict(1, "LIEN LOCAL PERFECTIBLE",
                $"{gw.Rtt:0} ms jusqu'à la box : correct, mais un lien filaire descendrait sous 2 ms.");

        if (loss > 0)
            return new Verdict(1, "PERTES MARGINALES",
                $"{loss:0.#}% de perte moyenne. Négligeable en usage courant, à surveiller si cela augmente.");

        return new Verdict(0, "CHAÎNE RÉSEAU SAINE",
            $"Les {measurable.Count} maillons mesurables répondent sans perte" +
            (net.PingMs >= 0 ? $", meilleure latence {net.PingMs:0} ms." : ".") +
            " Aucun point de blocage détecté entre ce PC et l'Internet public.");
    }
}
