using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ChestTracker;

/// <summary>
/// The Save the World mission alerts that pay V-Bucks, and nothing else.
///
/// Epic has no open door for this. Its own endpoint, /fortnite/api/game/v2/world/info, answers
/// 401 to anybody without an account token, and getting one means logging a real Epic account in
/// and keeping its device secret on disk. fortnite-api.com, where the map and the shop come from,
/// has no Save the World at all. So this reads the page the Save the World players themselves
/// read, freethevbucks.com, which prints every alert on the server side, V-Bucks included.
///
/// That page is somebody else's work and it is read as a guest: at most one request every twenty
/// minutes, only when the reader opens the panel, never on startup, and nothing of the reader's
/// goes in it. The page carries the real expiry of the alerts in its own script, so the countdown
/// is theirs and not a guess about midnight UTC.
///
/// Being a page and not an API, it can change shape. When it does, the parse finds no missions at
/// all and says so (Failed), instead of claiming that today pays nothing.
/// </summary>
public sealed class Vbucks
{
    private const string STR_URL = "https://freethevbucks.com/timed-missions";

    /// <summary>The page itself tells browsers to keep it an hour; twenty minutes is plenty.</summary>
    private static readonly TimeSpan tsFresh = TimeSpan.FromMinutes(20);

    /// <summary>After a failed try, the next one waits: no hammering somebody else's server.</summary>
    private static readonly TimeSpan tsRetry = TimeSpan.FromMinutes(2);

