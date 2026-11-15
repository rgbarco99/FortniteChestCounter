using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// The item shop: what is on sale right now, what came in today and what leaves at the next
/// reset.
///
/// None of this is in a replay, so it comes from fortnite-api.com, the same public service the
/// season's map comes from, and it is kept in the data folder. The shop rolls over at midnight
/// UTC every day, so a copy is good until that moment and not a second longer.
///
/// Nothing of the reader's travels in that request: it asks for the shop in one of three
/// languages and no more. Without a connection the tab shows the last shop it managed to bring,
/// and says it is old.
///
/// The answer is cut down to what the page draws before it is saved: the whole thing is about
/// three quarters of a megabyte of fields nobody here reads.
/// </summary>
public sealed class Shop
{
    private const string STR_URL = "https://fortnite-api.com/v2/shop?language=";

    /// <summary>The three the page speaks. Anything else would be pasted into a URL unchecked.</summary>
    private static readonly string[] arrLanguages = { "es", "en", "fr" };

    private readonly Store _objStore;
    private readonly string _strFolder;
    private readonly SemaphoreSlim _objOnce = new(1, 1);

    public Shop(Store objStore)
    {
        _objStore = objStore;
        _strFolder = Path.Combine(objStore.DataFolder, "tienda");
    }

    /// <summary>Midnight UTC, which is when Fortnite changes the shop.</summary>
    public static DateTime NextReset(DateTime dtUtc) => dtUtc.Date.AddDays(1);

    private static string Clean(string? strLanguage) =>
        arrLanguages.FirstOrDefault(strItem => string.Equals(strItem, strLanguage, StringComparison.OrdinalIgnoreCase)) ?? "es";

    private string FileFor(string strLanguage) => Path.Combine(_strFolder, $"tienda-{strLanguage}.json");

    private ShopView? ReadCache(string strLanguage)
    {
        try
        {
            var strPath = FileFor(strLanguage);
            return File.Exists(strPath) ? JsonSerializer.Deserialize<ShopView>(File.ReadAllText(strPath)) : null;
        }
        catch (Exception objError)
        {
            _objStore.Log($"No se pudo leer la tienda guardada: {objError.Message}");
            return null;
        }
    }

    /// <summary>True while the saved copy is still the shop that is on right now.</summary>
    private static bool IsCurrent(ShopView? objView) =>
        objView != null && objView.Entries.Count > 0 && DateTime.UtcNow < objView.NextResetUtc;

    /// <summary>
    /// The shop the page asks for. A copy from this same shop is answered straight away; otherwise
    /// it is fetched, cut down and saved. Only one fetch runs at a time, and whoever waited for it
    /// gets what it brought.
    /// </summary>
    public async Task<ShopView> DescribeAsync(string? strLanguage, CancellationToken objToken = default)
    {
        var strLang = Clean(strLanguage);
        var objCached = ReadCache(strLang);
        if (IsCurrent(objCached))
        {
            return objCached!;
        }

        await _objOnce.WaitAsync(objToken);
        try
        {
            // Somebody else may have brought it while this call waited at the door
            objCached = ReadCache(strLang) ?? objCached;
            if (IsCurrent(objCached))
            {
                return objCached!;
            }
            return await FetchAsync(strLang, objCached, objToken);
        }
        finally
        {
            _objOnce.Release();
        }
    }

    /// <summary>
    /// Brings the shop again for every language already saved, so that opening the tab after a
    /// reset shows today's shop instead of yesterday's while it loads. A reader who has never
    /// opened the tab has no file here, and then this asks for nothing at all.
    /// </summary>
    public async Task WarmAsync(CancellationToken objToken = default)
    {
        foreach (var strLanguage in arrLanguages)
        {
            if (objToken.IsCancellationRequested || !File.Exists(FileFor(strLanguage)))
            {
                continue;
            }
            try
            {
                await DescribeAsync(strLanguage, objToken);
            }
            catch (Exception)
            {
                // The tab will try again by itself when the reader opens it
            }
        }
    }

