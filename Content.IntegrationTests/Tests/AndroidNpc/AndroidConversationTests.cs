// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Services;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Speech;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// Free conversation with androids: deterministic intent/tone/language rules ported from
/// Android-generic-responses, scripted replies and the in-game effects of each intent.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidConversationSystem))]
[TestOf(typeof(AndroidIntentClassifier))]
public sealed class AndroidConversationTests
{
    private static void SetAndroidIdentity(IEntityManager sEnt, EntityUid android, string firstName)
    {
        var identity = sEnt.GetComponent<AndroidIdentityComponent>(android);
        identity.FirstName = firstName;
        identity.FullDisplayName = $"Androïde {firstName}";
        sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
    }

    [Test]
    public async Task Classifier_MatchesPythonRules()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var conversation = server.System<AndroidConversationSystem>();
            var classifier = conversation.Classifier;
            var janitor = conversation.GetIntentSet(conversation.GetConversation(AndroidJob.Janitor)!);
            var bartender = conversation.GetIntentSet(conversation.GetConversation(AndroidJob.Bartender)!);

            Assert.Multiple(() =>
            {
                Assert.That(classifier.DetectLanguage("va bosser"), Is.EqualTo("fr"));
                Assert.That(classifier.DetectLanguage("salut ça va ?"), Is.EqualTo("fr"));
                Assert.That(classifier.DetectLanguage("hello, how are you?"), Is.EqualTo("en"));
                Assert.That(classifier.DetectLanguage("xyz"), Is.EqualTo("en"), "No hint falls back to the default language");

                Assert.That(classifier.Classify("Va bosser !", "fr", janitor),
                    Is.EqualTo(new AndroidIntentMatch("work_command", AndroidIntentClassifier.PhraseSource)));
                Assert.That(classifier.Classify("Salut", "fr", janitor),
                    Is.EqualTo(new AndroidIntentMatch("greeting", AndroidIntentClassifier.PhraseSource)));
                Assert.That(classifier.Classify("There is blood all over medbay lobby!", "en", janitor),
                    Is.EqualTo(new AndroidIntentMatch("cleaning_request", AndroidIntentClassifier.KeywordSource)));
                Assert.That(classifier.Classify("je peux prendre une serpillière ?", "fr", janitor)?.Intent, Is.EqualTo("supply_request"));
                Assert.That(classifier.Classify("où est l'infirmerie ?", "fr", janitor)?.Intent, Is.EqualTo("directions"));
                Assert.That(classifier.Classify("vous avez quoi comme bières ?", "fr", bartender)?.Intent, Is.EqualTo("menu_question"),
                    "Question intents are checked before order intents");
                Assert.That(classifier.Classify("une bière s'il vous plaît", "fr", bartender)?.Intent, Is.EqualTo("drink_order"));
                Assert.That(classifier.Classify("une bière s'il vous plaît", "fr", janitor)?.Intent, Is.Not.EqualTo("drink_order"),
                    "Intents outside the job's set are skipped");
                Assert.That(classifier.Classify("the weather is nice", "en", janitor), Is.Null);

                Assert.That(classifier.DetectTone("bonjour"), Is.EqualTo(0));
                Assert.That(classifier.DetectTone("dépêche-toi, idiot"), Is.EqualTo(1));
                Assert.That(classifier.DetectTone("CLEAN THIS RIGHT NOW"), Is.EqualTo(1));
                Assert.That(classifier.DetectTone("je vais te tuer"), Is.EqualTo(2));
                Assert.That(classifier.DetectTone("clean it or I'll scrap you"), Is.EqualTo(2));

                Assert.That(classifier.FindLocation("There is blood all over medbay lobby!"), Is.EqualTo("medical"));
                Assert.That(classifier.FindLocation("y'a du vomi à la cuisine"), Is.EqualTo("kitchen"));
                Assert.That(classifier.FindLocation("y'a du vomi ici"), Is.Null);
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void Backend_ParsesDecideResponse()
    {
        const string json = """
            {"model":"THX-01","answers":{
              "intent":{"type":"choice","choice":"drink_order","probabilities":{"drink_order":0.2846,"menu_question":0.1945},
                        "confidence":0.1382,"action":{"act_probability":1.0}},
              "tone":{"type":"score","score":0.53,"legend":{"0":"calm or polite","1":"direct or impatient","2":"threatening or dangerous"},
                      "probabilities":{"0":0.62,"1":0.23,"2":0.15},"confidence":0.4,"action":{"act_probability":1.0}}},
             "detected":{"script":"latin","language":null},"latency_ms":242.97}
            """;

        Assert.That(Thx01ConversationBackend.TryParse(json, out var prediction), Is.True);
        Assert.That(prediction!.Intent, Is.EqualTo("drink_order"));
        Assert.That(prediction.IntentConfidence, Is.EqualTo(0.1382f).Within(1e-4));
        Assert.That(prediction.ToneProbabilities, Is.EqualTo(new[] { 0.62f, 0.23f, 0.15f }).Within(1e-4));

        Assert.That(Thx01ConversationBackend.TryParse("{\"detail\":\"missing or invalid bearer token\"}", out _), Is.False);
        Assert.That(Thx01ConversationBackend.TryParse("not json", out _), Is.False);
        Assert.That(new Thx01ConversationBackend("", 4f, null).Enabled, Is.False);
    }

    [Test]
    public void Backend_ToneLevelIsConservative()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Thx01ConversationBackend.ToneLevel(new[] { 0.18f, 0.12f, 0.70f }, 0.65f, 0.45f), Is.EqualTo(2));
            Assert.That(Thx01ConversationBackend.ToneLevel(new[] { 0.24f, 0.22f, 0.54f }, 0.65f, 0.45f), Is.EqualTo(0),
                "A likely-but-unsure threat must not alert security");
            Assert.That(Thx01ConversationBackend.ToneLevel(new[] { 0.30f, 0.50f, 0.20f }, 0.65f, 0.45f), Is.EqualTo(1));
            Assert.That(Thx01ConversationBackend.ToneLevel(Array.Empty<float>(), 0.65f, 0.45f), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Backend_RequestUsesJobContextAndLanguageQuestions()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var conversation = server.System<AndroidConversationSystem>();
            var bartender = conversation.GetConversation(AndroidJob.Bartender)!;
            var query = conversation.BuildQuery(bartender, "je peux avoir 1 mojito svp?", "fr");

            Assert.That(query, Is.Not.Null);
            Assert.That(query!.IntentCriteria.Keys, Is.EquivalentTo(conversation.GetIntentSet(bartender)));
            Assert.That(query.ToneCriteria, Has.Count.EqualTo(3));

            using var doc = JsonDocument.Parse(Thx01ConversationBackend.BuildRequest(query)!);
            var root = doc.RootElement;
            Assert.That(root.GetProperty("state").GetProperty("message").GetString(), Is.EqualTo("je peux avoir 1 mojito svp?"));
            Assert.That(root.GetProperty("state").GetProperty("language").GetString(), Is.EqualTo("fr"));
            Assert.That(root.GetProperty("state").TryGetProperty("role", out _), Is.True);
            Assert.That(root.GetProperty("questions").GetProperty("intent").GetProperty("type").GetString(), Is.EqualTo("choice"));
            Assert.That(root.GetProperty("questions").GetProperty("intent").GetProperty("criteria").TryGetProperty("drink_order", out _), Is.True);
            Assert.That(root.GetProperty("questions").GetProperty("tone").GetProperty("type").GetString(), Is.EqualTo("score"));
            Assert.That(root.GetProperty("questions").GetProperty("tone").GetProperty("criteria").GetArrayLength(), Is.EqualTo(3));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [Explicit("Needs a THX-01 server on http://localhost:8095")]
    public async Task Backend_LiveThx01Server()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        AndroidConversationQuery? query = null;

        await server.WaitAssertion(() =>
        {
            var conversation = server.System<AndroidConversationSystem>();
            query = conversation.BuildQuery(conversation.GetConversation(AndroidJob.Janitor)!, "can I borrow your mop?", "en");
        });

        var prediction = await new Thx01ConversationBackend("http://localhost:8095", 20f, null).PredictAsync(query!, default);
        Assert.That(prediction, Is.Not.Null);
        Assert.That(prediction!.Intent, Is.EqualTo("supply_request"));
        Assert.That(prediction.ToneProbabilities, Has.Count.EqualTo(3));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task YamlData_HasRepliesForEveryIntentToneAndLanguage()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            var conversations = protos.EnumeratePrototypes<AndroidConversationPrototype>().ToList();
            Assert.That(conversations.Select(c => c.Job), Is.EquivalentTo(Enum.GetValues<AndroidJob>()));

            foreach (var conversation in conversations)
            {
                var intents = conversation.Responses.Select(r => r.Intent).Distinct().ToList();
                // Every intent the model may pick has replies.
                foreach (var questions in conversation.BackendQuestions)
                    Assert.That(intents, Is.EquivalentTo(questions.IntentCriteria.Keys), $"{conversation.ID} {questions.Lang}");

                foreach (var lang in new[] { "en", "fr" })
                foreach (var intent in intents)
                {
                    var response = conversation.Responses.SingleOrDefault(r => r.Lang == lang && r.Intent == intent);
                    Assert.That(response, Is.Not.Null, $"{conversation.ID} {lang} {intent}");
                    Assert.That(response!.Calm, Has.Count.GreaterThanOrEqualTo(3));
                    Assert.That(response.Direct, Has.Count.GreaterThanOrEqualTo(3));
                    Assert.That(response.Threatening, Has.Count.GreaterThanOrEqualTo(3));
                }

                foreach (var intent in conversation.Effects.Keys)
                    Assert.That(intents, Does.Contain(intent), $"{conversation.ID} effect on unknown intent {intent}");

                Assert.That(conversation.BackendState, Is.Not.Empty, conversation.ID);
                foreach (var lang in new[] { "en", "fr" })
                {
                    var questions = conversation.BackendQuestions.SingleOrDefault(q => q.Lang == lang);
                    Assert.That(questions, Is.Not.Null, $"{conversation.ID} backend questions {lang}");
                    Assert.That(questions!.IntentCriteria.Keys, Is.EquivalentTo(intents), $"{conversation.ID} {lang}");
                    Assert.That(questions.ToneCriteria, Has.Count.EqualTo(3), $"{conversation.ID} {lang}");
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    private sealed record Setup(TestPair Pair, EntityUid Android, EntityUid Speaker);

    private static async Task<Setup> SpawnAndroidAndSpeaker(Vector2 speakerOffset = default)
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var sEnt = server.EntMan;

        EntityUid android = default;
        EntityUid speaker = default;

        await server.WaitAssertion(() =>
        {
            sEnt.System<AndroidConversationSystem>().SetBackend(new Thx01ConversationBackend("", 4f, null));
            android = sEnt.SpawnEntity("MobAndroidJanitor", testMap.GridCoords);
            speaker = sEnt.SpawnEntity("MobHuman", testMap.GridCoords.Offset(speakerOffset));
            SetAndroidIdentity(sEnt, android, "Gerard");
        });

        await server.WaitRunTicks(2);
        return new Setup(pair, android, speaker);
    }

    private static AndroidConversationPrototype Janitor(IEntityManager sEnt)
    {
        return sEnt.System<AndroidConversationSystem>().GetConversation(AndroidJob.Janitor)!;
    }

    [Test]
    public async Task Greeting_GetsScriptedReplyWithoutOrder()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, salut !", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("greeting"));
            Assert.That(conversation.LastSource, Is.EqualTo(AndroidIntentClassifier.PhraseSource));
            Assert.That(conversation.LastLanguage, Is.EqualTo("fr"));
            Assert.That(conversation.LastTone, Is.EqualTo(0));

            var calm = Janitor(sEnt).Responses.Single(r => r.Lang == "fr" && r.Intent == "greeting").Calm;
            Assert.That(calm, Does.Contain(conversation.LastReply));
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Question_UsesInformativeFirstVariant()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, comment tu vas ?", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("small_talk"));
            Assert.That(conversation.LastReply,
                Is.EqualTo(Janitor(sEnt).Responses.Single(r => r.Lang == "fr" && r.Intent == "small_talk").Calm[0]));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ExplicitOrderPhrase_StillHandledByOrderSystem()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard suis-moi", speaker));

            Assert.That(sEnt.GetComponent<AndroidOrderComponent>(android).OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
            Assert.That(sEnt.HasComponent<AndroidConversationComponent>(android), Is.False,
                "Recognized orders do not go through free conversation");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WorkCommand_IssuesReturnToWork()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, va bosser", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("work_command"));
            Assert.That(conversation.LastEffect, Is.EqualTo(AndroidConversationEffect.ReturnToWork));
            Assert.That(sEnt.GetComponent<AndroidOrderComponent>(android).OrderType, Is.EqualTo(AndroidOrderType.ReturnToWork));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CleaningRequest_SendsAndroidToSpeaker()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker(new Vector2(1, 0));
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, il y a du sang par terre", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("cleaning_request"));
            Assert.That(conversation.LastEffect, Is.EqualTo(AndroidConversationEffect.CleanArea));

            var order = sEnt.GetComponent<AndroidOrderComponent>(android);
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.CleanArea));
            Assert.That(order.Issuer, Is.EqualTo(speaker));
            Assert.That(order.TargetCoordinates, Is.EqualTo(sEnt.GetComponent<TransformComponent>(speaker).Coordinates));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SupplyRequest_HandsOverTheMop()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker(new Vector2(0.5f, 0));
        var sEnt = pair.Server.EntMan;
        var hands = sEnt.System<SharedHandsSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, can I borrow your mop?", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("supply_request"));
            Assert.That(conversation.LastEffect, Is.EqualTo(AndroidConversationEffect.GiveSupply));

            var held = hands.EnumerateHeld(speaker).ToList();
            Assert.That(held.Any(e => sEnt.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "MopItem"), Is.True,
                "The speaker should now hold the android's mop");
            Assert.That(sEnt.GetComponent<AndroidInventoryComponent>(android).OwnedItems.Any(held.Contains), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Threat_UsesSecurityLineAndHasNoEffect()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, nettoie ça ou je vais te tuer", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("cleaning_request"));
            Assert.That(conversation.LastTone, Is.EqualTo(2));
            Assert.That(conversation.LastEffect, Is.EqualTo(AndroidConversationEffect.None));
            Assert.That(Janitor(sEnt).Responses.Single(r => r.Lang == "fr" && r.Intent == "cleaning_request").Threatening,
                Does.Contain(conversation.LastReply));
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Emergency_MakesAndroidEvacuate()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;
        var timing = pair.Server.ResolveDependency<IGameTiming>();

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, il y a un incendie à la cuisine", speaker));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(android);
            Assert.That(conversation.LastIntent, Is.EqualTo("emergency"));
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).EmergencyUntil, Is.GreaterThan(timing.CurTime));
        });

        await pair.Server.WaitRunTicks(2);

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).Status, Is.EqualTo(AndroidStatus.Fleeing));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Replies_AreRateLimited()
    {
        var (pair, android, speaker) = await SpawnAndroidAndSpeaker();
        var sEnt = pair.Server.EntMan;

        await pair.Server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, salut", speaker));
            sEnt.EventBus.RaiseLocalEvent(android, new ListenEvent("Gerard, comment tu vas ?", speaker));

            Assert.That(sEnt.GetComponent<AndroidConversationComponent>(android).LastIntent, Is.EqualTo("greeting"),
                "The second message arrives during the cooldown and is ignored");
        });

        await pair.CleanReturnAsync();
    }
}
