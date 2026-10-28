// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Server.NPC.Pathfinding;
using Content.Server.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree;

/// <summary>
/// Services shared by every behavior tree node for one android and one tick. Persistent state lives in
/// the android's components; the blackboard only bundles systems, data and small helpers.
/// </summary>
public sealed class AndroidBlackboard
{
    public EntityUid Owner { get; }
    public IEntityManager EntityManager { get; }
    public PathfindingSystem Pathfinding { get; }
    public NPCSteeringSystem Steering { get; }
    public SharedTransformSystem Transform { get; }
    public IGameTiming Timing { get; }
    public IRobustRandom Random { get; }
    public IBlacklistManager Blacklist { get; }
    public ClaimedTargetsSystem Claims { get; }
    public IPrototypeManager Prototype { get; }
    public ISawmill Log { get; }

    /// <summary>
    /// Optional trace of the nodes evaluated this tick, filled only while an admin inspects the android.
    /// </summary>
    public List<string>? Trace { get; set; }

    /// <summary>
    /// Nesting depth of the composite currently being traced.
    /// </summary>
    public int TraceDepth { get; set; }

    public AndroidNpcConfigPrototype Config => Prototype.AndroidConfig();
    public AndroidOrderFeedbackPrototype Feedback => Prototype.AndroidFeedback();

    private AndroidBlackboard(EntityUid owner, IEntityManager entityManager, IBlacklistManager blacklist)
    {
        Owner = owner;
        EntityManager = entityManager;
        Blacklist = blacklist;
        Pathfinding = entityManager.System<PathfindingSystem>();
        Steering = entityManager.System<NPCSteeringSystem>();
        Transform = entityManager.System<SharedTransformSystem>();
        Claims = entityManager.System<ClaimedTargetsSystem>();
        Timing = IoCManager.Resolve<IGameTiming>();
        Random = IoCManager.Resolve<IRobustRandom>();
        Prototype = IoCManager.Resolve<IPrototypeManager>();
        Log = IoCManager.Resolve<ILogManager>().GetSawmill("AndroidNpc");
    }

    /// <summary>
    /// Creates the blackboard for one android. Must be called on the game thread.
    /// </summary>
    public static AndroidBlackboard Create(IEntityManager entityManager, EntityUid owner, IBlacklistManager blacklist)
    {
        return new AndroidBlackboard(owner, entityManager, blacklist);
    }

    public T System<T>() where T : IEntitySystem => EntityManager.System<T>();

    /// <summary>
    /// Coordinates of an existing entity, or null if it is missing or deleted.
    /// </summary>
    public EntityCoordinates? CoordinatesOf(EntityUid? target)
    {
        if (target is not { Valid: true } uid ||
            EntityManager.Deleted(uid) ||
            !EntityManager.TryGetComponent<TransformComponent>(uid, out var xform))
        {
            return null;
        }

        return xform.Coordinates;
    }

    /// <summary>
    /// Returns true if the named behavior branch is on a post-failure cooldown.
    /// Expired cooldowns are cleaned up when checked.
    /// </summary>
    public bool IsBranchOnCooldown(string branch)
    {
        if (!EntityManager.TryGetComponent<AndroidComponent>(Owner, out var android))
            return false;

        if (!android.BranchCooldowns.TryGetValue(branch, out var cooldown))
            return false;

        if (Timing.CurTime >= cooldown)
        {
            android.BranchCooldowns.Remove(branch);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts the named behavior branch on cooldown for the configured duration.
    /// </summary>
    public void SetBranchCooldown(string branch)
    {
        if (!EntityManager.TryGetComponent<AndroidComponent>(Owner, out var android))
            return;

        android.BranchCooldowns[branch] = Timing.CurTime + TimeSpan.FromSeconds(Config.BranchFailureCooldownSeconds);
    }
}