    private async Task<ShopView> FetchAsync(string strLanguage, ShopView? objOld, CancellationToken objToken)
    {
        try
        {
            using var objClient = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            objClient.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteChestCounter");

            var strBody = await objClient.GetStringAsync(STR_URL + strLanguage, objToken);
            var objView = Parse(strBody, strLanguage);

            Directory.CreateDirectory(_strFolder);
            await File.WriteAllTextAsync(FileFor(strLanguage), JsonSerializer.Serialize(objView), objToken);
            _objStore.Log($"Tienda del {objView.DateUtc:yyyy-MM-dd} traída en {strLanguage}: {objView.Entries.Count} artículos");
            return objView;
        }
        catch (Exception objError)
        {
            _objStore.Log($"No se pudo traer la tienda: {objError.Message.ReplaceLineEndings(" ")}");
            if (objOld != null)
            {
                objOld.Stale = true;
                return objOld;
            }
            // Nothing to show, but the clock on the page still has to be right
            return new ShopView { Language = strLanguage, Failed = true, NextResetUtc = NextReset(DateTime.UtcNow) };
        }
    }

    /// <summary>Everything the page draws, and nothing else.</summary>
    private static ShopView Parse(string strBody, string strLanguage)
    {
        using var objDocument = JsonDocument.Parse(strBody);
        var objData = objDocument.RootElement.GetProperty("data");

        var dtShop = objData.GetProperty("date").GetDateTimeOffset().UtcDateTime;
        var dtNext = NextReset(dtShop);
        var objView = new ShopView
        {
            Language = strLanguage,
            DateUtc = dtShop,
            NextResetUtc = dtNext,
            FetchedUtc = DateTime.UtcNow,
            VbucksIcon = Text(objData, "vbuckIcon")
        };

        foreach (var objEntry in objData.GetProperty("entries").EnumerateArray())
        {
            var objItem = Describe(objEntry, dtShop, dtNext);
            if (objItem != null)
            {
                objView.Entries.Add(objItem);
            }
        }

        // The game's own order: section first, then what each section puts at the top
        objView.Entries = objView.Entries
            .OrderBy(objItem => objItem.SectionRank)
            .ThenBy(objItem => objItem.SectionIndex)
            .ThenByDescending(objItem => objItem.Sort)
            .ThenByDescending(objItem => objItem.Price)
            .ToList();
        return objView;
    }

    private static ShopItem? Describe(JsonElement objEntry, DateTime dtShop, DateTime dtNext)
    {
        var dtIn = Date(objEntry, "inDate") ?? dtShop;
        var dtOut = Date(objEntry, "outDate") ?? dtNext;
        var objBundle = Child(objEntry, "bundle");
        var arrBr = Children(objEntry, "brItems");
        var arrTracks = Children(objEntry, "tracks");
        var arrCars = Children(objEntry, "cars");
        var arrInstruments = Children(objEntry, "instruments");
        var arrAll = arrBr.Concat(arrCars).Concat(arrInstruments).ToList();
        var objFirst = arrAll.FirstOrDefault();

        var strName = Text(objBundle, "name")
            ?? string.Join(" + ", arrAll.Select(objOne => Text(objOne, "name")).Where(strOne => strOne != null).Distinct())
            ?? "";
        if (strName.Length == 0 && arrTracks.Count > 0)
        {
            strName = Text(arrTracks[0], "title") ?? "";
        }
        if (strName.Length == 0)
        {
            return null;        // Nothing to call it by: not worth a tile
        }

        var objLayout = Child(objEntry, "layout");
        var objBanner = Child(objEntry, "banner");
        var objRarity = Child(objFirst, "rarity");
        var objSeries = Child(objFirst, "series");

        return new ShopItem
        {
            Id = Text(objEntry, "offerId") ?? strName,
            Name = strName,
            Image = PickImage(objEntry, objBundle, arrBr, arrTracks, arrCars, arrInstruments),
            Price = Number(objEntry, "finalPrice"),
            Was = Number(objEntry, "regularPrice"),
            Section = Text(objLayout, "name"),
            SectionRank = objLayout.HasValue ? Number(objLayout, "rank") : int.MaxValue,
            SectionIndex = objLayout.HasValue ? Number(objLayout, "index") : 0,
            Sort = Number(objEntry, "sortPriority"),
            // A jam track has no type to show, and its artist is what anybody wants to read
            Kind = Text(Child(objFirst, "type"), "displayValue") ?? (arrTracks.Count > 0 ? Text(arrTracks[0], "artist") : null),
            Rarity = Text(objRarity, "displayValue"),
            RarityKey = Text(objRarity, "value"),
            Series = Text(objSeries, "value"),
            Banner = Text(objBanner, "value"),
            BannerKey = Text(objBanner, "backendValue"),
            Tag = Text(Child(objEntry, "offerTag"), "text"),
            IsBundle = objBundle.HasValue,
            IsTrack = arrTracks.Count > 0 && arrAll.Count == 0,
            Pieces = Math.Max(arrAll.Count + arrTracks.Count, 1),
            InUtc = dtIn,
            OutUtc = dtOut,
            // Everything that came in with this shop, and everything that will not see the next one
            IsNew = dtIn >= dtShop,
            Leaving = dtOut <= dtNext
        };
    }

