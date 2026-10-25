// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading.Tasks;
using Content.Goobstation.Common.CCVar;
using Content.IntegrationTests.Pair;
using Robust.Shared.Configuration;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// Test pairs for android tests: the pool disables pathfinding for every test
/// (<see cref="GoobCVars.DisablePathfinding"/>), and androids need it to move.
/// The pool reverts the cvar when the pair is returned.
/// </summary>
public static class AndroidTestPool
{
    public static async Task<TestPair> GetServerClient(PoolSettings? settings = null)
    {
        var pair = await PoolManager.GetServerClient(settings);
        await pair.Server.WaitPost(() =>
            pair.Server.ResolveDependency<IConfigurationManager>().SetCVar(GoobCVars.DisablePathfinding, false));
        return pair;
    }
}
