// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.AndroidNpc.Systems;
using Content.Shared._Shitmed.Body.Part;
using Content.Shared._Shitmed.Cybernetics;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Humanoid;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// In-game integration tests verifying that androids spawn with randomized cybernetic body parts.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidSystem))]
public sealed class AndroidCyberneticTests
{
    [Test]
    public async Task AndroidSpawns_AlwaysHasCyberneticHead()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = server.EntMan.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var bodySystem = server.EntMan.System<SharedBodySystem>();
            var foundHead = false;

            foreach (var (partId, part) in bodySystem.GetBodyChildren(android))
            {
                if (part.PartType != BodyPartType.Head)
                    continue;

                foundHead = true;
                Assert.That(part.BaseLayerId, Is.EqualTo("MobCyberneticBishopHead"), "Android head should always be cybernetic");
                Assert.That(part.PartComposition, Is.EqualTo(BodyPartComposition.Inorganic), "Android head should be inorganic");
                Assert.That(server.EntMan.HasComponent<CyberneticsComponent>(partId), Is.True, "Android head should have CyberneticsComponent");

                var hasCyberEyes = false;
                foreach (var (organId, organ) in bodySystem.GetPartOrgans(partId, part))
                {
                    if (organ.SlotId != "eyes")
                        continue;

                    hasCyberEyes = true;
                    var meta = server.EntMan.GetComponent<MetaDataComponent>(organId);
                    Assert.That(meta.EntityPrototype?.ID, Is.EqualTo("BasicCyberneticEyes"), "Android eyes should be cybernetic");
                }

                Assert.That(hasCyberEyes, Is.True, "Android head should contain cybernetic eyes");
            }

            Assert.That(foundHead, Is.True, "Android should have a head part");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawns_CyberneticCompositionAroundSeventyPercent()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        const int androidCount = 30;
        var bodySystem = server.EntMan.System<SharedBodySystem>();
        var totalParts = 0;
        var cyberneticParts = 0;

        for (var i = 0; i < androidCount; i++)
        {
            EntityUid android = default;

            await server.WaitAssertion(() =>
            {
                android = server.EntMan.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
            });

            await server.WaitRunTicks(5);

            await server.WaitAssertion(() =>
            {
                foreach (var (partId, part) in bodySystem.GetBodyChildren(android))
                {
                    if (part.ToHumanoidLayers() is not { } layer)
                        continue;

                    totalParts++;

                    if (part.PartComposition == BodyPartComposition.Inorganic
                        || server.EntMan.HasComponent<CyberneticsComponent>(partId))
                    {
                        cyberneticParts++;
                    }
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(totalParts, Is.GreaterThan(0), "Should have counted body parts");
            var ratio = (float) cyberneticParts / totalParts;
            Assert.That(ratio, Is.GreaterThanOrEqualTo(0.55f).And.LessThanOrEqualTo(0.85f),
                $"Cybernetic part ratio should be around 70%, got {ratio:P} ({cyberneticParts}/{totalParts})");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AndroidSpawns_CyberneticOrgansReplaced()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();

        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = server.EntMan.SpawnEntity("MobAndroidJanitor", testMap.MapCoords);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var bodySystem = server.EntMan.System<SharedBodySystem>();
            var expectedOrgans = new Dictionary<string, string>
            {
                { "eyes", "BasicCyberneticEyes" },
                { "heart", "BasicCyberneticHeart" },
                { "lungs", "BasicCyberneticLungs" },
                { "liver", "BasicCyberneticLiver" }
            };

            foreach (var (partId, part) in bodySystem.GetBodyChildren(android))
            {
                foreach (var (organId, organ) in bodySystem.GetPartOrgans(partId, part))
                {
                    if (!expectedOrgans.TryGetValue(organ.SlotId, out var expectedProto))
                        continue;

                    var meta = server.EntMan.GetComponent<MetaDataComponent>(organId);
                    Assert.That(meta.EntityPrototype?.ID, Is.EqualTo(expectedProto),
                        $"Android {organ.SlotId} organ should be {expectedProto}");
                }
            }
        });

        await pair.CleanReturnAsync();
    }
}
