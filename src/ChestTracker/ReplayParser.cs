using System.Diagnostics.CodeAnalysis;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Exceptions;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestTracker;

/// <summary>
/// Replay reader that, on top of what the library already extracts, records every
/// container opening together with the pawn that searched it.
///
/// It does not depend on the library knowing each chest class: any actor whose
/// replicated properties include "SearchingPawn" / "bAlreadySearched" is treated as a
/// container, so new chest types added by a Fortnite season are picked up automatically.
/// </summary>
public sealed class TrackerReplayReader : FortniteReplayReader.ReplayReader
{
    private const string FIELD_SEARCHED = "bAlreadySearched";
    private const string FIELD_PAWN = "SearchingPawn";
    private const string FIELD_ANIM = "SearchAnimationCount";
    private const string FIELD_DESTROYED = "bDestroyed";
    private const string FIELD_LOOT_TIER = "ReplicatedLootTier";

    /// <summary>Only sent when an actor comes into view, so its presence marks "this is the actor's full state".</summary>
    private const string FIELD_REMOTE_ROLE = "RemoteRole";
    private const string FIELD_PLAYER_STATE = "PlayerState";
    private const string FIELD_MOVEMENT = "ReplicatedMovement";
    private const string FIELD_FROM_CONTAINER = "bTossedFromContainer";

    public const string METHOD_SEARCH = "search";
    public const string METHOD_DESTROY = "destroy";

    /// <summary>Broken without a direct hit: what it stood on was destroyed, or it went down with the thing next to it.</summary>
    public const string METHOD_COLLAPSE = "collapse";

    /// <summary>A container that breaks throws its loot in the same frame; this is the tolerance for "same frame".</summary>
    private const double LOOT_SAME_FRAME_SEC = 0.05;

    /// <summary>How close a player must be to that loot to be the one who broke the container (centimetres).</summary>
    private const double COLLAPSE_MAX_DISTANCE_CM = 1000;

    /// <summary>A position older than this says nothing about where the player is now.</summary>
    private const double POSITION_MAX_AGE_SEC = 3;
    private const double POSITION_SAMPLE_GAP_SEC = 0.2;
    private const int MAX_DEFERRED_UPDATES = 200_000;

    private enum TapKind { Pawn, Pickup, Other }

    /// <summary>
    /// What to read from one export group: which kind of actor it is, the handles of the wanted
    /// properties, and whether it is known to be a container yet (its search properties have a name).
    /// </summary>
    private sealed record Tap(TapKind Kind, Dictionary<uint, string>? Handles, bool IsContainer);

    private sealed class ContainerState
    {
        public long SearchAnimationCount;
        public uint SearchingPawn;
        public bool IsOpened;
        public string? Identity;
        public int? LootTier;
    }

    public sealed record RawOpening(double Time, string ContainerClass, uint PawnGuid, string Method);

    /// <summary>A property that arrived before the replay said what it is called: handle and raw bits.</summary>
    private readonly record struct RawProperty(uint Handle, ulong Bits);

    /// <summary>
    /// An actor update that could not be judged when it was read: it holds nameless properties, or
    /// the actor broke before the replay had shown that its class is a container.
    /// </summary>
    private sealed record DeferredUpdate(double Time, NetFieldExportGroup Group, ulong ContainerKey, string Identity,
        List<RawProperty>? Unnamed, bool? Searched, bool? Destroyed, uint? Pawn);

    /// <summary>A container that broke with nobody credited by the game.</summary>
    private sealed record Orphan(double Time, string ContainerClass, string Identity);

    private readonly record struct Position(float Time, float X, float Y, float Z);

    private readonly Dictionary<string, Tap> _dicTaps = new();
    private readonly Dictionary<ulong, ContainerState> _dicContainers = new();

    /// <summary>
    /// Containers already opened, by identity rather than by network id. The game gives a
    /// container a new network id when it leaves your surroundings and comes back, and it
    /// comes back "already opened by X": without this set it would be counted again.
    /// </summary>
    private readonly HashSet<string> _setOpenedIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _dicOpeningByIdentity = new(StringComparer.Ordinal);

    private readonly List<DeferredUpdate> _lstDeferred = new();
    private readonly List<Orphan> _lstOrphans = new();

    /// <summary>Where and when loot thrown out of a container first appeared.</summary>
    private readonly List<Position> _lstLootSpawns = new();
    private readonly HashSet<uint> _setSeenLoot = new();

    /// <summary>Where each player pawn was over the match, a few samples per second.</summary>
    private readonly Dictionary<uint, List<Position>> _dicPawnTracks = new();
    private readonly NetBitReader _objMovementReader = new();

    /// <summary>Network id -> network id of its outer object (the level an actor belongs to).</summary>
    private readonly Dictionary<uint, uint> _dicOuterOf = new();
    private readonly Stack<uint> _stkNestedLoad = new();
    private float _fltFrameTime;

    public List<RawOpening> Openings { get; } = new();

