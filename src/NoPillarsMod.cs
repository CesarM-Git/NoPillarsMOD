using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Console;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Lifts;
using Mafi.Core.Factory.Sorters;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.GameLoop;
using Mafi.Core.Game;
using Mafi.Core.Input;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;

namespace NoPillarsMod;

/// <summary>
/// Mod that disables pillar requirements for elevated transports and allows
/// free removal of all pillars.
///
/// Patches:
///   1. Transport protos: MaxPillarSupportRadius = MaxValue → NeedsPillars = false (no auto-placement).
///   2. Layout entity protos (ZipperProto / LiftProto / SorterProto): strip the
///      LayoutTileConstraint.UsingPillar flag from every tile in their EntityLayout so the
///      ILayoutEntityProtoWithElevationValidator skips both the pillar-build check (CanAdd)
///      and the actual pillar placement (PrepareForAdd). The EntityLayout's cached
///      OccupiedTileRelative arrays are reset so subsequent calls recompute with the new
///      constraint set.
///   3. Height ceilings: Widen Layout.PlacementHeightRange on each ZipperProto / LiftProto /
///      SorterProto to (0, 30) so they can be placed at any height (the slider in
///      LayoutEntityPreview reads this). Also keep StaticEntityProto.VehicleGoalHeightAllowedRange
///      in sync so trucks can still path to elevated instances. TransportPillarProto.MAX_PILLAR_HEIGHT
///      is raised to 15 (matching the TransportPathFinder's Z-range cap) so the
///      TransportBuildController's transportUp() cursor stops getting clamped at 5 — pipes and
///      belts can now be raised further with the up-arrow.
///   4. Command: Injects a cache handler for RemoveTransportPillarCmd that skips IsPillarRedundant
///      and clears the support-check queue to prevent transport collapse.
///   5. UI: Forces m_canRemove = true on TransportPillarBuildController via ReadGameStateFrequent
///      (same thread as simUpdate) so the highlight shows yellow and clicks are allowed.
///
/// Patches 4 and 5 are deferred to InitState so they run AFTER InstantiateAllAndLock().
/// </summary>
public sealed class NoPillarsMod : IMod, IDisposable
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    private DependencyResolver m_resolver;
    private EntitiesManager m_entitiesManager;
    private IGameLoopEvents m_gameLoopEvents;
    private ISimLoopEvents m_simLoopEvents;

    // For clearing the support-check queue after pillar removal
    private object m_transportsManager;
    private FieldInfo m_supportCheckQueueField;

    // UI patch state
    private object m_uiController;
    private FieldInfo m_canRemoveField;
    private Action m_forceCanRemoveAction;

    public NoPillarsMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) { }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded)
    {
        PatchAllTransportProtos(protosDb);
        PatchLayoutEntityProtos(protosDb);
        PatchHeightCeilings(protosDb);
    }

    // Height-ceiling constants — see PatchHeightCeilings.
    private const int LAYOUT_PLACEMENT_HEIGHT_MAX = 30;
    private const int MAX_PILLAR_HEIGHT_OVERRIDE  = 15;

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        m_resolver = resolver;

        try
        {
            m_entitiesManager = resolver.Resolve<EntitiesManager>();
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to resolve EntitiesManager: {ex.Message}");
            return;
        }

        try
        {
            m_gameLoopEvents = resolver.Resolve<IGameLoopEvents>();
            m_simLoopEvents = resolver.Resolve<ISimLoopEvents>();
            m_gameLoopEvents.RegisterInitState(this, OnInitState);
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to register InitState: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs after InstantiateAllAndLock() — patches stick from here.
    /// </summary>
    private void OnInitState()
    {
        CacheSupportCheckQueue();
        PatchActionCache();
        PatchUIController();
        RegisterConsoleCommands();
        Log.Info("NoPillarsMod: All patches applied.");
    }

    // ── 1. Proto patch ──────────────────────────────────────────────────

    private static void PatchAllTransportProtos(ProtosDb protosDb)
    {
        var radiusField = typeof(TransportProto).GetField("MaxPillarSupportRadius",
            BindingFlags.Public | BindingFlags.Instance);
        var groundField = typeof(TransportProto).GetField("NeedsPillarsAtGround",
            BindingFlags.Public | BindingFlags.Instance);

        if (radiusField == null) { Log.Error("NoPillarsMod: MaxPillarSupportRadius field not found!"); return; }
        if (groundField == null) Log.Warning("NoPillarsMod: NeedsPillarsAtGround field not found.");

        int count = 0;
        foreach (TransportProto proto in protosDb.All<TransportProto>())
        {
            bool had = proto.NeedsPillars;
            radiusField.SetValue(proto, RelTile1i.MaxValue);
            groundField?.SetValue(proto, false);
            if (had) count++;
        }
        Log.Info($"NoPillarsMod: Disabled pillars on {count} transport proto(s).");
    }

    // ── 2. Proto patch — strip UsingPillar from balancer/lift/sorter layouts ──

    /// <summary>
    /// Zippers (balancers), lifts and sorters are LayoutEntityProtos, not TransportProtos.
    /// Their pillar requirement is encoded per-tile in <see cref="EntityLayout.LayoutTiles"/>
    /// via the <see cref="LayoutTileConstraint.UsingPillar"/> flag.
    /// <see cref="ILayoutEntityProtoWithElevationValidator"/> reads this flag (and the proto's
    /// <c>CanBeElevated</c> property) and, when set, both validates a pillar can be built and
    /// then calls <c>TransportsManager.BuildOrExtendPillarNoChecks</c> from <c>PrepareForAdd</c>.
    ///
    /// To make balancers / lifts / sorters need no pillar support and place no pillars on
    /// construction, we strip the <c>UsingPillar</c> flag from every layout tile of every
    /// matching proto. <c>CanBeElevated</c> is left at <c>true</c> so pillars can still pass
    /// through these entities and so the rest of the elevation pipeline keeps working.
    ///
    /// LayoutTile is a readonly struct, so each tile is reconstructed through the public
    /// constructor and written back into the underlying T[] of the
    /// <see cref="ImmutableArray{T}"/> via reflection on its private <c>m_items</c> field.
    /// The EntityLayout's per-rotation <c>m_occupiedTilesRelativeCache</c> is then reset so
    /// the next access recomputes <see cref="OccupiedTileRelative"/> arrays with the stripped
    /// constraint set.
    /// </summary>
    private static void PatchLayoutEntityProtos(ProtosDb protosDb)
    {
        // ImmutableArray<LayoutTile>.m_items — the backing T[] we will mutate in place.
        var itemsField = typeof(ImmutableArray<LayoutTile>).GetField("m_items",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (itemsField == null)
        {
            Log.Error("NoPillarsMod: ImmutableArray<LayoutTile>.m_items field not found!");
            return;
        }

        // EntityLayout.m_occupiedTilesRelativeCache — array of cached OccupiedTileRelative
        // arrays (one per Transform90RotFlip). We blank these so the next access recomputes
        // with the new constraint set.
        var cacheField = typeof(EntityLayout).GetField("m_occupiedTilesRelativeCache",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (cacheField == null)
        {
            Log.Error("NoPillarsMod: EntityLayout.m_occupiedTilesRelativeCache field not found!");
            return;
        }

        int zipperCount = 0, liftCount = 0, sorterCount = 0;
        int tilesStripped = 0;

        foreach (var proto in protosDb.All<ZipperProto>())
            if (StripUsingPillarFromLayout(proto, proto.Layout, itemsField, cacheField, ref tilesStripped))
                zipperCount++;

        foreach (var proto in protosDb.All<LiftProto>())
            if (StripUsingPillarFromLayout(proto, proto.Layout, itemsField, cacheField, ref tilesStripped))
                liftCount++;

        foreach (var proto in protosDb.All<SorterProto>())
            if (StripUsingPillarFromLayout(proto, proto.Layout, itemsField, cacheField, ref tilesStripped))
                sorterCount++;

        Log.Info($"NoPillarsMod: Stripped UsingPillar from layouts — " +
                 $"{zipperCount} zipper(s), {liftCount} lift(s), {sorterCount} sorter(s); " +
                 $"{tilesStripped} tile(s) modified.");
    }

    /// <summary>
    /// Strips <see cref="LayoutTileConstraint.UsingPillar"/> from every tile of the given
    /// EntityLayout and invalidates the per-rotation OccupiedTileRelative cache. Returns
    /// true if any tile was actually changed.
    /// </summary>
    private static bool StripUsingPillarFromLayout(
        object protoForLog,
        EntityLayout layout,
        FieldInfo itemsField,
        FieldInfo cacheField,
        ref int tilesStripped)
    {
        if (layout == null) return false;

        // Box the struct ImmutableArray<LayoutTile> so reflection can reach its m_items field.
        object boxedTiles = layout.LayoutTiles;
        var tiles = itemsField.GetValue(boxedTiles) as LayoutTile[];
        if (tiles == null)
        {
            Log.Warning($"NoPillarsMod: Layout for '{protoForLog}' has no backing tile array.");
            return false;
        }

        bool changed = false;
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            if ((tile.Constraint & LayoutTileConstraint.UsingPillar) == LayoutTileConstraint.None)
                continue;

            var newConstraint = tile.Constraint & ~LayoutTileConstraint.UsingPillar;
            tiles[i] = new LayoutTile(
                tile.Coord,
                tile.SourceStrIndex,
                tile.OccupiedThickness,
                tile.TerrainHeight,
                tile.MinTerrainHeight,
                tile.MaxTerrainHeight,
                newConstraint,
                tile.TerrainMaterialProto,
                tile.TileSurfaceProto,
                tile.HasVehicleSurface);
            changed = true;
            tilesStripped++;
        }

        if (changed)
        {
            // The cache stores ImmutableArray<OccupiedTileRelative>[8] (one per rotation/flip).
            // Reset each slot to default so IsValid is false and the next call recomputes
            // OccupiedTileRelative arrays with the stripped constraints.
            if (cacheField.GetValue(layout) is Array cache)
            {
                for (int i = 0; i < cache.Length; i++)
                    cache.SetValue(default(ImmutableArray<OccupiedTileRelative>), i);
            }
        }

        return changed;
    }

    // ── 3. Height ceilings — lift placement caps ────────────────────────

    /// <summary>
    /// Two height ceilings are in play in the vanilla game:
    ///
    /// (a) <see cref="EntityLayout.PlacementHeightRange"/> on ZipperProto / LiftProto /
    ///     SorterProto. The base game sets this to (0, MAX_PILLAR_HEIGHT - 1) = (0, 5) via
    ///     EntityLayoutParams.CustomPlacementRange when those protos are registered.
    ///     <see cref="LayoutEntityPreview"/> reads it to drive the placement-height slider
    ///     and to clamp the placement cursor. Widening it to (0, 30) lets the player place
    ///     these entities anywhere up to 30 tiles above ground.
    ///
    ///     The <see cref="StaticEntityProto.VehicleGoalHeightAllowedRange"/> nullable is
    ///     populated from the same range at construction time. We keep it in sync so trucks
    ///     can still path-find to elevated instances.
    ///
    /// (b) <see cref="TransportPillarProto.MAX_PILLAR_HEIGHT"/> (public static readonly).
    ///     <see cref="Mafi.Unity.Ui.Controllers.TransportBuildController.transportUp"/> clamps
    ///     the placement cursor to <c>MAX_PILLAR_HEIGHT - 1</c>. Raising MAX_PILLAR_HEIGHT to
    ///     15 lifts that clamp to 14 (matching the TransportPathFinder's hard Z-range cap of
    ///     16 / start-Z 8 / +7 reachable). Pipes and belts can now be raised further with the
    ///     up-arrow.
    ///
    /// All three fields are <c>readonly</c>; we set them via reflection. The protos' layouts
    /// were already baked from the original MAX_PILLAR_HEIGHT at registration time, so we
    /// must patch each layout's <c>PlacementHeightRange</c> explicitly — bumping
    /// MAX_PILLAR_HEIGHT alone does not propagate.
    /// </summary>
    private static void PatchHeightCeilings(ProtosDb protosDb)
    {
        PatchMaxPillarHeight();
        PatchLayoutPlacementRanges(protosDb);
    }

    private static void PatchMaxPillarHeight()
    {
        var field = typeof(TransportPillarProto).GetField("MAX_PILLAR_HEIGHT",
            BindingFlags.Public | BindingFlags.Static);

        if (field == null)
        {
            Log.Error("NoPillarsMod: TransportPillarProto.MAX_PILLAR_HEIGHT field not found!");
            return;
        }

        try
        {
            var oldValue = (ThicknessTilesI)field.GetValue(null);
            field.SetValue(null, new ThicknessTilesI(MAX_PILLAR_HEIGHT_OVERRIDE));
            Log.Info($"NoPillarsMod: MAX_PILLAR_HEIGHT {oldValue.Value} → {MAX_PILLAR_HEIGHT_OVERRIDE}.");
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to set MAX_PILLAR_HEIGHT: {ex.Message}");
        }
    }

    private static void PatchLayoutPlacementRanges(ProtosDb protosDb)
    {
        var placementField = typeof(EntityLayout).GetField("PlacementHeightRange",
            BindingFlags.Public | BindingFlags.Instance);
        var vehicleGoalField = typeof(StaticEntityProto).GetField("VehicleGoalHeightAllowedRange",
            BindingFlags.Public | BindingFlags.Instance);

        if (placementField == null)
        {
            Log.Error("NoPillarsMod: EntityLayout.PlacementHeightRange field not found!");
            return;
        }
        if (vehicleGoalField == null)
            Log.Warning("NoPillarsMod: StaticEntityProto.VehicleGoalHeightAllowedRange field not found — trucks may fail to path to elevated instances.");

        var newRange = new ThicknessIRange(0, LAYOUT_PLACEMENT_HEIGHT_MAX);

        int zipperCount = 0, liftCount = 0, sorterCount = 0;

        foreach (var proto in protosDb.All<ZipperProto>())
            if (WidenPlacementRange(proto, proto.Layout, placementField, vehicleGoalField, newRange))
                zipperCount++;

        foreach (var proto in protosDb.All<LiftProto>())
            if (WidenPlacementRange(proto, proto.Layout, placementField, vehicleGoalField, newRange))
                liftCount++;

        foreach (var proto in protosDb.All<SorterProto>())
            if (WidenPlacementRange(proto, proto.Layout, placementField, vehicleGoalField, newRange))
                sorterCount++;

        Log.Info($"NoPillarsMod: Widened PlacementHeightRange to (0, {LAYOUT_PLACEMENT_HEIGHT_MAX}) on " +
                 $"{zipperCount} zipper(s), {liftCount} lift(s), {sorterCount} sorter(s).");
    }

    private static bool WidenPlacementRange(
        object proto,
        EntityLayout layout,
        FieldInfo placementField,
        FieldInfo vehicleGoalField,
        ThicknessIRange newRange)
    {
        if (layout == null) return false;

        try
        {
            placementField.SetValue(layout, newRange);
        }
        catch (Exception ex)
        {
            Log.Warning($"NoPillarsMod: Failed to set PlacementHeightRange on '{proto}': {ex.Message}");
            return false;
        }

        // VehicleGoalHeightAllowedRange is Nullable<ThicknessIRange>. If the proto was
        // constructed with a non-null range, mirror our new range so trucks can still reach
        // elevated instances. Leave null protos alone.
        if (vehicleGoalField != null && proto is StaticEntityProto staticProto)
        {
            var current = (ThicknessIRange?)vehicleGoalField.GetValue(staticProto);
            if (current.HasValue)
            {
                try
                {
                    vehicleGoalField.SetValue(staticProto, (ThicknessIRange?)newRange);
                }
                catch (Exception ex)
                {
                    Log.Warning($"NoPillarsMod: Failed to set VehicleGoalHeightAllowedRange on '{proto}': {ex.Message}");
                }
            }
        }

        return true;
    }

    // ── 4. Cache TransportsManager support-check queue ──────────────────

    private void CacheSupportCheckQueue()
    {
        try
        {
            m_transportsManager = m_resolver.Resolve<TransportsManager>();
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to resolve TransportsManager: {ex.Message}");
            return;
        }

        m_supportCheckQueueField = typeof(TransportsManager).GetField("m_transportsSupportCheck",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (m_supportCheckQueueField == null)
            Log.Error("NoPillarsMod: m_transportsSupportCheck field not found!");
    }

    /// <summary>
    /// Clears the support-check queue to prevent checkTransportSupportedAt from running.
    /// Called after each pillar removal to stop the game from collapsing unsupported transports.
    /// </summary>
    private void ClearSupportCheckQueue()
    {
        if (m_supportCheckQueueField == null || m_transportsManager == null) return;

        try
        {
            var queue = m_supportCheckQueueField.GetValue(m_transportsManager);
            if (queue == null) return;

            var clearMethod = queue.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
            if (clearMethod != null)
                clearMethod.Invoke(queue, null);
            else
                Log.Warning("NoPillarsMod: Clear() method not found on m_transportsSupportCheck.");
        }
        catch (Exception ex)
        {
            Log.Warning($"NoPillarsMod: Failed to clear support-check queue: {ex.Message}");
        }
    }

    // ── 5. Command handler patch (action cache) ─────────────────────────

    private void PatchActionCache()
    {
        var field = typeof(Mafi.DependencyResolver).GetField("m_factoryActionCache",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (field == null) { Log.Error("NoPillarsMod: m_factoryActionCache field not found!"); return; }

        var cache = field.GetValue(m_resolver) as ConcurrentDictionary<Type, Action<object[]>>;
        if (cache == null) { Log.Error("NoPillarsMod: Action cache is null!"); return; }

        cache[typeof(RemoveTransportPillarCmd)] = (object[] args) =>
        {
            var cmd = (RemoveTransportPillarCmd)args[0];

            if (!m_entitiesManager.TryGetEntity<TransportPillar>(cmd.PillarId, out var pillar))
            {
                Log.Warning($"NoPillarsMod: Pillar {cmd.PillarId} not found.");
                cmd.SetResultError("Pillar not found.");
                return;
            }

            m_entitiesManager.RemoveAndDestroyEntityNoChecks(pillar, EntityRemoveReason.Remove);
            ClearSupportCheckQueue();
            cmd.SetResultSuccess();
        };
    }

    // ── 6. UI patch (bypass IsPillarRedundant gate) ─────────────────────

    private void PatchUIController()
    {
        // Find the type by name — it lives in Mafi.Unity.dll which we don't reference.
        Type controllerType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            controllerType = asm.GetType("Mafi.Unity.Ui.Controllers.TransportPillarBuildController");
            if (controllerType != null) break;
        }

        if (controllerType == null)
        {
            Log.Error("NoPillarsMod: TransportPillarBuildController type not found!");
            return;
        }

        try
        {
            m_uiController = m_resolver.Resolve(controllerType);
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to resolve TransportPillarBuildController: {ex.Message}");
            return;
        }

        if (m_uiController == null)
        {
            Log.Error("NoPillarsMod: TransportPillarBuildController resolved as null!");
            return;
        }

        m_canRemoveField = controllerType.GetField("m_canRemove",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (m_canRemoveField == null)
        {
            Log.Error("NoPillarsMod: m_canRemove field not found!");
            return;
        }

        // Subscribe on the sim thread (same as simUpdate) so our override sticks
        // before the main thread reads it in InputUpdate.
        m_forceCanRemoveAction = () => m_canRemoveField.SetValue(m_uiController, true);
        m_simLoopEvents.ReadGameStateFrequent.AddNonSaveable(this, m_forceCanRemoveAction);
    }

    // ── 7. Console command — remove all pillars ───────────────────────────

    private void RegisterConsoleCommands()
    {
        try
        {
            var executor = m_resolver.Resolve<GameConsoleCommandsExecutor>();
            int count = executor.ScanObjectForConsoleCommands(this, ignoreDuplicates: true);
            Log.Info($"NoPillarsMod: Registered {count} console command(s).");
        }
        catch (Exception ex)
        {
            Log.Error($"NoPillarsMod: Failed to register console commands: {ex.Message}");
        }
    }

    [ConsoleCommand(documentation: "Removes all transport pillars from the map.")]
    private string RemoveAllPillars()
    {
        if (m_entitiesManager == null)
            return "Error: EntitiesManager not available.";

        var pillars = m_entitiesManager.GetAllEntitiesOfType<TransportPillar>().ToList();
        int total = pillars.Count;

        if (total == 0)
            return "No pillars found on the map.";

        int removed = 0;
        int failed = 0;

        foreach (var pillar in pillars)
        {
            try
            {
                m_entitiesManager.RemoveAndDestroyEntityNoChecks(pillar, EntityRemoveReason.Remove);
                ClearSupportCheckQueue();
                removed++;
            }
            catch (Exception ex)
            {
                failed++;
                Log.Warning($"NoPillarsMod: Failed to remove pillar {pillar.Id}: {ex.Message}");
            }
        }

        string result = $"Removed {removed}/{total} pillars.";
        if (failed > 0)
            result += $" ({failed} failed — check log)";

        Log.Info($"NoPillarsMod: {result}");
        return result;
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose()
    {
        if (m_simLoopEvents != null && m_forceCanRemoveAction != null)
            m_simLoopEvents.ReadGameStateFrequent.RemoveNonSaveable(this, m_forceCanRemoveAction);
    }
}
