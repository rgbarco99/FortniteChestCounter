using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// The season's map: the picture, and where each named place stands.
///
/// Neither is in the replay. The replay says where a chest was opened, in the game's own
/// coordinates, but not what the island looks like or what anything is called; and the points of
/// interest only reach a replay in a parse mode far heavier than the one this program uses. So
/// both come from fortnite-api.com, a public service, and are kept in the data folder.
///
/// Nothing of the reader's travels in that request: it asks for the season's map and no more.
/// Without a connection the program carries on exactly as before, with no map tab.
///
/// The coordinates of those points of interest and the ones in the replay are the same world,
/// which is what lets a chest be placed on the picture at all.
/// </summary>
public sealed class SeasonMap
{
    private const string STR_MAP_URL = "https://fortnite-api.com/v1/map";

    /// <summary>Fortnite moves its island every season, and sometimes mid-season.</summary>
    private static readonly TimeSpan tsMaxAge = TimeSpan.FromHours(20);

    private readonly Store _objStore;
    private readonly string _strFolder;
    private readonly SemaphoreSlim _objOnce = new(1, 1);

    public SeasonMap(Store objStore)
    {
        _objStore = objStore;
        _strFolder = Path.Combine(objStore.DataFolder, "mapa");
    }

    private string ImageFile => Path.Combine(_strFolder, "mapa.png");
    private string PoisFile => Path.Combine(_strFolder, "sitios.json");

    public bool HasImage => File.Exists(ImageFile);

    public string ReadImagePath() => ImageFile;

    /// <summary>What the page needs: the places, and whether there is a picture to draw them on.</summary>
    public MapView Describe()
    {
        var objView = new MapView { HasImage = HasImage };
        try
        {
            if (File.Exists(PoisFile))
            {
                objView.Places = JsonSerializer.Deserialize<List<MapPlace>>(File.ReadAllText(PoisFile)) ?? new List<MapPlace>();
                objView.FetchedUtc = File.GetLastWriteTimeUtc(PoisFile);
            }
        }
        catch (Exception objError)
        {
            _objStore.Log($"No se pudo leer el mapa guardado: {objError.Message}");
        }
        return objView;
    }

    /// <summary>
    /// Downloads the map when there is none or the one there has gone stale. Runs at most once at
    /// a time, and a failure only means the tab keeps the map it already had, or none.
    /// </summary>
    public async Task RefreshAsync(CancellationToken objToken = default)
    {
        if (File.Exists(PoisFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(PoisFile) < tsMaxAge && HasImage)
        {
            return;
        }
        if (!await _objOnce.WaitAsync(0, objToken))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_strFolder);
            using var objClient = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            objClient.DefaultRequestHeaders.UserAgent.ParseAdd("FortniteChestCounter");

            var strBody = await objClient.GetStringAsync(STR_MAP_URL, objToken);
            using var objDocument = JsonDocument.Parse(strBody);
            var objData = objDocument.RootElement.GetProperty("data");

            var lstPlaces = new List<MapPlace>();
            foreach (var objPoi in objData.GetProperty("pois").EnumerateArray())
            {
                if (!objPoi.TryGetProperty("location", out var objWhere) || objWhere.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                lstPlaces.Add(new MapPlace
                {
                    Name = objPoi.TryGetProperty("name", out var objName) ? objName.GetString() ?? "" : "",
                    // Centimetres in the replay's world; stored in metres, as the openings are
                    X = Math.Round(objWhere.GetProperty("x").GetDouble() / 100.0),
                    Y = Math.Round(objWhere.GetProperty("y").GetDouble() / 100.0)
                });
            }

            if (lstPlaces.Count == 0)
            {
                throw new InvalidDataException("La respuesta no traía ningún sitio.");
            }

            // The plain picture: names are drawn by the page, in the reader's own language
            var strImageUrl = objData.GetProperty("images").GetProperty("blank").GetString()
                ?? throw new InvalidDataException("La respuesta no traía la imagen del mapa.");
            var arrImage = await objClient.GetByteArrayAsync(strImageUrl, objToken);

            await File.WriteAllBytesAsync(ImageFile, arrImage, objToken);
            await File.WriteAllTextAsync(PoisFile, JsonSerializer.Serialize(lstPlaces), objToken);
            _objStore.Log($"Mapa de la temporada actualizado: {lstPlaces.Count} sitios, {arrImage.Length / 1024} KB");
            _objStore.Touch();
        }
        catch (Exception objError)
        {
            _objStore.Log($"No se pudo descargar el mapa de la temporada: {objError.Message.ReplaceLineEndings(" ")}");
        }
        finally
        {
            _objOnce.Release();
        }
    }
}
