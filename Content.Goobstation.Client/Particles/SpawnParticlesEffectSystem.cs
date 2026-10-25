// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.Particles;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Goobstation.Client.Particles;

public sealed partial class SpawnParticlesEffectSystem : SharedSpawnParticlesEffectSystem
{
    [Dependency] private ParticleSystem _particles = default!;
    [Dependency] private IRobustRandom _random = default!; // Harvest
    [Dependency] private SharedTransformSystem _transform = default!; // Harvest

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<SpawnParticlesEvent>(OnNetworkSpawnParticles);
    }

    private void OnNetworkSpawnParticles(SpawnParticlesEvent ev)
    {
        var entity = GetEntity(ev.Target);
        if (!entity.IsValid())
            return;

        for (var i = 0; i < ev.Number; i++)
        {
            SpawnSingle(ev.ParticleProto, entity, ev.Color, ev.Attached, ev.Radius);
        }
    }

    protected override void SpawnParticles(ProtoId<ParticleEffectPrototype> particleProto, EntityUid target, Color? color, bool attached, float? radius)
    {
        base.SpawnParticles(particleProto, target, color, attached, radius);

        SpawnSingle(particleProto, target, color, attached, radius);
    }

    private void SpawnSingle(ProtoId<ParticleEffectPrototype> particleProto, EntityUid target, Color? color, bool attached, float? radius)
    {
        if (radius.HasValue)
        {
            var center = _transform.GetMapCoordinates(target);
            var offset = _random.NextVector2(radius.Value);
            var coords = new MapCoordinates(center.Position + offset, center.MapId);
            _particles.CreateParticle(particleProto, coords, color);
        }
        else
        {
            _particles.CreateParticle(particleProto, target, color, attached);
        }
    }
}
