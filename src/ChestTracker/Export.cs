using System.Globalization;
using System.Text;

namespace ChestTracker;

/// <summary>
/// The saved matches as a spreadsheet: one row per match, the same figures the table shows.
/// Semicolons and decimal commas, which is what a spreadsheet opened in Spain or France expects;
/// the byte order mark is what makes it show the accents instead of mojibake.
/// </summary>
public static class Export
{
    public static byte[] BuildCsv(Store objStore)
    {
        var objSettings = objStore.Settings;
        var objText = new StringBuilder();

        string[] arrHeadings =
        [
            "Fecha", "Hora", "Modo", "Puesto", "De cuántos", "Kills", "Minutos jugados",
            "Cofres", "Cofres especiales", "Cofres de espíritu", "Cofres en total", "Cofres por minuto",
            "Primer cofre (min)", "Cajas de munición", "Otros contenedores",
            "Daño hecho", "Daño recibido", "Precisión %", "Asistencias", "Reanimaciones", "Distancia (km)",
            "Compañeros", "Archivo", "No contada"
        ];
        objText.AppendLine(string.Join(';', arrHeadings));

        foreach (var objRecord in objStore.GetMatches().OrderBy(objRecord => objRecord.StartedAt))
        {
            MatchSummary objMatch;
            try { objMatch = Summarizer.Summarize(objRecord, objSettings, objSettings.DemosPath); }
            catch (Exception) { continue; }

            var objStats = objMatch.Stats;
            string[] arrCells =
            [
                objMatch.StartedAt.ToString("yyyy-MM-dd"),
                objMatch.StartedAt.ToString("HH:mm"),
                objMatch.ModeLabel ?? "",
                Num(objMatch.Position),
                Num(objMatch.TotalPlayers),
                Num(objMatch.Kills),
                Num(Math.Round(objMatch.AliveSec / 60.0, 1)),
                Num(objMatch.Counts.GetValueOrDefault(Summarizer.CAT_CHEST)),
                Num(objMatch.Counts.GetValueOrDefault(Summarizer.CAT_SPECIAL)),
                Num(objMatch.Counts.GetValueOrDefault(Summarizer.CAT_SPIRIT)),
                Num(objMatch.ChestsTotal),
                Num(objMatch.ChestsPerMin),
                objMatch.FirstChestSec is { } dblFirst ? Num(Math.Round(dblFirst / 60.0, 1)) : "",
                Num(objMatch.Counts.GetValueOrDefault(Summarizer.CAT_AMMO)),
                Num(objMatch.Counts.GetValueOrDefault(Summarizer.CAT_OTHER)),
                Num(objStats?.DamageDealt),
                Num(objStats?.DamageTaken),
                objStats?.Accuracy is { } dblAccuracy ? Num(Math.Round(dblAccuracy * 100, 1)) : "",
                Num(objStats?.Assists),
                Num(objStats?.Revives),
                Num(objStats?.DistanceKm),
                Cell(string.Join(", ", objMatch.Squad.Where(objMate => !objMate.IsYou).Select(objMate => objMate.Name ?? objMate.Id))),
                Cell(objMatch.File),
                objMatch.Excluded ? "sí" : ""
            ];
            objText.AppendLine(string.Join(';', arrCells));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(objText.ToString())).ToArray();
    }

    /// <summary>A number with the decimal comma a spreadsheet here expects; empty when there is none.</summary>
    private static string Num(double? dblValue) =>
        dblValue is { } dblNumber ? dblNumber.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',') : "";

    private static string Num(int? intValue) => intValue?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Text that may hold the separator, a quote or a line break.</summary>
    private static string Cell(string strText) =>
        strText.Contains(';') || strText.Contains('"') || strText.Contains('\n')
            ? $"\"{strText.Replace("\"", "\"\"")}\""
            : strText;
}
