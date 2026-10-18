// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Atmos.Components;
using Content.Shared.EntityConditions;
using JetBrains.Annotations;
using Robust.Shared.Prototypes;

namespace Content.Goobstation.Shared.EntityConditions;

/// <summary>
/// Returns true if the entity is on fire (has OnFireComponent).
/// </summary>
[UsedImplicitly]
public sealed partial class OnFireConditionSystem : EntityConditionSystem<OnFireComponent, OnFireCondition>
{
    protected override void Condition(Entity<OnFireComponent> entity, ref EntityConditionEvent<OnFireCondition> args)
    {
        args.Result = true;
    }
}

/// <inheritdoc cref="EntityCondition"/>
[UsedImplicitly]
public sealed partial class OnFireCondition : EntityConditionBase<OnFireCondition>
{
    public override string EntityConditionGuidebookText(IPrototypeManager prototype) =>
        Loc.GetString("entity-condition-guidebook-on-fire", ("invert", Inverted));
}