using Content.Goobstation.Shared.Emoting;
using Content.Shared._Harvest.Clown;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.EntitySystems;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.Clown;

public sealed class ClownFartSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private readonly HashSet<Entity<BloodstreamComponent>> _targets = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClownFartComponent, PostFartEvent>(OnPostFart);
    }

    private void OnPostFart(Entity<ClownFartComponent> ent, ref PostFartEvent args)
    {
        // A fart next to a bible gibs the farter.
        if (TerminatingOrDeleted(ent) || _timing.CurTime < ent.Comp.NextLaughter)
            return;

        ent.Comp.NextLaughter = _timing.CurTime + ent.Comp.Cooldown;

        _targets.Clear();
        _lookup.GetEntitiesInRange(_transform.GetMapCoordinates(ent), ent.Comp.Range, _targets);
        foreach (var target in _targets)
        {
            if (target.Owner == ent.Owner
                || !_solutions.TryGetInjectableSolution(target.Owner, out var solution, out _))
                continue;

            _solutions.TryAddReagent(solution.Value, ent.Comp.Reagent, ent.Comp.Amount);
        }
    }
}
