// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using Content.Shared._Starlight.CollectiveMind;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;
using Robust.Shared.GameObjects;

namespace Content.Shared._Starlight.CollectiveMind;

public sealed class CollectiveMindUpdateSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IComponentFactory _componentFactory = default!;
    [Dependency] private readonly TagSystem _tag = default!;

    private readonly ConcurrentDictionary<string, int> _currentId = new(); // Harvest - per instance and thread-safe

    public void UpdateCollectiveMind(EntityUid uid, CollectiveMindComponent collective)
    {
        foreach (var prototype in _prototypeManager.EnumeratePrototypes<CollectiveMindPrototype>())
        {
            bool hasChannel = collective.Channels.Contains(prototype.ID);
            bool alreadyAdded = collective.Minds.ContainsKey(prototype.ID);
            if (hasChannel && !alreadyAdded)
            {
                var id = _currentId.AddOrUpdate(prototype.ID, 1, (_, current) => current + 1);
                collective.Minds.Add(prototype.ID, id);
            }
            else if (!hasChannel && alreadyAdded)
            {
                collective.Minds.Remove(prototype.ID);
            }
        }
    }
}
