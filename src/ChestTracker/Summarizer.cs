namespace ChestTracker;

/// <summary>
/// Turns the raw facts of a match into what the dashboard shows: who "you" are,
/// which category each container falls in, counts and timelines.
/// </summary>
public static class Summarizer
{
    public const string CAT_CHEST = "cofre";
    public const string CAT_SPECIAL = "especial";
    public const string CAT_SPIRIT = "espiritu";
    public const string CAT_AMMO = "municion";
    public const string CAT_OTHER = "otros";
    public const string CAT_IGNORE = "ignorar";

    public static readonly List<CategoryInfo> Categories = new()
    {
        new() { Id = CAT_CHEST, Label = "Cofres" },
        new() { Id = CAT_SPECIAL, Label = "Cofres especiales" },
        new() { Id = CAT_SPIRIT, Label = "Cofres de espíritu" },
        new() { Id = CAT_AMMO, Label = "Cajas de munición" },
        new() { Id = CAT_OTHER, Label = "Otros contenedores" },
        new() { Id = CAT_IGNORE, Label = "No contar" }
    };

    private static readonly string[] arrChestCategories = { CAT_CHEST, CAT_SPECIAL, CAT_SPIRIT };

    /// <summary>
    /// Default category from the class name. First match wins; the user can override
    /// any class from the interface.
    /// </summary>
    public static string GetDefaultCategory(string strContainerClass)
    {
        bool Has(string strText) => strContainerClass.Contains(strText, StringComparison.OrdinalIgnoreCase);

        // "Relics" is the chest of a sprite extraction site: what players call the spirit chest
        if (Has("Relic") || Has("Sprite") || Has("Spirit")) return CAT_SPIRIT;

        // The reader appends the loot tier when it matters: above zero is the special (blue) chest,
        // whatever the class is called; zero is a normal one even where a special one can appear
        if (Has("Chest") && Has("(botín nivel"))
        {
            return Has("(botín nivel 0)") ? CAT_CHEST : CAT_SPECIAL;
        }
        if (Has("RareChest") || Has("Rare_Chest")) return CAT_SPECIAL;
        if (Has("Ammo")) return CAT_AMMO;
        if (Has("Chest")) return CAT_CHEST;
        return CAT_OTHER;
    }

    public static string GetCategory(string strContainerClass, TrackerSettings objSettings)
    {
        if (objSettings.ClassOverrides.TryGetValue(strContainerClass, out var strOverride)
            && Categories.Any(objCategory => objCategory.Id == strOverride))
        {
            return strOverride;
        }
        return GetDefaultCategory(strContainerClass);
    }

    /// <summary>The Epic id used as "you" in a match: what the replay says, else the saved one.</summary>
    public static string? GetOwnerId(MatchRecord objMatch, TrackerSettings objSettings)
    {
        if (!string.IsNullOrEmpty(objMatch.DetectedOwnerId)) return objMatch.DetectedOwnerId;
        return string.IsNullOrWhiteSpace(objSettings.EpicId) ? null : objSettings.EpicId.Trim().ToUpperInvariant();
    }