    // The alerts are drawn one zone at a time, each under its name in capitals
    private static readonly Regex reZone = new("<p class=\"jrfont\"[^>]*>([^<]+)</p>", RegexOptions.Compiled);
    private static readonly Regex reExpiry = new("expirationDates\\s*=\\s*\\[([^\\]]*)\\]", RegexOptions.Compiled);
    private static readonly Regex reDate = new("\"([^\"]+)\"", RegexOptions.Compiled);
    // Inside a zone, one mission per paragraph: its icon, then its power level in bold
    private static readonly Regex reTitle = new("title=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex reAlt = new("alt=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex rePower = new("<b>(\\d+)</b>", RegexOptions.Compiled);
    private static readonly Regex rePay = new("V-Bucks</b>\\s*\\(x(\\d+)\\)", RegexOptions.Compiled);

    private readonly Store _objStore;
    private readonly SemaphoreSlim _objOnce = new(1, 1);
    private VbucksView? _objCached;
    private DateTime _dtTried;

    public Vbucks(Store objStore) => _objStore = objStore;

    /// <summary>
    /// True while the copy in hand is still both recent and about the alerts that are running.
    /// The alerts change at their own expiry, so a copy older than that is yesterday's.
    /// </summary>
    private static bool IsCurrent(VbucksView? objView) =>
        objView is { Failed: false }
        && DateTime.UtcNow < objView.FetchedUtc + tsFresh
        && (objView.ExpiresUtc is not { } dtExpiry || DateTime.UtcNow < dtExpiry);

    /// <summary>
    /// What the panel asks for. A recent copy is answered straight away; otherwise the page is
    /// read again. Only one read runs at a time, and whoever waited for it gets what it brought.
    /// </summary>
    public async Task<VbucksView> DescribeAsync(CancellationToken objToken = default)
    {
        if (IsCurrent(_objCached))
        {
            return _objCached!;
        }

        await _objOnce.WaitAsync(objToken);
        try
        {
            // Somebody else may have brought it while this call waited at the door
            if (IsCurrent(_objCached))
            {
                return _objCached!;
            }
            if (DateTime.UtcNow < _dtTried + tsRetry)
            {
                return Stale();
            }
            return await FetchAsync(objToken);
        }
        finally
        {
            _objOnce.Release();
        }
    }

    /// <summary>What there is to show when the page cannot be read right now.</summary>
    private VbucksView Stale()
    {
        if (_objCached == null)
        {
            return new VbucksView { Failed = true, FetchedUtc = DateTime.UtcNow };
        }
        _objCached.Stale = true;
        return _objCached;
    }

    private async Task<VbucksView> FetchAsync(CancellationToken objToken)
    {
        _dtTried = DateTime.UtcNow;
        try
        {
            using var objClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            objClient.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteChestCounter");

            var strBody = await objClient.GetStringAsync(STR_URL, objToken);
            var objView = Parse(strBody);
            if (objView.Failed)
            {
                _objStore.Log("La página de misiones de Salvar el Mundo se leyó pero no se reconoció ninguna misión");
                return Stale();
            }

            _objCached = objView;
            _objStore.Log($"Alertas de Salvar el Mundo: {objView.Missions.Count} con pavos, {objView.Total} en total, de {objView.Seen} misiones");
            return objView;
        }
        catch (Exception objError)
        {
            _objStore.Log($"No se pudieron traer las alertas de Salvar el Mundo: {objError.Message.ReplaceLineEndings(" ")}");
            return Stale();
        }
    }

    /// <summary>
    /// Everything the panel draws: how many V-Bucks are up today, until when, and which mission
    /// pays each one.
    /// </summary>
    private static VbucksView Parse(string strBody)
    {
        var objView = new VbucksView { FetchedUtc = DateTime.UtcNow };

        var intStart = strBody.IndexOf("id=\"missions\"", StringComparison.Ordinal);
        var strZones = intStart < 0 ? strBody : strBody[intStart..];

        objView.ExpiresUtc = ReadExpiry(strZones);

        // Split on the zone headings: odd parts are the names, even parts what is under them
        var arrParts = reZone.Split(strZones);
        for (var intPart = 1; intPart + 1 < arrParts.Length; intPart += 2)
        {
            var strZone = Title(WebUtility.HtmlDecode(arrParts[intPart]).Trim());
            foreach (var strChunk in arrParts[intPart + 1].Split("<p><img src=").Skip(1))
            {
                Add(objView, strZone, strChunk);
            }
        }

        // A page that reads as no missions at all is a page that changed, not a quiet day
        objView.Failed = objView.Seen == 0;
        objView.Total = objView.Missions.Sum(objMission => objMission.Vbucks);
        objView.Missions = objView.Missions
            .OrderByDescending(objMission => objMission.Vbucks)
            .ThenBy(objMission => objMission.Power)
            .ToList();
        return objView;
    }

    /// <summary>
    /// One paragraph of a zone. Every alert counts towards what was seen, which is how a changed
    /// page tells itself apart from a day without V-Bucks; only the ones that pay are kept.
    /// </summary>
    private static void Add(VbucksView objView, string strZone, string strChunk)
    {
        var objTitle = reTitle.Match(strChunk);
        var objPower = rePower.Match(strChunk);
        if (!objTitle.Success || !objPower.Success)
        {
            return;
        }
        objView.Seen++;

        var objPay = rePay.Match(strChunk);
        if (!objPay.Success)
        {
            return;
        }

        // The title is "Mission - Where", and the icon's alt is the mission on its own
        var strTitle = WebUtility.HtmlDecode(objTitle.Groups[1].Value).Trim();
        var intDash = strTitle.IndexOf(" - ", StringComparison.Ordinal);
        var strAlt = reAlt.Match(strChunk) is { Success: true } objAlt
            ? WebUtility.HtmlDecode(objAlt.Groups[1].Value).Trim()
            : "";

        objView.Missions.Add(new VbucksMission
        {
            Vbucks = int.Parse(objPay.Groups[1].Value, CultureInfo.InvariantCulture),
            Power = int.Parse(objPower.Groups[1].Value, CultureInfo.InvariantCulture),
            Mission = strAlt.Length > 0 ? strAlt : intDash > 0 ? strTitle[..intDash] : strTitle,
            Area = intDash > 0 ? strTitle[(intDash + 3)..] : "",
            Zone = strZone
        });
    }

    /// <summary>
    /// When the alerts run out, which the page itself knows: it keeps one date per zone for its
    /// own countdown. The soonest one still ahead is the one the panel counts down to.
    /// </summary>
    private static DateTime? ReadExpiry(string strBody)
    {
        var objList = reExpiry.Match(strBody);
        if (!objList.Success)
        {
            return null;
        }
        DateTime? dtSoonest = null;
        foreach (Match objDate in reDate.Matches(objList.Groups[1].Value))
        {
            if (!DateTimeOffset.TryParse(objDate.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var dtOffset))
            {
                continue;
            }
            var dtUtc = dtOffset.UtcDateTime;
            if (dtUtc > DateTime.UtcNow && (dtSoonest is not { } dtBest || dtUtc < dtBest))
            {
                dtSoonest = dtUtc;
            }
        }
        return dtSoonest;
    }

    /// <summary>The zone names are printed in capitals; the panel wants them read, not shouted.</summary>
    private static string Title(string strText) =>
        string.Join(" ", strText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(strWord => strWord.Length < 2
                ? strWord.ToUpperInvariant()
                : char.ToUpperInvariant(strWord[0]) + strWord[1..].ToLowerInvariant()));
}
