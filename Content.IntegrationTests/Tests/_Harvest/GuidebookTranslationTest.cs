// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Client._Harvest.Guidebook;
using Content.Client.Guidebook;
using Content.Client.Guidebook.Richtext;
using Content.Shared.Guidebook;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._Harvest;

[TestFixture]
[TestOf(typeof(GuidebookTranslationSystem))]
public sealed class GuidebookTranslationTest
{
    private static readonly ResPath TranslationRoot = new("/ServerInfo/Guidebook/_Harvest/Translations");

    /// <summary>
    /// Every translated guide replaces an existing guide entry and parses.
    /// </summary>
    [Test]
    public async Task TranslationsReplaceGuides()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var client = pair.Client;
        await client.WaitIdleAsync();

        var protoMan = client.ResolveDependency<IPrototypeManager>();
        var resMan = client.ResolveDependency<IResourceManager>();
        var parser = client.ResolveDependency<DocumentParsingManager>();

        var translations = resMan.ContentFindFiles(TranslationRoot)
            .Where(p => p.Extension == "xml")
            .ToHashSet();
        var used = protoMan.EnumeratePrototypes<GuideEntryPrototype>()
            .Where(p => translations.Contains(p.Text))
            .ToList();

        Assert.That(translations, Is.Not.Empty);
        Assert.That(used.Select(p => p.Text).ToHashSet(), Is.EquivalentTo(translations),
            "Each translation must mirror the path of an existing guide.");

        foreach (var proto in used)
        {
            await client.WaitAssertion(() =>
            {
                using var reader = resMan.ContentFileReadText(proto.Text);
                Assert.That(parser.TryAddMarkup(new Document(), reader.ReadToEnd()), $"Failed to parse guidebook: {proto.Id}");
            });

            // Avoid styleguide update limit
            await client.WaitRunTicks(1);
        }

        await pair.CleanReturnAsync();
    }
}
