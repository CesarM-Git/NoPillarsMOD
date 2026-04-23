using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Transports;
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
/// Three patches:
///   1. Proto: MaxPillarSupportRadius = MaxValue → NeedsPillars = false (no auto-placement)
///   2. Command: Injects a cache handler for RemoveTransportPillarCmd that skips IsPillarRedundant
///      and clears the support-check queue to prevent transport collapse
///   3. UI: Forces m_canRemove = true on TransportPillarBuildController via ReadGameStateFrequent
///      (same thread as simUpdate) so the highlight shows yellow and clicks are allowed
///
/// Patches 2 and 3 are deferred to InitState so they run AFTER InstantiateAllAndLock().
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
    }

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

    // ── 2. Cache TransportsManager support-check queue ──────────────────

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

    // ── 3. Command handler patch (action cache) ─────────────────────────

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

    // ── 4. UI patch (bypass IsPillarRedundant gate) ─────────────────────

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

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose()
    {
        if (m_simLoopEvents != null && m_forceCanRemoveAction != null)
            m_simLoopEvents.ReadGameStateFrequent.RemoveNonSaveable(this, m_forceCanRemoveAction);
    }
}