    /// <summary>Time of the last frame read; in a replay still being recorded this is how far the match has got.</summary>
    public float LastFrameTime => _fltFrameTime;

    public Dictionary<uint, uint> PawnToPlayerState { get; } = new();
    public Dictionary<uint, string> PlayerStateToId { get; } = new();
    public uint? RecorderPlayerState { get; private set; }

    public TrackerReplayReader() : base(null!, ParseMode.Normal) { }

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        // Peek the frame time, rewind, and let the library do the real read
        var intStart = archive.Position;
        if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS)
        {
            archive.ReadInt32();
        }
        _fltFrameTime = archive.ReadSingle();
        archive.Seek(intStart);

        base.ReadDemoFrameIntoPlaybackPackets(archive);
    }

    /// <summary>
    /// The library reads "object, then its outer" recursively but only keeps the name.
    /// Remember the outer too: the level is what tells apart two actors with the same name.
    /// </summary>
    public override NetworkGUID InternalLoadObject(FArchive archive, bool isExportingNetGUIDBunch, int internalLoadObjectRecursionCount = 0)
    {
        _stkNestedLoad.Push(0);
        var objGuid = base.InternalLoadObject(archive, isExportingNetGUIDBunch, internalLoadObjectRecursionCount);
        var intOuter = _stkNestedLoad.Pop();

        if (intOuter != 0 && objGuid.Value != 0)
        {
            _dicOuterOf[objGuid.Value] = intOuter;
        }

        // Hand our own id to the call that is loading us as its outer
        if (_stkNestedLoad.Count > 0)
        {
            _stkNestedLoad.Pop();
            _stkNestedLoad.Push(objGuid.Value);
        }

        return objGuid;
    }

    public override void ReadEvent(FArchive archive)
    {
        // Event types the library does not know are irrelevant here
        try { base.ReadEvent(archive); }
        catch (UnknownEventException) { }
    }

    // Export groups learn their property names as the replay goes on, so the cached
    // handle lookups are dropped whenever new names arrive.
    public override void ReadNetFieldExports(FArchive archive)
    {
        base.ReadNetFieldExports(archive);
        _dicTaps.Clear();
    }

    public override void ReceiveNetFieldExportsCompat(FBitArchive bitArchive)
    {
        base.ReceiveNetFieldExportsCompat(bitArchive);
        _dicTaps.Clear();
    }

    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex, [NotNullWhen(returnValue: true)] out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
    {
        if (Channels[channelIndex] != null)
        {
            var objTap = GetTap(group);

            // Groups with nothing to read yet are still looked at: a container can be broken
            // before the replay has said what its properties are called
            if (objTap.Handles != null || objTap.Kind == TapKind.Other)
            {
                ReadTappedUpdate(archive, group, channelIndex, objTap, enablePropertyChecksum);
            }
        }

        return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
    }

    /// <summary>
    /// Three kinds of actor are read here: containers (anything with search properties),
    /// player pawns (human or bot, whatever blueprint they use) for their link to a player and
    /// their position, and loot on the ground for where a container threw it.
    /// </summary>
    private Tap GetTap(NetFieldExportGroup group)
    {
        if (_dicTaps.TryGetValue(group.PathName, out var objTap))
        {
            return objTap;
        }

        var enuKind = group.PathName.Contains("PlayerPawn", StringComparison.OrdinalIgnoreCase) ? TapKind.Pawn
            : group.PathName.Contains("Pickup", StringComparison.OrdinalIgnoreCase) ? TapKind.Pickup
            : TapKind.Other;

        Dictionary<uint, string>? dicHandles = null;
        for (uint intHandle = 0; intHandle < group.NetFieldExports.Length; intHandle++)
        {
            var strName = group.NetFieldExports[intHandle]?.Name;
            var bWanted = enuKind switch
            {
                TapKind.Pawn => strName is FIELD_PLAYER_STATE or FIELD_MOVEMENT,
                TapKind.Pickup => strName is FIELD_FROM_CONTAINER,
                _ => strName is FIELD_SEARCHED or FIELD_PAWN or FIELD_ANIM or FIELD_DESTROYED or FIELD_LOOT_TIER or FIELD_REMOTE_ROLE
            };
            if (bWanted)
            {
                dicHandles ??= new Dictionary<uint, string>();
                dicHandles[intHandle] = strName!;
            }
        }

        // Plenty of actors can be destroyed; only the ones that can be searched are containers
        var bIsContainer = enuKind == TapKind.Other && dicHandles != null && (dicHandles.ContainsValue(FIELD_PAWN) || dicHandles.ContainsValue(FIELD_SEARCHED));

        objTap = new Tap(enuKind, dicHandles, bIsContainer);
        _dicTaps[group.PathName] = objTap;
        return objTap;
    }

    /// <summary>
    /// Reads the wanted properties of one actor update without consuming the archive:
    /// the property list is "handle, bit count, payload" repeated until handle 0.
    /// </summary>
    private void ReadTappedUpdate(FBitArchive archive, NetFieldExportGroup group, uint channelIndex, Tap objTap, bool bHasChecksumBit)
    {
        bool? bSearched = null;
        bool? bDestroyed = null;
        uint? intPawn = null;
        uint? intPlayerState = null;
        long? lngAnimCount = null;
        int? intLootTier = null;
        bool? bFromContainer = null;
        var bCameIntoView = false;
        FVector? objPawnLocation = null;
        List<RawProperty>? lstUnnamed = null;

        archive.Mark();
        try
        {
            if (bHasChecksumBit)
            {
                archive.ReadBit();
            }

            while (!archive.AtEnd() && !archive.IsError)
            {
                var intHandle = archive.ReadIntPacked();
                if (intHandle == 0)
                {
                    break;
                }
                intHandle--;

                var intBits = (int)archive.ReadIntPacked();
                if (intBits == 0)
                {
                    continue;
                }

                string? strName = null;
                if (objTap.Handles == null || !objTap.Handles.TryGetValue(intHandle, out strName))
                {
                    var bNameless = objTap.Kind == TapKind.Other
                        && (intHandle >= group.NetFieldExports.Length || group.NetFieldExports[intHandle] == null);

                    if (bNameless && intBits <= 64)
                    {
                        // Keep the raw bits: once the name is known they can still be read
                        ulong lngBits = (uint)archive.ReadBitsToInt(Math.Min(intBits, 32));
                        if (intBits > 32)
                        {
                            lngBits |= (ulong)(uint)archive.ReadBitsToInt(intBits - 32) << 32;
                        }
                        (lstUnnamed ??= new List<RawProperty>()).Add(new RawProperty(intHandle, lngBits));
                    }
                    else
                    {
                        archive.SkipBits(intBits);
                    }
                    continue;
                }

                var intBitsBefore = archive.GetBitsLeft();
                switch (strName)
                {
                    case FIELD_SEARCHED: bSearched = archive.ReadBit(); break;
                    case FIELD_DESTROYED: bDestroyed = archive.ReadBit(); break;
                    case FIELD_PAWN: intPawn = archive.ReadIntPacked(); break;
                    case FIELD_PLAYER_STATE: intPlayerState = archive.ReadIntPacked(); break;
                    case FIELD_FROM_CONTAINER: bFromContainer = archive.ReadBit(); break;
                    case FIELD_ANIM: lngAnimCount = (uint)archive.ReadBitsToInt(Math.Min(intBits, 32)); break;
                    case FIELD_LOOT_TIER: intLootTier = archive.ReadBitsToInt(Math.Min(intBits, 32)); break;
                    case FIELD_REMOTE_ROLE: bCameIntoView = true; break;
                    case FIELD_MOVEMENT: objPawnLocation = ReadMovementLocation(archive, intBits); break;
                }

                var intBitsUnread = intBits - (intBitsBefore - archive.GetBitsLeft());
                if (intBitsUnread > 0)
                {
                    archive.SkipBits(intBitsUnread);
                }
            }
        }
        finally
        {
            archive.Pop();
        }

        var objActor = Channels[channelIndex]!.Actor;
        var intActorGuid = objActor?.ActorNetGUID.Value ?? 0;

        switch (objTap.Kind)
        {
            case TapKind.Pawn:
                if (intPlayerState is > 0 && intActorGuid != 0)
                {
                    PawnToPlayerState[intActorGuid] = intPlayerState.Value;
                }
                if (objPawnLocation is not null && intActorGuid != 0)
                {
                    TrackPawn(intActorGuid, objPawnLocation);
                }
                return;

            case TapKind.Pickup:
                // The first sighting of loot thrown by a container is, give or take, where the container is
                if (bFromContainer == true && objActor?.Location is { } objSpawn && _setSeenLoot.Add(intActorGuid != 0 ? intActorGuid : channelIndex | 0x80000000))
                {
                    _lstLootSpawns.Add(new Position(_fltFrameTime, (float)objSpawn.X, (float)objSpawn.Y, (float)objSpawn.Z));
                }
                return;
        }

        var lngKey = intActorGuid != 0 ? intActorGuid : (1UL << 40) | channelIndex;

        // Kept for the end of the replay: nameless properties, and anything that breaks before
        // its class has shown itself to be a container (the first one of its kind in the match)
        var bJudgeLater = lstUnnamed != null || (!objTap.IsContainer && bDestroyed == true);
        if (bJudgeLater && _lstDeferred.Count < MAX_DEFERRED_UPDATES)
        {
            // The identity is worked out now: the names behind a network id can change later in the replay
            _lstDeferred.Add(new DeferredUpdate(_fltFrameTime, group, lngKey, GetContainerIdentity(group, objActor?.Location, intActorGuid), lstUnnamed, bSearched, bDestroyed, intPawn));
        }

        // Chests are refilled during the match. One that comes into view closed is a fresh chest,
        // even if it was opened earlier: forget that opening so the next search counts again.
        // (One that comes into view already opened keeps its identity and is not counted twice.)
        if (bCameIntoView && bSearched != true && bDestroyed != true && (objTap.IsContainer || intLootTier != null))
        {
            var strSeenClosed = GetContainerIdentity(group, objActor?.Location, intActorGuid);
            if (_setOpenedIdentities.Remove(strSeenClosed))
            {
                _dicOpeningByIdentity.Remove(strSeenClosed);
                RefilledCount++;
            }
        }

        if (bSearched == null && bDestroyed == null && intPawn == null && lngAnimCount == null && intLootTier == null)
        {
            return;
        }

        if (!objTap.IsContainer && intLootTier == null)
        {
            return;
        }

        if (!_dicContainers.TryGetValue(lngKey, out var objState))
        {
            objState = new ContainerState();
            _dicContainers[lngKey] = objState;
        }

        if (intLootTier != null)
        {
            objState.LootTier = intLootTier;
        }

        if (!objTap.IsContainer)
        {
            return;
        }

        // Follow the searching pawn exactly, including when the game clears it
        if (intPawn != null)
        {
            objState.SearchingPawn = intPawn.Value;
        }

        var bAnimationAdvanced = lngAnimCount > objState.SearchAnimationCount;
        if (lngAnimCount != null)
        {
            objState.SearchAnimationCount = lngAnimCount.Value;
        }

        // A container that becomes searchable again (not seen in battle royale, but cheap to honour)
        if (bSearched == false && objState.IsOpened)
        {
            objState.IsOpened = false;
            if (objState.Identity != null)
            {
                _setOpenedIdentities.Remove(objState.Identity);
                _dicOpeningByIdentity.Remove(objState.Identity);
            }
            return;
        }

        // Opened = searched with the interact key, or broken open (the loot drops either way)
        var bOpenedNow = bSearched == true || bDestroyed == true || bAnimationAdvanced;
        if (!bOpenedNow || objState.IsOpened)
        {
            return;
        }

        objState.IsOpened = true;
        objState.Identity = GetContainerIdentity(group, objActor?.Location, intActorGuid);

        // Same container seen before under another network id: it is not a new opening
        if (!_setOpenedIdentities.Add(objState.Identity))
        {
            return;
        }

        var strClass = DescribeClass(GetClassName(group.PathName), objState.LootTier);
        var bBroken = bSearched != true && bDestroyed == true;

        if (objState.SearchingPawn != 0)
        {
            _dicOpeningByIdentity[objState.Identity] = Openings.Count;
            Openings.Add(new RawOpening(_fltFrameTime, strClass, objState.SearchingPawn, bBroken ? METHOD_DESTROY : METHOD_SEARCH));
        }
        else if (bBroken)
        {
            // Nobody credited: decided at the end, from where its loot landed
            _lstOrphans.Add(new Orphan(_fltFrameTime, strClass, objState.Identity));
        }
    }

    /// <summary>Decodes a replicated movement far enough to get the location.</summary>
    private FVector? ReadMovementLocation(FBitArchive archive, int intBits)
    {
        try
        {
            _objMovementReader.EngineNetworkVersion = archive.EngineNetworkVersion;
            _objMovementReader.NetworkVersion = archive.NetworkVersion;
            _objMovementReader.ReplayHeaderFlags = archive.ReplayHeaderFlags;
            _objMovementReader.NetworkReplayVersion = archive.NetworkReplayVersion;
            _objMovementReader.FillBitsFrom(archive, intBits);

            var objMovement = _objMovementReader.SerializeRepMovement();
            return _objMovementReader.IsError ? null : objMovement.Location;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TrackPawn(uint intPawnGuid, FVector objLocation)
    {
        if (!_dicPawnTracks.TryGetValue(intPawnGuid, out var lstTrack))
        {
            lstTrack = new List<Position>();
            _dicPawnTracks[intPawnGuid] = lstTrack;
        }

        if (lstTrack.Count == 0 || _fltFrameTime - lstTrack[^1].Time >= POSITION_SAMPLE_GAP_SEC)
        {
            lstTrack.Add(new Position(_fltFrameTime, (float)objLocation.X, (float)objLocation.Y, (float)objLocation.Z));
        }
    }

    /// <summary>
    /// Call once the whole replay has been read. Settles the two kinds of opening that cannot be
    /// decided on the spot: updates whose property names arrived later, and containers that broke
    /// with nobody credited.
    /// </summary>
    public void ResolvePending()
    {
        foreach (var objPending in _lstDeferred.OrderBy(objPending => objPending.Time))
        {
            // By now every class that is a container has said so
            if (!objPending.Group.NetFieldExports.Any(objField => objField?.Name is FIELD_PAWN or FIELD_SEARCHED))
            {
                continue;
            }

            var bSearched = objPending.Searched;
            var bDestroyed = objPending.Destroyed;
            var intPawn = objPending.Pawn ?? 0;

            foreach (var objProperty in objPending.Unnamed ?? [])
            {
                var strName = objProperty.Handle < objPending.Group.NetFieldExports.Length ? objPending.Group.NetFieldExports[objProperty.Handle]?.Name : null;
                switch (strName)
                {
                    case FIELD_SEARCHED: bSearched = (objProperty.Bits & 1) == 1; break;
                    case FIELD_DESTROYED: bDestroyed = (objProperty.Bits & 1) == 1; break;
                    case FIELD_PAWN: intPawn = DecodeIntPacked(objProperty.Bits); break;
                }
            }

            if (bSearched != true && bDestroyed != true)
            {
                continue;
            }

            var bBroken = bSearched != true;
            var intTier = _dicContainers.TryGetValue(objPending.ContainerKey, out var objState) ? objState.LootTier : null;
            var strClass = DescribeClass(GetClassName(objPending.Group.PathName), intTier);
            var strIdentity = objPending.Identity;

            if (_dicOpeningByIdentity.TryGetValue(strIdentity, out var intIndex))
            {
                // Already counted from a later sighting: this one says when it really happened
                if (intPawn != 0 && Openings[intIndex].Time > objPending.Time)
                {
                    Openings[intIndex] = new RawOpening(objPending.Time, strClass, intPawn, bBroken ? METHOD_DESTROY : METHOD_SEARCH);
                }
                continue;
            }

            _setOpenedIdentities.Add(strIdentity);
            _lstOrphans.RemoveAll(objOrphan => objOrphan.Identity == strIdentity);

            if (intPawn != 0)
            {
                _dicOpeningByIdentity[strIdentity] = Openings.Count;
                Openings.Add(new RawOpening(objPending.Time, strClass, intPawn, bBroken ? METHOD_DESTROY : METHOD_SEARCH));
            }
            else if (bBroken)
            {
                _lstOrphans.Add(new Orphan(objPending.Time, strClass, strIdentity));
            }
        }

        foreach (var objOrphan in _lstOrphans)
        {
            // An unopened container always throws loot when it breaks, in that same frame
            var lstLoot = _lstLootSpawns.Where(objLoot => Math.Abs(objLoot.Time - objOrphan.Time) <= LOOT_SAME_FRAME_SEC).ToList();
            if (lstLoot.Count == 0)
            {
                continue;
            }

            uint intNearestPawn = 0;
            var dblNearest = double.MaxValue;
            foreach (var (intPawnGuid, lstTrack) in _dicPawnTracks)
            {
                if (!PawnToPlayerState.ContainsKey(intPawnGuid) || FindPosition(lstTrack, objOrphan.Time) is not { } objPosition)
                {
                    continue;
                }

                var dblDistance = lstLoot.Min(objLoot => Math.Sqrt(Math.Pow(objLoot.X - objPosition.X, 2) + Math.Pow(objLoot.Y - objPosition.Y, 2) + Math.Pow(objLoot.Z - objPosition.Z, 2)));
                if (dblDistance < dblNearest)
                {
                    dblNearest = dblDistance;
                    intNearestPawn = intPawnGuid;
                }
            }

            if (intNearestPawn != 0 && dblNearest <= COLLAPSE_MAX_DISTANCE_CM)
            {
                Openings.Add(new RawOpening(objOrphan.Time, objOrphan.ContainerClass, intNearestPawn, METHOD_COLLAPSE));
            }
        }

        Openings.Sort((objLeft, objRight) => objLeft.Time.CompareTo(objRight.Time));
        _dicOpeningByIdentity.Clear();
        DeferredCount = _lstDeferred.Count;
    }

    /// <summary>How many opened containers were later seen closed again, that is, refilled (diagnostics).</summary>
    public int RefilledCount { get; private set; }

    /// <summary>How many updates had to wait for the end of the replay (diagnostics).</summary>
    public int DeferredCount { get; private set; }

    /// <summary>The sample of a pawn's track closest in time, if it is recent enough to trust.</summary>
    private static Position? FindPosition(List<Position> lstTrack, double dblTime)
    {
        if (lstTrack.Count == 0)
        {
            return null;
        }

        var intLow = 0;
        var intHigh = lstTrack.Count - 1;
        while (intLow < intHigh)
        {
            var intMiddle = (intLow + intHigh) / 2;
            if (lstTrack[intMiddle].Time < dblTime) intLow = intMiddle + 1; else intHigh = intMiddle;
        }

        var objBest = lstTrack[intLow];
        if (intLow > 0 && Math.Abs(lstTrack[intLow - 1].Time - dblTime) < Math.Abs(objBest.Time - dblTime))
        {
            objBest = lstTrack[intLow - 1];
        }
        return Math.Abs(objBest.Time - dblTime) <= POSITION_MAX_AGE_SEC ? objBest : null;
    }

    /// <summary>Unreal's packed integer, from raw bits: seven bits of value per byte, lowest bit says "more follows".</summary>
    private static uint DecodeIntPacked(ulong lngBits)
    {
        uint intValue = 0;
        for (var intShift = 0; intShift < 35; intShift += 7)
        {
            var intByte = (uint)(lngBits & 0xFF);
            intValue |= (intByte >> 1) << intShift;
            if ((intByte & 1) == 0)
            {
                break;
            }
            lngBits >>= 8;
        }
        return intValue;
    }

    /// <summary>
    /// Chests come in loot tiers and the special (blue) one is a higher tier, sometimes of the very
    /// same class as a normal chest. The tier is made part of the name so each can be classified.
    /// </summary>
    private static string DescribeClass(string strClass, int? intLootTier)
    {
        if (intLootTier == null || !strClass.Contains("Chest", StringComparison.OrdinalIgnoreCase))
        {
            return strClass;
        }

        var bNamedRare = strClass.Contains("Rare", StringComparison.OrdinalIgnoreCase);
        return intLootTier > 0 || bNamedRare ? $"{strClass} (botín nivel {intLootTier})" : strClass;
    }

    /// <summary>
    /// What makes a container "the same one" for the whole match.
    /// Placed in the map: its name plus the level it belongs to. Spawned during the match
    /// (it has no name of its own): its class plus where it stands, to the nearest metre.
    /// </summary>
    private string GetContainerIdentity(NetFieldExportGroup group, FVector? objLocation, uint intActorGuid)
    {
        var strClass = GetClassName(group.PathName);
        var bPlacedInMap = (intActorGuid & 1) == 1;

        if (bPlacedInMap && _netGuidCache.TryGetPathName(intActorGuid, out var strName) && !string.IsNullOrEmpty(strName))
        {
            var lstParts = new List<string> { strName };
            var intCurrent = intActorGuid;
            for (var intDepth = 0; intDepth < 8 && _dicOuterOf.TryGetValue(intCurrent, out var intOuter); intDepth++)
            {
                if (_netGuidCache.TryGetPathName(intOuter, out var strOuterName) && !string.IsNullOrEmpty(strOuterName))
                {
                    lstParts.Add(strOuterName);
                }
                intCurrent = intOuter;
            }
            return $"{strClass}|{string.Join("<", lstParts)}";
        }

        if (objLocation is not null)
        {
            return $"{strClass}|@{Math.Round(objLocation.X / 100)},{Math.Round(objLocation.Y / 100)},{Math.Round(objLocation.Z / 100)}";
        }

        return $"{strClass}|#{intActorGuid}";
    }

    /// <summary>"/Game/.../Tiered_Chest_Athena.Tiered_Chest_Athena_C" -> "Tiered_Chest_Athena"</summary>
    private static string GetClassName(string strPathName)
    {
        var strName = strPathName[(strPathName.LastIndexOf('.') + 1)..];
        return strName.EndsWith("_C", StringComparison.Ordinal) ? strName[..^2] : strName;
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        var intActorGuid = Channels[channelIndex]?.Actor?.ActorNetGUID.Value;

        switch (exportGroup)
        {
            case FortPlayerState objPlayerState when intActorGuid != null:
                var strId = objPlayerState.UniqueId ?? objPlayerState.UniqueID ?? objPlayerState.BotUniqueId;
                if (!string.IsNullOrEmpty(strId))
                {
                    PlayerStateToId[intActorGuid.Value] = strId.ToUpperInvariant();
                }
                break;

            case GameState objGameState when objGameState.RecorderPlayerState?.Value is > 0:
                RecorderPlayerState = objGameState.RecorderPlayerState.Value;
                break;
        }

        base.OnExportRead(channelIndex, exportGroup);
    }
}

public static class ReplayParser
{
    /// <summary>The unencrypted start of a replay file: enough to know when it began and whether it can be read yet.</summary>
    public sealed record ReplayHeader(bool IsLive, int LiveFlagAt, DateTime? Timestamp, bool IsEncrypted, bool HasKey, int ChunksStart);

    /// <summary>
    /// Reads the info header by hand (magic, versions, length, name, live flag, timestamp, key).
    /// Returns null when the bytes are not a layout we understand.
    /// </summary>
    public static ReplayHeader? ReadHeader(byte[] arrBytes)
    {
        try
        {
            var intPos = 0;
            int ReadInt32() { var intValue = BitConverter.ToInt32(arrBytes, intPos); intPos += 4; return intValue; }

            ReadInt32();                                   // magic
            var intFileVersion = ReadInt32();
            if (intFileVersion >= 7)
            {
                var intCustomVersions = ReadInt32();
                intPos += intCustomVersions * 20;          // 16-byte id + int32 version each
            }
            intPos += 12;                                  // length, network version, changelist
            var intNameLength = ReadInt32();
            intPos += intNameLength < 0 ? -intNameLength * 2 : intNameLength;

            var intLiveFlagAt = intPos;
            var bIsLive = ReadInt32() != 0;

            DateTime? dtTimestamp = null;
            if (intFileVersion >= 3)
            {
                dtTimestamp = DateTime.FromBinary(BitConverter.ToInt64(arrBytes, intPos));
                intPos += 8;
            }
            if (intFileVersion >= 2) intPos += 4;          // is compressed

            var bIsEncrypted = false;
            var bHasKey = false;
            if (intFileVersion >= 6)
            {
                bIsEncrypted = ReadInt32() != 0;
                var intKeyLength = ReadInt32();
                // While a match is being recorded the game leaves the key blank and fills it in at the end
                bHasKey = arrBytes.AsSpan(intPos, intKeyLength).IndexOfAnyExcept((byte)0) >= 0;
                intPos += intKeyLength;
            }

            return new ReplayHeader(bIsLive, intLiveFlagAt, dtTimestamp, bIsEncrypted, bHasKey, intPos);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the "type, size, payload" chunks and returns the offset where the last complete one
    /// ends. For a finished replay that is the whole file.
    /// </summary>
    public static int FindEndOfCompleteChunks(byte[] arrBytes, ReplayHeader? objHeader)
    {
        if (objHeader == null)
        {
            // Not a layout we understand: let the reader deal with the whole file
            return arrBytes.Length;
        }

        var intPos = objHeader.ChunksStart;
        var intLastGood = intPos;
        while (intPos + 8 <= arrBytes.Length)
        {
            var intSize = BitConverter.ToInt32(arrBytes, intPos + 4);
            intPos += 8;
            if (intSize < 0 || (long)intPos + intSize > arrBytes.Length)
            {
                break;
            }
            intPos += intSize;
            intLastGood = intPos;
        }
        return intLastGood;
    }

    /// <summary>
    /// A match in progress whose contents cannot be read yet: only when it started is known.
    /// The start comes from the header, else from the file name, else from the file's creation time.
    /// </summary>
    public static MatchRecord BuildLockedRecord(FileInfo objFileInfo, ReplayHeader? objHeader)
    {
        var dtStarted = objHeader?.Timestamp ?? objFileInfo.CreationTime;
        var objNameMatch = System.Text.RegularExpressions.Regex.Match(objFileInfo.Name, @"(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2})");
        if (objHeader?.Timestamp == null && objNameMatch.Success)
        {
            var arrParts = objNameMatch.Groups.Values.Skip(1).Select(objGroup => int.Parse(objGroup.Value)).ToArray();
            dtStarted = new DateTime(arrParts[0], arrParts[1], arrParts[2], arrParts[3], arrParts[4], arrParts[5]);
        }

        return new MatchRecord
        {
            File = objFileInfo.Name,
            FileSize = objFileInfo.Length,
            FileModifiedUtc = objFileInfo.LastWriteTimeUtc,
            ParserVersion = PARSER_VERSION,
            StartedAt = DateTime.SpecifyKind(dtStarted, DateTimeKind.Unspecified),
            IsRecording = true,
            IsLocked = true
        };
    }

    /// <summary>
    /// Cheap look at a file the game is writing. Returns a locked record when its contents cannot be
    /// read yet (the game holds the file, or the key is not in the header), or null when a full read is worth it.
    /// </summary>
    public static MatchRecord? TryReadLocked(FileInfo objFileInfo, out string strReason)
    {
        strReason = "";
        try
        {
            var arrStart = new byte[4096];
            using (var objFile = new FileStream(objFileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var intRead = objFile.ReadAtLeast(arrStart, arrStart.Length, throwOnEndOfStream: false);
                Array.Resize(ref arrStart, intRead);
            }

            var objHeader = ReadHeader(arrStart);
            if (objHeader == null)
            {
                strReason = "todavía no hay cabecera";
                return BuildLockedRecord(objFileInfo, null);
            }
            if (objHeader is { IsLive: true, IsEncrypted: true, HasKey: false })
            {
                strReason = "Fortnite no escribe la clave de la repetición hasta que termina la partida";
                return BuildLockedRecord(objFileInfo, objHeader);
            }
            return null;
        }
        catch (IOException objError)
        {
            strReason = $"Fortnite no deja abrir el archivo mientras lo escribe ({objError.Message})";
            return BuildLockedRecord(objFileInfo, null);
        }
        catch (UnauthorizedAccessException objError)
        {
            strReason = $"sin permiso para abrir el archivo ({objError.Message})";
            return BuildLockedRecord(objFileInfo, null);
        }
    }

    /// <summary>Bump when the extracted data changes, so cached matches are parsed again.</summary>
    public const int PARSER_VERSION = 6;

    public static MatchRecord ParseFile(string strPath)
    {
        var objFileInfo = new FileInfo(strPath);

        // The game may still hold the file open, so ask for a fully shared read
        byte[] arrBytes;
        using (var objFile = new FileStream(strPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            arrBytes = new byte[objFile.Length];
            objFile.ReadExactly(arrBytes);
        }

        var objHeader = ReadHeader(arrBytes);
        var bRecording = objHeader?.IsLive == true;
        if (objHeader is { IsLive: true, IsEncrypted: true })
        {
            if (!objHeader.HasKey)
            {
                return BuildLockedRecord(objFileInfo, objHeader);
            }
            // The key is there, so the contents can be read. The library refuses files flagged
            // as live, so clear the flag in our copy of the bytes.
            arrBytes.AsSpan(objHeader.LiveFlagAt, 4).Clear();
        }

        // A replay still being recorded can end in the middle of a chunk: read only the complete ones
        var intUsable = FindEndOfCompleteChunks(arrBytes, objHeader);

        var objReader = new TrackerReplayReader();
        using var objStream = new MemoryStream(arrBytes, 0, intUsable, writable: false);
        var objReplay = objReader.ReadReplay(objStream);
        objReader.ResolvePending();

        var objRecord = new MatchRecord
        {
            File = objFileInfo.Name,
            FileSize = objFileInfo.Length,
            FileModifiedUtc = objFileInfo.LastWriteTimeUtc,
            ParserVersion = PARSER_VERSION,
            // The game stamps replays with the PC's local clock (same as the file name), so no time zone is attached
            StartedAt = DateTime.SpecifyKind(objReplay.Info.Timestamp, DateTimeKind.Unspecified),
            // The header of a replay in progress does not know its length yet; the last frame does
            DurationSec = Math.Max(objReplay.Info.LengthInMs / 1000.0, double.IsFinite(objReader.LastFrameTime) ? objReader.LastFrameTime : 0),
            IsRecording = bRecording,
            Build = objReplay.Header?.Branch,
            Playlist = objReplay.GameData?.CurrentPlaylist,
            SessionId = objReplay.GameData?.GameSessionId,
            Position = objReplay.TeamStats is null ? null : (int)objReplay.TeamStats.Position,
            TotalPlayers = objReplay.TeamStats is null ? null : (int)objReplay.TeamStats.TotalPlayers
        };

        if (objReplay.Stats is { } objStats)
        {
            objRecord.Stats = new MatchStats
            {
                Eliminations = (int)objStats.Eliminations,
                Assists = (int)objStats.Assists,
                Accuracy = float.IsFinite(objStats.Accuracy) ? Math.Round(objStats.Accuracy, 4) : null,
                DamageDealt = (int)objStats.DamageToPlayers,
                DamageTaken = (int)objStats.DamageTaken,
                DamageToStructures = (int)objStats.DamageToStructures,
                Revives = (int)objStats.Revives,
                MaterialsGathered = (int)objStats.MaterialsGathered,
                // The replay stores the distance in centimetres
                DistanceKm = Math.Round(objStats.TotalTraveled / 100000.0, 2)
            };
        }

        // Owner: the game state names the recording player; fall back to the library's own flag
        if (objReader.RecorderPlayerState is { } intRecorder && objReader.PlayerStateToId.TryGetValue(intRecorder, out var strRecorderId))
        {
            objRecord.DetectedOwnerId = strRecorderId;
        }
        objRecord.DetectedOwnerId ??= objReplay.PlayerData?.FirstOrDefault(objPlayer => objPlayer.IsReplayOwner)?.EpicId?.ToUpperInvariant();

        foreach (var objPlayer in objReplay.PlayerData ?? [])
        {
            var strId = objPlayer.PlayerId;
            if (string.IsNullOrEmpty(strId))
            {
                continue;
            }

            objRecord.Players.Add(new PlayerInfo
            {
                Id = strId.ToUpperInvariant(),
                Name = string.IsNullOrWhiteSpace(objPlayer.PlayerName) ? null : objPlayer.PlayerName,
                Team = objPlayer.TeamIndex,
                IsBot = objPlayer.IsBot,
                Kills = objPlayer.Kills is null ? null : (int)objPlayer.Kills,
                Platform = string.IsNullOrWhiteSpace(objPlayer.Platform) ? null : objPlayer.Platform,
                Level = objPlayer.Level,
                ThankedBusDriver = objPlayer.HasThankedBusDriver == true,
                Reboots = objPlayer.RebootCounter is null ? null : (int)objPlayer.RebootCounter
            });
        }

        foreach (var objOpening in objReader.Openings)
        {
            if (objReader.PawnToPlayerState.TryGetValue(objOpening.PawnGuid, out var intPlayerState)
                && objReader.PlayerStateToId.TryGetValue(intPlayerState, out var strPlayerId))
            {
                objRecord.Openings.Add(new Opening
                {
                    Time = double.IsFinite(objOpening.Time) ? Math.Round(objOpening.Time, 2) : 0,
                    ContainerClass = objOpening.ContainerClass,
                    PlayerId = strPlayerId,
                    Method = objOpening.Method
                });
            }
        }

        foreach (var objElimination in objReplay.Eliminations)
        {
            double? dblDistance = null;
            if (objElimination.IsValidLocation && objElimination.Distance is { } dblCentimetres && double.IsFinite(dblCentimetres))
            {
                dblDistance = Math.Round(dblCentimetres / 100.0, 1);
            }

            objRecord.Eliminations.Add(new Elimination
            {
                Time = (objElimination.Info?.StartTime ?? 0) / 1000.0,
                Killer = (objElimination.Eliminator ?? "").ToUpperInvariant(),
                Victim = (objElimination.Eliminated ?? "").ToUpperInvariant(),
                IsKnock = objElimination.Knocked,
                DistanceM = dblDistance
            });
        }

        return objRecord;
    }
}
