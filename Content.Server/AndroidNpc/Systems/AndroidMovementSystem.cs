// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.Components;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared.Movement.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.GameObjects;
using System.Numerics;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Post-processes NPC steering for androids to apply the right movement speed:
/// walk while working, idle or on break; run while following an order or fleeing.
/// Runs after the steering system has computed a movement vector but before physics applies it.
/// </summary>
public sealed class AndroidMovementSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        UpdatesAfter.Add(typeof(NPCSteeringSystem));
        UpdatesBefore.Add(typeof(SharedPhysicsSystem));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AndroidComponent, NPCSteeringComponent, InputMoverComponent>();

        while (query.MoveNext(out var uid, out var android, out _, out var mover))
        {
            var shouldRun = android.Status is AndroidStatus.Fleeing or AndroidStatus.FollowingOrder;

            // Androids also sprint when they are actively hunting or pulling a pest so they can
            // catch a fleeing mouse or get to a safe disposal area quickly.
            if (!shouldRun &&
                EntityManager.TryGetComponent<AndroidInventoryComponent>(uid, out var inventory) &&
                inventory.PestTarget is { Valid: true } pest &&
                !EntityManager.Deleted(pest))
            {
                shouldRun = true;
            }

            // Re-route the steering vector into the correct walk/run input bucket.
            var desired = mover.CurTickSprintMovement;

            if (shouldRun)
            {
                mover.CurTickSprintMovement = desired;
                mover.CurTickWalkMovement = Vector2.Zero;
            }
            else
            {
                mover.CurTickWalkMovement = desired;
                mover.CurTickSprintMovement = Vector2.Zero;
            }

            // Toggle the default sprint flag so footstep sounds and any sprint-based logic match.
            if (mover.DefaultSprinting != shouldRun)
            {
                mover.DefaultSprinting = shouldRun;
                Dirty(uid, mover);
            }
        }
    }
}
