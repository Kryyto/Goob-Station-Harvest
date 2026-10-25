// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Atmos;
using Content.Shared.StatusEffectNew;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.StatusEffects;

/// <summary>
/// Removes the status effect when the entity is ignited.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RemoveOnIgniteStatusEffectComponent : Component
{
    /// <summary>
    /// The status effect entity prototype to remove when ignited.
    /// </summary>
    [DataField(required: true)]
    public EntProtoId EffectProto = default!;
}

public sealed partial class RemoveOnIgniteStatusEffectSystem : EntitySystem
{
    [Dependency] private StatusEffectsSystem _statusEffects = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RemoveOnIgniteStatusEffectComponent, IgnitedEvent>(OnIgnited);
    }

    private void OnIgnited(Entity<RemoveOnIgniteStatusEffectComponent> ent, ref IgnitedEvent args)
    {
        _statusEffects.TryRemoveStatusEffect(ent.Owner, ent.Comp.EffectProto);
    }
}