    /// <summary>
    /// The picture the game itself puts on the tile, when there is one; failing that, whatever the
    /// thing on sale has of its own. Every entry has at least one of these.
    /// </summary>
    private static string? PickImage(JsonElement objEntry, JsonElement? objBundle, List<JsonElement> arrBr,
        List<JsonElement> arrTracks, List<JsonElement> arrCars, List<JsonElement> arrInstruments)
    {
        var objRender = Children(Child(objEntry, "newDisplayAsset"), "renderImages").FirstOrDefault();
        var strRender = Text(objRender, "image");
        if (strRender != null)
        {
            return strRender;
        }
        if (Text(objBundle, "image") is { } strBundle)
        {
            return strBundle;
        }
        foreach (var objOne in arrBr)
        {
            var objImages = Child(objOne, "images");
            if ((Text(objImages, "featured") ?? Text(objImages, "icon") ?? Text(objImages, "smallIcon")) is { } strIcon)
            {
                return strIcon;
            }
        }
        foreach (var objOne in arrTracks)
        {
            if (Text(objOne, "albumArt") is { } strArt)
            {
                return strArt;
            }
        }
        foreach (var objOne in arrCars.Concat(arrInstruments))
        {
            var objImages = Child(objOne, "images");
            if ((Text(objImages, "large") ?? Text(objImages, "small")) is { } strPicture)
            {
                return strPicture;
            }
        }
        return null;
    }

    // ----------------------------------------------------------------------------------------
    // Reading a field that may not be there, which in this answer is most of them
    // ----------------------------------------------------------------------------------------
    private static JsonElement? Child(JsonElement? objParent, string strName) =>
        objParent is { ValueKind: JsonValueKind.Object } objObject
            && objObject.TryGetProperty(strName, out var objChild)
            && objChild.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? objChild
                : null;

    private static List<JsonElement> Children(JsonElement? objParent, string strName) =>
        Child(objParent, strName) is { ValueKind: JsonValueKind.Array } objArray
            ? objArray.EnumerateArray().ToList()
            : new List<JsonElement>();

    private static string? Text(JsonElement? objParent, string strName)
    {
        if (objParent is not { ValueKind: JsonValueKind.Object } objObject
            || !objObject.TryGetProperty(strName, out var objValue)
            || objValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var strValue = objValue.GetString();
        return string.IsNullOrWhiteSpace(strValue) ? null : strValue;
    }

    private static int Number(JsonElement? objParent, string strName) =>
        objParent is { ValueKind: JsonValueKind.Object } objObject
            && objObject.TryGetProperty(strName, out var objValue)
            && objValue.ValueKind == JsonValueKind.Number
            && objValue.TryGetInt32(out var intValue)
                ? intValue
                : 0;

    private static DateTime? Date(JsonElement? objParent, string strName) =>
        objParent is { ValueKind: JsonValueKind.Object } objObject
            && objObject.TryGetProperty(strName, out var objValue)
            && objValue.ValueKind == JsonValueKind.String
            && objValue.TryGetDateTimeOffset(out var dtValue)
                ? dtValue.UtcDateTime
                : null;
}