    public static MatchSummary Summarize(MatchRecord objMatch, TrackerSettings objSettings, string strDemosPath)
    {
        var strOwnerId = GetOwnerId(objMatch, objSettings);
        var dicPlayers = objMatch.Players.GroupBy(objPlayer => objPlayer.Id).ToDictionary(objGroup => objGroup.Key, objGroup => objGroup.First());
        var objOwner = strOwnerId != null ? dicPlayers.GetValueOrDefault(strOwnerId) : null;

        var objSummary = new MatchSummary
        {
            File = objMatch.File,
            FileStillExists = File.Exists(Path.Combine(strDemosPath, objMatch.File)),
            Outdated = objMatch.ParserVersion < ReplayParser.PARSER_VERSION,
            Excluded = objSettings.ExcludedFiles.Contains(objMatch.File),
            StartedAt = objMatch.StartedAt,
            DurationSec = objMatch.DurationSec,
            Build = objMatch.Build,
            Playlist = objMatch.Playlist,
            ModeLabel = DescribePlaylist(objMatch.Playlist),
            OwnerId = strOwnerId,
            OwnerName = objOwner?.Name,
            Position = objMatch.Position,
            TotalPlayers = objMatch.TotalPlayers,
            Stats = objMatch.Stats
        };

        foreach (var objCategory in Categories.Where(objCategory => objCategory.Id != CAT_IGNORE))
        {
            objSummary.Counts[objCategory.Id] = 0;
        }

        // Your containers
        foreach (var objOpening in objMatch.Openings.Where(objOpening => objOpening.PlayerId == strOwnerId).OrderBy(objOpening => objOpening.Time))
        {
            var strCategory = GetCategory(objOpening.ContainerClass, objSettings);
            if (strCategory == CAT_IGNORE)
            {
                continue;
            }

            objSummary.Counts[strCategory]++;
            objSummary.Openings.Add(new TimelineOpening { T = objOpening.Time, Category = strCategory, ContainerClass = objOpening.ContainerClass, Method = objOpening.Method, X = objOpening.X, Y = objOpening.Y });
        }

        objSummary.ChestsTotal = arrChestCategories.Sum(strCategory => objSummary.Counts[strCategory]);
        objSummary.ChestsBroken = objSummary.Openings.Count(objOpening => arrChestCategories.Contains(objOpening.Category) && objOpening.Method != TrackerReplayReader.METHOD_SEARCH);
        objSummary.FirstChestSec = objSummary.Openings.FirstOrDefault(objOpening => arrChestCategories.Contains(objOpening.Category))?.T;

        // Your kills and deaths
        foreach (var objElimination in objMatch.Eliminations.OrderBy(objElimination => objElimination.Time))
        {
            if (strOwnerId == null)
            {
                break;
            }

            if (objElimination.Killer == strOwnerId && objElimination.Victim != strOwnerId)
            {
                objSummary.KillEvents.Add(new TimelineKill
                {
                    T = objElimination.Time,
                    IsKnock = objElimination.IsKnock,
                    DistanceM = objElimination.DistanceM,
                    VictimName = dicPlayers.GetValueOrDefault(objElimination.Victim)?.Name
                });
            }
            else if (objElimination.Victim == strOwnerId)
            {
                objSummary.Deaths.Add(new TimelineDeath { T = objElimination.Time, IsKnock = objElimination.IsKnock });
            }
        }

        // The game's own count credits a kill to whoever knocked the enemy, so prefer it
        objSummary.Kills = objMatch.Stats?.Eliminations
            ?? objSummary.KillEvents.Count(objKill => !objKill.IsKnock);

        // Time actually played: until your last elimination, or the whole replay if you survived
        var objLastDeath = objSummary.Deaths.LastOrDefault(objDeath => !objDeath.IsKnock);
        var bWon = objMatch.Position == 1;
        objSummary.AliveSec = objLastDeath != null && !bWon ? Math.Min(objLastDeath.T, objMatch.DurationSec) : objMatch.DurationSec;
        objSummary.ChestsPerMin = objSummary.AliveSec > 0 ? Math.Round(objSummary.ChestsTotal / (objSummary.AliveSec / 60.0), 2) : 0;

        // Squad: what the replay holds for every teammate wherever they were (kill feed and player state)
        if (objOwner?.Team != null)
        {
            foreach (var objMate in objMatch.Players.Where(objPlayer => objPlayer.Team == objOwner.Team && !objPlayer.IsBot))
            {
                var bIsYou = objMate.Id == strOwnerId;
                var objFinalBlow = objMatch.Eliminations.LastOrDefault(objElimination => objElimination.Victim == objMate.Id && !objElimination.IsKnock);

                objSummary.Squad.Add(new SquadMember
                {
                    Id = objMate.Id,
                    Name = objMate.Name,
                    IsYou = bIsYou,
                    // The game only replicates the kill count once it is above zero
                    Kills = bIsYou ? objSummary.Kills : objMate.Kills ?? 0,
                    Knocks = objMatch.Eliminations.Count(objElimination => objElimination.Killer == objMate.Id && objElimination.Victim != objMate.Id && objElimination.IsKnock),
                    TimesKnocked = objMatch.Eliminations.Count(objElimination => objElimination.Victim == objMate.Id && objElimination.IsKnock),
                    EliminatedAt = objFinalBlow?.Time,
                    EliminatedBy = objFinalBlow == null || objFinalBlow.Killer == objMate.Id ? null : dicPlayers.GetValueOrDefault(objFinalBlow.Killer)?.Name,
                    Platform = objMate.Platform,
                    Level = objMate.Level,
                    ThankedBusDriver = objMate.ThankedBusDriver,
                    Reboots = objMate.Reboots,
                    DamageDealt = bIsYou ? objMatch.Stats?.DamageDealt : null,
                    DamageTaken = bIsYou ? objMatch.Stats?.DamageTaken : null,
                    Assists = bIsYou ? objMatch.Stats?.Assists : null,
                    Revives = bIsYou ? objMatch.Stats?.Revives : null
                });
            }

            objSummary.Squad = objSummary.Squad.OrderByDescending(objMate => objMate.IsYou).ThenByDescending(objMate => objMate.Kills ?? 0).ToList();
        }

        return objSummary;
    }

    /// <summary>Every container class seen so far, with its current category.</summary>
    public static List<ClassInfo> ListClasses(IEnumerable<MatchRecord> lstMatches, TrackerSettings objSettings)
    {
        var dicClasses = new Dictionary<string, ClassInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var objMatch in lstMatches)
        {
            var strOwnerId = GetOwnerId(objMatch, objSettings);
            foreach (var objOpening in objMatch.Openings)
            {
                if (!dicClasses.TryGetValue(objOpening.ContainerClass, out var objClass))
                {
                    objClass = new ClassInfo
                    {
                        Name = objOpening.ContainerClass,
                        Category = GetCategory(objOpening.ContainerClass, objSettings),
                        DefaultCategory = GetDefaultCategory(objOpening.ContainerClass)
                    };
                    dicClasses[objOpening.ContainerClass] = objClass;
                }

                objClass.OpenedByAnyone++;
                if (objOpening.PlayerId == strOwnerId)
                {
                    objClass.OpenedByYou++;
                }
            }
        }

        return dicClasses.Values.OrderByDescending(objClass => objClass.OpenedByYou).ThenByDescending(objClass => objClass.OpenedByAnyone).ToList();
    }

    /// <summary>Readable game mode from the internal playlist name, e.g. "Playlist_NoBuildBR_Squad".</summary>
    private static string DescribePlaylist(string? strPlaylist)
    {
        if (string.IsNullOrWhiteSpace(strPlaylist))
        {
            return "Sin identificar";
        }

        bool Has(string strText) => strPlaylist.Contains(strText, StringComparison.OrdinalIgnoreCase);

        var strSize =
            Has("Solo") ? "Solitario" :
            Has("Duo") ? "Dúos" :
            Has("Trio") ? "Tríos" :
            Has("Squad") ? "Escuadrones" : null;

        var strRules = Has("NoBuild") ? "Cero construcción" : null;

        var lstParts = new[] { strSize, strRules }.Where(strPart => strPart != null).ToList();
        return lstParts.Count > 0 ? string.Join(", ", lstParts) : strPlaylist.Replace("Playlist_", "");
    }
}
