using System.Text.Json;
using System.Text.Json.Nodes;
using Stagehand.Sts2;

var cases = new (string Name, Action Test)[]
{
    ("travel rejects a stale room before native activation", () =>
    {
        var g = new Game();
        g.State["screen"] = "NMapScreen";
        g.State["map"] = JsonNode.Parse("""[{"ref":"current","state":"Travelable"}]""");

        g.Submit("""{"kind":"travel","room":{"ref":"stale"}}""");
        g.Tick();

        g.Is("failed");
        g.Equal(g.Outcome["error"]!.GetValue<string>().Contains("travelable"), true);
        g.Equal(g.Clicks.Count, 0);
    }),
    ("menu return refuses an active unlock before any native input", () =>
    {
        var g = new Game();
        g.State["navigation"] = JsonNode.Parse("""{"atMainMenu":false,"canReturnToMenu":false}""");

        g.Submit("""{"kind":"returnToMenu"}""");
        g.Tick();

        g.Is("failed");
        g.Equal(g.Outcome["error"]!.GetValue<string>().Contains("unavailable"), true);
        g.Equal(g.Clicks.Count, 0);
    }),
    ("travel revalidates a room after pacing and never activates an illegal target", () =>
    {
        var g = new Game();
        g.Map();
        g.Submit("""{"kind":"travel","room":{"ref":"room"},"pacingMs":400}""");
        g.Equal(g.Travels.Count, 0);
        g.Tick();
        g.Tick(() => g.State["map"]![0]!["state"] = "Untravelable");
        for (var frame = 0; frame < 4; frame++)
            g.Tick();

        g.Is("failed");
        g.Equal(g.Travels.Count, 0);
    }),
    ("travel waits for arrival beyond UI acknowledgment and keeps its attempted room", () =>
    {
        var g = new Game();
        g.Map();
        g.OnTravel = _ => g.State["ready"] = false;
        g.Submit("""{"kind":"travel","room":{"ref":"room"}}""");
        g.Tick();
        for (var frame = 0; frame < 90; frame++)
            g.Tick();
        g.Is("running");
        g.Equal(g.Travels.Count, 1);

        g.Tick(() => { g.State["screen"] = "NCombatRoom"; g.State["floor"] = 11; g.State["ready"] = true; });
        g.Is("completed");
        g.Equal(g.Outcome["attempted"]!["room"]!["ref"]!.GetValue<string>(), "room");
        g.Equal(g.Outcome["state"]!["floor"]!.GetValue<int>(), 11);
    }),
    ("travel with a failed post-activation read remains uncertain without replay", () =>
    {
        var g = new Game();
        g.Map();
        g.OnTravel = _ => g.Observe = () => throw new Exception("Lost native observation");
        g.Submit("""{"kind":"travel","room":{"ref":"room"}}""");
        g.Tick();

        g.Is("unconfirmed");
        g.Equal(g.Travels.Count, 1);
        g.Equal(g.Outcome["state"], null);
        g.Equal(g.Outcome["attempted"]!["room"]!["ref"]!.GetValue<string>(), "room");
    }),
    ("explicit shop entry preserves the shopkeeper decision and rejects a changed entrance", () =>
    {
        var g = new Game();
        g.Entrance();
        g.OnBrowse = _ =>
        {
            g.State["screen"] = "NFakeMerchantInventory";
            g.State["shop"]!["open"] = true;
        };
        g.Submit("""{"kind":"openShop"}""");
        g.Tick();
        g.Is("completed");
        g.Equal(g.Browses.Count, 1);
        g.Equal(g.Outcome["state"]!["screen"]!.GetValue<string>(), "NFakeMerchantInventory");

        g.Entrance();
        g.Submit("""{"kind":"openShop","pacingMs":400}""");
        g.Tick();
        g.Tick(() => g.State["shop"]!["control"] = "different-shop");
        for (var frame = 0; frame < 4; frame++)
            g.Tick();
        g.Is("failed");
        g.Equal(g.Browses.Count, 1);
    }),
    ("automatic shop entry retains an uncertain activation without replay", () =>
    {
        var g = new Game();
        g.Entrance();
        g.State["player"] = null;
        g.OnBrowse = _ => throw new Exception("Native shop handler interrupted");
        g.Submit("""{"kind":"settle"}""");
        g.Tick();

        g.Is("unconfirmed");
        g.Equal(g.Browses.Count, 1);
        g.Equal(g.Outcome["automaticOutcome"]!["attempted"]!["shop"]!.GetValue<string>(), "merchant");
    }),
    ("menu return retains applied pops when a later unlock blocks navigation", () =>
    {
        var g = new Game();
        g.Menu("outer");
        g.OnPop = _ =>
        {
            g.State["navigation"]!["menuTop"] = "unlock";
            g.State["navigation"]!["canReturnToMenu"] = false;
        };
        g.Submit("""{"kind":"returnToMenu"}""");
        g.Tick();

        g.Is("failed");
        g.Equal(g.Pops.Count, 1);
        g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["results"]![0]!["attempted"]!["menu"]!.GetValue<string>(), "outer");
        g.Equal(g.Outcome["state"]!["navigation"]!["menuTop"]!.GetValue<string>(), "unlock");
    }),
    ("menu return waits for each ready transition and preserves pending epochs", () =>
    {
        var g = new Game();
        g.Menu("inner");
        g.OnPop = top =>
        {
            g.State["navigation"]!["menuTop"] = top == "inner" ? "timeline" : null;
            g.State["navigation"]!["atMainMenu"] = top == "timeline";
            g.State["ready"] = false;
        };
        g.Submit("""{"kind":"returnToMenu"}""");
        g.Tick();
        g.Is("running");
        g.Equal(g.Pops.Count, 1);
        g.Tick(() => g.State["ready"] = true);
        g.Equal(g.Pops.Count, 2);
        g.Is("running");
        g.Tick(() => g.State["ready"] = true);

        g.Is("completed");
        g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
        g.Equal(g.Outcome["state"]!["navigation"]!["pendingEpochReveals"]!.GetValue<int>(), 3);
        g.Equal(g.Clicks.Count, 0);
    }),
    ("native menu handler uncertainty retains the attempted pop and stops", () =>
    {
        var g = new Game();
        g.Menu("timeline");
        g.OnPop = _ => throw new Exception("Native handler interrupted");
        g.Submit("""{"kind":"returnToMenu"}""");
        g.Tick();
        for (var frame = 0; frame < 5; frame++)
            g.Tick();

        g.Is("unconfirmed");
        g.Equal(g.Pops.Count, 1);
        g.Equal(g.Outcome["results"]![0]!["attempted"]!["menu"]!.GetValue<string>(), "timeline");
    }),
    ("run setup uses native Standard identity and retains progress on a locked character", () =>
    {
        var g = new Game();
        g.State["combat"] = null;
        g.State["screen"] = "NSingleplayerSubmenu";
        g.State["choices"] = JsonNode.Parse("""[{"ref":"custom","label":"Standard","type":"NSubmenuButton","enabled":true},{"ref":"standard","label":"通常","type":"NSubmenuButton","enabled":true,"detail":{"menuAction":"standard"}}]""");
        g.OnChoose = _ =>
        {
            g.State["screen"] = "NCharacterSelectScreen";
            g.State["choices"] = JsonNode.Parse("""[{"ref":"character","label":"Locked","type":"NCharacterSelectButton","enabled":false,"detail":{"id":"LOCKED"}},{"ref":"confirm","type":"NConfirmButton","enabled":true}]""");
        };
        g.Submit("""{"kind":"startRun","character":"LOCKED"}""");
        g.Tick();

        g.Is("failed");
        g.Equal(g.Clicks.Single(), "standard");
        g.Equal(g.Outcome["results"]![0]!["selectedChoice"]!["ref"]!.GetValue<string>(), "standard");
        g.Equal(g.Outcome["error"]!.GetValue<string>().Contains("locked"), true);
    }),
    ("retention confirmation waits for the same resumed turn beyond the UI acknowledgment budget", () =>
    {
        var g = new Game();
        g.Selection();
        g.State["screen"] = "HandSelection";
        g.State["pending"] = JsonNode.Parse("""{"action":"turn","kind":"endTurn","status":"choice_required"}""");
        g.OnChoose = choice =>
        {
            if (choice["ref"]!.GetValue<string>() != "confirm")
            {
                choice["selected"] = true;
                g.State["choices"]!.AsArray().Last()!["enabled"] = true;

                return;
            }

            g.State["screen"] = "NCombatRoom";
            g.State["selection"] = null;
            g.State["choices"] = new JsonArray();
            g.State["ready"] = false;
            g.State["pending"]!["status"] = "running";
        };

        g.Submit("""{"kind":"selectCards","cards":"Defend"}""");
        g.Tick();
        for (var frame = 0; frame < 90; frame++)
            g.Tick();

        g.Is("running");
        g.Equal(g.Clicks.Count, 2);
        g.Equal(g.Requests.Count, 0);

        g.Finish();
        g.Is("completed");
        var results = g.Outcome["results"]!.AsArray();
        g.Equal(results[0]!["state"]!["choices"]![0]!["selected"]!.GetValue<bool>(), true);
        g.Equal(results[1]!["selectedChoice"]!["ref"]!.GetValue<string>(), "confirm");
        g.Equal(g.Outcome["state"]!["pending"]!["action"]!.GetValue<string>(), "turn");
        g.Equal(g.Outcome["state"]!["pending"]!["status"]!.GetValue<string>(), "completed");
    }),
    ("a closed selection without evidence of its original parent resuming stays unconfirmed", () =>
    {
        foreach (var pending in new[]
        {
            """{"action":"different","status":"running"}""",
            """{"action":"turn","status":"choice_required"}"""
        })
        {
            var g = new Game();
            g.Selection();
            g.State["pending"] = JsonNode.Parse("""{"action":"turn","status":"choice_required"}""");
            g.State["choices"]!.AsArray().Last()!["enabled"] = true;
            g.OnChoose = _ =>
            {
                g.State["screen"] = "NCombatRoom";
                g.State["selection"] = null;
                g.State["choices"] = new JsonArray();
                g.State["ready"] = false;
                g.State["pending"] = JsonNode.Parse(pending);
            };

            g.Submit("""{"kind":"selectCards","cards":[]}""");
            g.Tick();
            for (var frame = 0; frame < 90; frame++)
                g.Tick();

            g.Is("unconfirmed");
            g.Equal(g.Clicks.Count, 1);
            g.Equal(g.Outcome["results"]![0]!["selectedChoice"]!["ref"]!.GetValue<string>(), "confirm");
            g.Equal(g.Requests.Count, 0);
        }
    }),
    ("resumed selection parents preserve failure, cancellation and a subsequent required choice", () =>
    {
        foreach (var status in new[] { "failed", "cancelled", "choice_required" })
        {
            var g = new Game();
            g.Selection();
            g.State["pending"] = JsonNode.Parse("""{"action":"turn","status":"choice_required"}""");
            g.State["choices"]!.AsArray().Last()!["enabled"] = true;
            g.OnChoose = _ =>
            {
                g.State["screen"] = "NCombatRoom";
                g.State["selection"] = null;
                g.State["choices"] = new JsonArray();
                g.State["ready"] = false;
                g.State["pending"]!["status"] = "running";
            };

            g.Submit("""{"kind":"selectCards","cards":[]}""");
            g.Tick();
            g.Finish(status, () =>
            {
                if (status == "failed")
                    g.State["pending"]!["error"] = "Native failure";
                if (status == "choice_required")
                    g.Selection();
            });

            g.Is(status);
            g.Equal(g.Outcome["action"]!.GetValue<string>(), "turn");
            g.Equal(g.Clicks.Count, 1);
            if (status == "failed")
                g.Equal(g.Outcome["error"]!.GetValue<string>(), "Native failure");
        }
    }),
    ("confirmation exposes an immediate next selection under the same parent", () =>
    {
        foreach (var operation in new[]
        {
            """{"kind":"selectCards","cards":[]}""",
            """{"kind":"choose","choice":{"ref":"confirm"}}"""
        })
        {
            var g = new Game();
            g.Selection();
            g.State["pending"] = JsonNode.Parse("""{"action":"turn","status":"choice_required"}""");
            g.State["choices"]!.AsArray().Last()!["enabled"] = true;
            g.OnChoose = _ =>
            {
                g.Selection();
                g.State["selection"]!["prompt"] = "Choose another card";
            };

            g.Submit(operation);
            g.Tick();

            g.Is("choice_required");
            g.Equal(g.Outcome["action"]!.GetValue<string>(), "turn");
            g.Equal(g.Outcome["state"]!["selection"]!["prompt"]!.GetValue<string>(), "Choose another card");
            g.Equal(g.Outcome["state"]!["pending"]!["status"]!.GetValue<string>(), "choice_required");
            g.Equal(g.Clicks.Count, 1);
            g.Equal(g.Requests.Count, 0);
        }
    }),
    ("Ancient dialogue is an explicit decision by default and autoskip is an operation preference", () =>
    {
        var g = new Game(); g.Rewards(); g.State["screen"] = "NEventRoom";
        g.State["dialogue"] = JsonNode.Parse("""{"ref":"chat","canAdvance":true,"text":["現在の会話"]}""");
        g.State["choices"] = JsonNode.Parse("""[{"ref":"chat","type":"NAncientDialogueHitbox","label":"Weiter","enabled":true}]""");
        g.OnChoose = _ => { g.State["dialogue"]!["canAdvance"] = false; g.State["choices"] = JsonNode.Parse("""[{"ref":"offer","type":"NEventOptionButton","label":"Relic","enabled":true}]"""); };
        g.Submit("""{"kind":"settle"}"""); g.Tick(); g.Is("ready"); g.Equal(g.Clicks.Count, 0);
        g.Submit("""{"kind":"choose","choice":{"ref":"chat"}}"""); g.Tick();
        g.Equal(g.Clicks.Single(), "chat");
        g.Equal(g.Outcome["state"]!["choices"]![0]!["type"]!.GetValue<string>(), "NEventOptionButton");
        g.State["dialogue"]!["canAdvance"] = true;
        g.State["choices"] = JsonNode.Parse("""[{"ref":"chat","type":"NAncientDialogueHitbox","label":"Weiter","enabled":true}]""");
        g.Submit("""{"kind":"settle","autoSkipDialogue":true}"""); g.Tick(); g.Is("ready");
        g.Equal(string.Join(",", g.Clicks), "chat,chat");
        g.Equal(g.Outcome["outcomeText"]![0]!.GetValue<string>(), "現在の会話");
        // A later operation returns to the default; this preference is not game-global.
        g.State["dialogue"]!["canAdvance"] = true;
        g.State["choices"] = JsonNode.Parse("""[{"ref":"chat","type":"NAncientDialogueHitbox","label":"Weiter","enabled":true}]""");
        g.Submit("""{"kind":"settle"}"""); g.Tick(); g.Equal(g.Clicks.Count, 2);
    }),
    ("forced settlement preserves an attempted choice when its post-action state is unavailable", () =>
    {
        var g = new Game(); g.Event(true);
        g.Observe = () => g.Clicks.Count > 0 ? new JsonObject { ["error"] = "Projection failed" } : g.State.DeepClone().AsObject();
        g.Submit("""{"kind":"settle","outcome":{"status":"completed","results":[{"status":"completed","state":{"screen":"BeforeUI"}}],"state":{}}}""");
        g.Tick(); g.Is("unconfirmed");
        g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["state"]!["screen"]!.GetValue<string>(), "NEventRoom");
        g.Equal(g.Outcome["stateScope"]!.GetValue<string>(), "last_observed");
        g.Equal(g.Outcome["executionStatus"]!.GetValue<string>(), "completed");
        g.Equal(g.Outcome["results"]![0]!["state"]!["screen"]!.GetValue<string>(), "BeforeUI");
        g.Equal(g.Outcome["automaticOutcome"]!["selectedChoice"]!["ref"]!.GetValue<string>(), "proceed");
        g.Equal(g.Outcome["automaticOutcome"]!["state"], null);
        g.Equal(g.Outcome["automaticOutcome"]!["error"]!.GetValue<string>().Contains("Projection failed"), true);
    }),
    ("a choice observation identifies the current stop without rewriting the historical receipt", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"potion","type":"NRewardButton","label":"Potion","enabled":false,"blockedBy":"potion_inventory_full"}]""");
        g.Submit("""{"kind":"claimRewards","items":["Potion"]}"""); g.Tick(); g.Is("choice_required");
        g.Equal(g.Service.Observe(g.State)!["atChoiceStop"]!.GetValue<bool>(), true);
        var receipt = g.Service.Status(g.Operation).ToJsonString();
        g.State["screen"] = "NMapScreen"; g.State["choices"] = new JsonArray();
        g.Equal(g.Service.Observe(g.State)!["atChoiceStop"]!.GetValue<bool>(), false);
        g.Equal(g.Service.Status(g.Operation).ToJsonString(), receipt);
    }),
    ("a native UI handler error after activation retains the attempted choice", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"gold","type":"NRewardButton","label":"Gold","enabled":true},{"ref":"card","type":"NRewardButton","label":"Cards","enabled":true}]""");
        g.OnChoose = c => { g.State["choices"]!.AsArray().Remove(c); throw new InvalidOperationException("Handler failed after applying input"); };
        g.Submit("""{"kind":"claimRewards","items":["Gold","Cards"]}"""); g.Tick(); g.Is("unconfirmed");
        g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["results"]![0]!["selectedChoice"]!["ref"]!.GetValue<string>(), "gold");
        g.Equal(g.Outcome["remaining"]![0]!.GetValue<string>(), "Cards");
    }),
    ("accepted card/purchase steps survive observation failure without entering the unsubmitted tail", () =>
    {
        foreach (var kind in new[] { "playCards", "buy" })
        {
            var g = new Game();
            if (kind == "buy")
            {
                g.Shop();
                g.State["choices"] = new JsonArray(new[] { "First", "Second", "Third" }.Select(name => (JsonNode)new JsonObject
                {
                    ["ref"] = name, ["offerRef"] = name, ["label"] = name, ["type"] = "NMerchantCard", ["enabled"] = true
                }).ToArray());
            }
            g.Observe = () => g.Requests.Count >= 2 ? new JsonObject { ["error"] = "Projection failed" } : g.State.DeepClone().AsObject();
            g.Submit(kind == "playCards" ? """{"kind":"playCards","plays":["Strike","Strike","Strike"]}"""
                : """{"kind":"buy","items":["First","Second","Third"]}""");
            g.Tick(); g.Finish(); g.Is("unconfirmed");
            g.Equal(g.Requests.Count, 2);
            g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
            g.Equal(g.Outcome["results"]![0]!["status"]!.GetValue<string>(), "completed");
            g.Equal(g.Outcome["results"]![1]!["action"]!.GetValue<string>(), "2");
            g.Equal(g.Outcome["results"]![1]!["status"]!.GetValue<string>(), "unconfirmed");
            g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1);
            g.Equal(g.State["pending"]!["status"]!.GetValue<string>(), "accepted");
        }
    }),
    ("attempted rewards/selections survive observation failure with earlier progress and no replay", () =>
    {
        foreach (var kind in new[] { "claimRewards", "selectCards" })
        {
            var g = new Game();
            if (kind == "selectCards")
            {
                g.Selection();
                var third = g.State["choices"]![0]!.DeepClone(); third["ref"] = "three";
                g.State["choices"]!.AsArray().Add(third);
                g.State["selection"]!["max"] = 3;
            }
            else
            {
                g.Rewards();
                g.State["choices"] = new JsonArray(new[] { "Gold", "Potion", "Cards" }.Select(name => (JsonNode)new JsonObject
                {
                    ["ref"] = name, ["label"] = name, ["type"] = "NRewardButton", ["enabled"] = true
                }).ToArray());
            }
            g.Observe = () => g.Clicks.Count >= 2 ? new JsonObject { ["error"] = "Projection failed" } : g.State.DeepClone().AsObject();
            g.Submit(kind == "selectCards" ? """{"kind":"selectCards","cards":["Defend","Defend","Defend"],"confirm":false}"""
                : """{"kind":"claimRewards","items":["Gold","Potion","Cards"]}""");
            g.Tick(); g.Is("unconfirmed");
            g.Equal(g.Clicks.Count, 2);
            g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
            g.Equal(g.Outcome["results"]![0]!["status"]!.GetValue<string>(), "observed_change");
            g.Equal(g.Outcome["results"]![1]!["selectedChoice"]!["ref"]!.GetValue<string>(), g.Clicks[1]);
            g.Equal(g.Outcome["results"]![1]!["status"]!.GetValue<string>(), "unconfirmed");
            g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1);
        }
    }),
    ("Tyranny turn-start hand choice stops endTurn and completes the same native parent after selection", () =>
    {
        var g = new Game();
        g.Submit("""{"kind":"playCards","plays":[],"endTurn":true}"""); g.Tick();
        g.Finish("choice_required", () =>
        {
            g.Selection(); g.State["screen"] = "HandSelection";
            g.State["selection"] = JsonNode.Parse("""{"prompt":"Choose a card to Exhaust","min":1,"max":1,"selected":[]}""");
            g.State["combat"]!["phase"] = "Start"; g.State["combat"]!["ready"] = false;
            g.State["choices"] = new JsonArray(new[] {"Gather Light", "Cloak of Stars+", "Defend", "Defend", "I Am Invincible", "Bulwark"}
                .Select((name, index) => (JsonNode)new JsonObject { ["ref"] = index.ToString(), ["label"] = name,
                    ["type"] = "NHandCardHolder", ["enabled"] = true, ["detail"] = new JsonObject { ["name"] = name }}).ToArray());
        });
        g.Is("choice_required");
        var parent = g.Outcome["action"]!.GetValue<string>();
        g.OnChoose = _ =>
        {
            g.State["screen"] = "NCombatRoom"; g.State["selection"] = null;
            g.State["choices"] = new JsonArray(); g.State["ready"] = false;
            g.State["pending"]!["status"] = "running";
        };
        g.Submit("""{"kind":"selectCards","cards":"Defend","confirm":false}"""); g.Tick();
        g.Is("running"); g.Equal(g.Requests.Count, 1);
        g.Equal(g.State["pending"]!["action"]!.GetValue<string>(), parent);
        g.Finish("completed", () => { g.State["combat"]!["phase"] = "Play"; g.State["combat"]!["ready"] = true; });
        g.Is("completed"); g.Equal(g.Requests.Count, 1); g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["state"]!["pending"]!["action"]!.GetValue<string>(), parent);
        g.Equal(g.Outcome["state"]!["pending"]!["status"]!.GetValue<string>(), "completed");
    }),
    ("Godot JSON float-form integral pacing is accepted without weakening validation", () =>
    {
        var g = new Game();
        g.Submit("""{"kind":"playCards","plays":[],"pacingMs":0.0}""");
        g.Tick(); g.Is("completed");
    }),
    ("the service supplies a completion allowance scaled by steps and pacing", () =>
    {
        var g = new Game();
        var args = JsonNode.Parse("""{"kind":"playCards","plays":[{"card":"Strike","select":["Defend","Defend"]},"Strike"],"endTurn":true,"pacingMs":400}""")!.AsObject();
        var accepted = g.Service.Start(args);
        // Two plays, two selections, confirmation and endTurn, plus settlement margin.
        g.Equal(accepted["completionTimeoutMs"]!.GetValue<int>(), 60_000 + 6 * 30_400);
        g.Equal(g.Service.Status()["completionTimeoutMs"]!.GetValue<int>(), accepted["completionTimeoutMs"]!.GetValue<int>());
        g.Equal(g.Requests.Count, 0);
    }),
    ("acceptance precedes native dispatch; names resolve against each completed hand", () =>
    {
        var g = new Game();
        g.Submit("""{"kind":"playCards","plays":[{"card":"Strike","target":"Dummy"},{"card":"Strike","target":"Dummy"}]}""");
        g.Is("accepted"); g.Equal(g.Requests.Count, 0);
        g.Tick(); g.Is("running"); g.Equal(g.Requests.Count, 1);
        g.Finish("completed", () => g.Hand("second"));
        g.Equal(g.Requests.Count, 2); g.Equal(g.Requests[1]["card"]!.GetValue<string>(), "second");
        g.Finish(); g.Is("completed"); g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
    }),
    ("unexpected choice stops the plan while original native action remains pending", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike","Strike"],"endTurn":true}""");
        g.Tick(); g.Finish("choice_required", () => g.Selection()); g.Is("choice_required");
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1); g.Equal(g.Requests.Count, 1);
        g.Equal(g.State["pending"]!["status"]!.GetValue<string>(), "choice_required");
        g.Equal(g.Outcome["action"]!.GetValue<string>(), g.State["pending"]!["action"]!.GetValue<string>());
    }),
    ("first selection progress is visible before second; confirm resumes same native parent", () =>
    {
        var g = new Game();
        g.Submit("""{"kind":"playCards","plays":[{"card":"Strike","select":["Defend","Defend"]},"Strike"]}""");
        g.Tick();
        g.OnChoose = choice =>
        {
            if (choice["ref"]!.GetValue<string>() == "confirm")
            {
                g.State["selection"] = null; g.State["screen"] = "NCombatRoom";
                g.State["choices"] = new JsonArray();
                g.State["pending"]!["status"] = "completed";
                return;
            }
            choice["selected"] = true;
            g.State["ready"] = false;
        };
        g.Finish("choice_required", () => g.Selection());
        g.Equal(g.Clicks.Count, 1); g.Is("running");
        g.Tick(() => g.State["ready"] = true);
        // The second click then awaits its own ready boundary.
        g.Equal(g.Clicks.Count, 2); g.Equal(g.Clicks[0], "one"); g.Equal(g.Clicks[1], "two");
        g.Tick(() =>
        {
            g.State["ready"] = true;
            g.State["choices"]!.AsArray().Last()!["enabled"] = true;
        });
        g.Equal(g.Clicks.Count, 3); g.Equal(g.Requests.Count, 2);
        g.Finish(); g.Is("completed");
        g.Equal(g.Outcome["results"]![0]!["selection"]!["results"]!.AsArray().Count, 3);
        var snapshots = g.Outcome["results"]![0]!["selection"]!["results"]!;
        g.Equal(snapshots[0]!["state"]!["choices"]![0]!["selected"]!.GetValue<bool>(), true);
        g.Equal(snapshots[0]!["state"]!["choices"]![1]!["selected"]!.GetValue<bool>(), false);
        g.Equal(snapshots[1]!["state"]!["choices"]![1]!["selected"]!.GetValue<bool>(), true);
    }),
    ("selection failure retains first play, first selection and the unsubmitted tail", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":[{"card":"Strike","select":["Defend","Missing"]},"Strike"]}""");
        g.Tick(); g.Finish("choice_required", () => g.Selection()); g.Is("failed");
        g.Equal(g.Requests.Count, 1); g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["results"]![0]!["selection"]!["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1);
    }),
    ("a selected-only operation completes its requests without completing the native parent", () =>
    {
        var g = new Game(); g.Selection();
        g.State["pending"] = JsonNode.Parse("""{"action":"parent","status":"choice_required"}""");
        g.Submit("""{"kind":"selectCards","cards":["Defend"],"confirm":false}"""); g.Tick();
        g.Is("completed"); g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["state"]!["pending"]!["status"]!.GetValue<string>(), "choice_required");
        g.Equal(g.Outcome["state"]!["pending"]!["action"]!.GetValue<string>(), "parent");
    }),
    ("combat ending stops unused cards and never requests endTurn", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike","Strike"],"endTurn":true}""");
        g.Tick(); g.Finish("completed", () => g.Rewards()); g.Is("completed");
        g.Equal(g.Requests.Count, 1); g.Equal(g.Outcome["combatEnded"]!.GetValue<bool>(), true);
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1);
    }),
    ("illegal later step keeps earlier completed actions and the actual remaining request", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike",{"ref":"stale"},"Strike"]}""");
        g.Tick(); g.Finish(); g.Is("failed");
        g.Equal(g.Requests.Count, 1); g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 2);
    }),
    ("native cancellation is not completion and stops remaining commands", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike","Strike"]}""");
        g.Tick(); g.Finish("cancelled"); g.Is("cancelled"); g.Equal(g.Requests.Count, 1);
    }),
    ("malformed later step retains the earlier applied result", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike",null]}""");
        g.Tick(); g.Finish(); g.Is("failed");
        g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1);
    }),
    ("batch results preserve independent historical snapshots alongside final state", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike","Strike"]}""");
        g.Tick(); g.Finish("completed", () => g.Hand("second"));
        g.Finish("completed", () => g.Hand("third")); g.Is("completed");
        g.Equal(g.Outcome["results"]![0]!["state"]!["combat"]!["hand"]![0]!["ref"]!.GetValue<string>(), "second");
        g.Equal(g.Outcome["results"]![1]!["state"]!["combat"]!["hand"]![0]!["ref"]!.GetValue<string>(), "third");
        g.Equal(g.Outcome["state"]!["combat"]!["hand"]![0]!["ref"]!.GetValue<string>(), "third");
        g.Equal(g.Outcome["state"]!["operation"]!["status"]!.GetValue<string>(), "completed");
    }),
    ("endTurn selection stops at its native boundary with all plays retained", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike"],"endTurn":true}""");
        g.Tick(); g.Finish(); g.Equal(g.Requests[1]["kind"]!.GetValue<string>(), "endTurn");
        g.Finish("choice_required", () => g.Selection()); g.Is("choice_required");
        g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
    }),
    ("same printed name with different native variants is rejected before dispatch", () =>
    {
        var g = new Game();
        var variant = g.State["combat"]!["hand"]![0]!.DeepClone(); variant["ref"] = "different"; variant["starCost"] = 2;
        g.State["combat"]!["hand"]!.AsArray().Add(variant);
        g.Submit("""{"kind":"playCards","plays":["Strike"]}"""); g.Tick(); g.Is("failed"); g.Equal(g.Requests.Count, 0);
    }),
    ("a bought removal opens a real choice and keeps later offers unsubmitted", () =>
    {
        var g = new Game(); g.Shop(); g.Submit("""{"kind":"buy","items":["Removal","Potion"]}""");
        g.Tick(); g.Finish("choice_required", () => g.Selection()); g.Is("choice_required");
        g.Equal(g.Outcome["remaining"]!.AsArray().Count, 1); g.Equal(g.Requests.Count, 1);
    }),
    ("fresh restocked offer identity is required after earlier purchases", () =>
    {
        var g = new Game(); g.Shop(); g.Submit("""{"kind":"buy","items":["Potion",{"offerRef":"old"}]}""");
        g.Tick(); g.Finish(); g.Is("failed"); g.Equal(g.Requests.Count, 1);
        g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
    }),
    ("full shop inventory stops as a choice without undoing an earlier purchase", () =>
    {
        var g = new Game(); g.Shop();
        g.State["choices"]!.AsArray().Add(JsonNode.Parse("""{"ref":"card","offerRef":"card-offer","label":"Card","type":"NMerchantCard","enabled":true}"""));
        var potion = g.State["choices"]![1]!;
        potion["enabled"] = false; potion["blockedBy"] = "potion_inventory_full";
        g.Submit("""{"kind":"buy","items":["Card","Potion"]}"""); g.Tick(); g.Finish();
        g.Is("choice_required"); g.Equal(g.Requests.Count, 1);
        g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["remaining"]![0]!.GetValue<string>(), "Potion");
        g.Equal(g.Outcome["blocker"]!["kind"]!.GetValue<string>(), "potion_inventory_full");
    }),
    ("full inventory is a choice with prior gold and unclaimed potion intact", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"gold","type":"NRewardButton","label":"Gold","enabled":true,"detail":{"category":"GoldReward"}},{"ref":"potion","type":"NRewardButton","label":"Potion","enabled":false,"blockedBy":"potion_inventory_full","detail":{"category":"PotionReward"}}]""");
        g.Submit("""{"kind":"collectLoot","potions":true}"""); g.Tick(); g.Is("choice_required");
        g.Equal(g.Clicks.Count, 1); g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["remaining"]![0]!["ref"]!.GetValue<string>(), "potion");
    }),
    ("collectLoot leaves unrequested relics/special cards and opens only ordinary cards", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"relic","type":"NRewardButton","label":"Relic","enabled":true,"detail":{"category":"RelicReward"}},{"ref":"special","type":"NRewardButton","label":"Recover","enabled":true,"detail":{"category":"SpecialCardReward"}},{"ref":"ordinary","type":"NRewardButton","label":"Cards","enabled":true,"detail":{"category":"CardReward"}}]""");
        g.OnChoose = c => { g.State["screen"] = "NCardRewardSelectionScreen"; g.State["choices"] = new JsonArray(); };
        g.Submit("""{"kind":"collectLoot"}"""); g.Tick(); g.Is("choice_required");
        g.Equal(g.Clicks.Single(), "ordinary");
    }),
    ("a final explicit claim can finish on another screen without repeating it", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"gold","type":"NRewardButton","label":"Gold","enabled":true}]""");
        g.OnChoose = c => { g.State["screen"] = "NMapScreen"; g.State["choices"] = new JsonArray(); };
        g.Submit("""{"kind":"claimRewards","items":["Gold"]}"""); g.Tick(); g.Is("completed"); g.Equal(g.Clicks.Count, 1);
    }),
    ("unconfirmed UI mutation stops without replay and retains applied reward progress", () =>
    {
        var g = new Game(); g.Rewards();
        g.State["choices"] = JsonNode.Parse("""[{"ref":"gold","type":"NRewardButton","label":"Gold","enabled":true},{"ref":"card","type":"NRewardButton","label":"Cards","enabled":true}]""");
        g.OnChoose = c => { if (c["ref"]!.GetValue<string>() == "gold") g.State["choices"]!.AsArray().Remove(c); };
        g.Submit("""{"kind":"claimRewards","items":["Gold","Cards"]}"""); g.Tick();
        for (var i = 0; i < 82; i++) g.Tick();
        g.Is("unconfirmed"); g.Equal(g.Clicks.Count, 2); g.Equal(g.Outcome["results"]!.AsArray().Count, 2);
        g.Equal(g.Outcome["results"]![1]!["status"]!.GetValue<string>(), "unconfirmed");
    }),
    ("a caller disappearing does not cancel or replay an accepted service plan", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike","Strike"]}""");
        g.Tick();
        // There is no transport/caller object in the service lifetime.
        g.Finish(); g.Finish(); g.Is("completed"); g.Equal(g.Requests.Count, 2);
    }),
    ("concurrent service operations are refused; passive receipt/state remain readable", () =>
    {
        var g = new Game(); g.Submit("""{"kind":"playCards","plays":["Strike"]}""");
        try { g.Service.Start(JsonNode.Parse("""{"kind":"buy","items":[]}""")!.AsObject()); throw new Exception("Accepted concurrent work"); }
        catch (InvalidOperationException) { }
        g.Is("accepted"); g.Tick(); g.Finish(); g.Is("completed");
    }),
    ("finished native event advances; a merely named Proceed does not", () =>
    {
        var g = new Game(); g.Event(false); g.Submit("""{"kind":"settle"}"""); g.Tick(); g.Is("ready"); g.Equal(g.Clicks.Count, 0);
        g.Event(true); g.Submit("""{"kind":"settle"}"""); g.Tick(); g.Is("ready"); g.Equal(g.Clicks.Count, 1);
        g.Equal(g.Outcome["automaticSteps"]![0]!.GetValue<string>(), "leave_finished_event");
        g.Equal(g.Outcome["outcomeText"]![0]!.GetValue<string>(), "Actual outcome");
    }),
    ("mandatory chest exposes relic without automatically taking it", () =>
    {
        var g = new Game(); g.Rewards(); g.State["screen"] = "NTreasureRoom";
        g.State["choices"] = JsonNode.Parse("""[{"ref":"chest","type":"NTreasureButton","enabled":true,"label":"Open"}]""");
        g.OnChoose = c => g.State["choices"] = JsonNode.Parse("""[{"ref":"relic","type":"NTreasureRoomRelicHolder","enabled":true,"label":"Relic"},{"ref":"leave","type":"NProceedButton","enabled":true,"isSkip":true}]""");
        g.Submit("""{"kind":"settle"}"""); g.Tick(); g.Is("ready"); g.Equal(g.Clicks.Single(), "chest");
    }),
    ("native progression during pacing refreshes a forced step without submitting it again", () =>
    {
        var g = new Game(); g.Rewards(); g.State["screen"] = "NTreasureRoom";
        g.State["choices"] = JsonNode.Parse("""[{"ref":"leave","type":"NProceedButton","enabled":true,"isSkip":false}]""");
        g.Submit("""{"kind":"settle","pacingMs":400}"""); g.Tick();
        g.Tick(() => { g.State["screen"] = "NMapScreen"; g.State["choices"] = new JsonArray(); });
        for (var i = 0; i < 4; i++) g.Tick();
        g.Is("ready"); g.Equal(g.Clicks.Count, 0);
        g.Equal(g.Outcome["state"]!["screen"]!.GetValue<string>(), "NMapScreen");
    }),
    ("forced transition failure retains original applied operation and latest state", () =>
    {
        var g = new Game(); g.Event(true); g.OnChoose = c => throw new WorkflowService.ActivationRejected("Activation failed");
        g.Submit("""{"kind":"settle","outcome":{"status":"completed","results":[{"status":"completed"}],"state":{}}}""");
        g.Tick(); g.Is("failed"); g.Equal(g.Outcome["results"]!.AsArray().Count, 1);
        g.Equal(g.Outcome["executionStatus"]!.GetValue<string>(), "completed");
        g.Equal(g.Outcome["automaticOutcome"]!["error"]!.GetValue<string>(), "Activation failed");
    }),
    ("already observed event text survives native progression before settlement", () =>
    {
        var g = new Game(); g.Rewards(); g.State["screen"] = "NMapScreen";
        g.Submit("""{"kind":"settle","outcome":{"status":"observed_change","state":{"screen":"NEventRoom","event":{"finished":true,"description":"Observed ending"}}}}""");
        g.Tick(); g.Is("observed_change"); g.Equal(g.Clicks.Count, 0);
        g.Equal(g.Outcome["outcomeText"]![0]!.GetValue<string>(), "Observed ending");
    })
};
var failures = 0;
foreach (var (name, test) in cases)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} service cases passed");
return failures == 0 ? 0 : 1;

sealed class Game
{
    public JsonObject State = JsonNode.Parse("""{"screen":"NCombatRoom","ready":true,"choices":[],"combat":{"hand":[{"ref":"first","name":"Strike","cost":1}],"enemies":[{"ref":"enemy","name":"Dummy"}]}}""")!.AsObject();
    public List<JsonObject> Requests = [];
    public List<string> Clicks = [];
    public Action<JsonObject>? OnChoose;
    public Func<JsonObject>? Observe;
    public Action<string>? OnTravel;
    public Action<string>? OnBrowse;
    public Action<string>? OnPop;
    public List<string> Travels = [], Browses = [], Pops = [];
    public WorkflowService Service;
    public string Operation = "";
    private long _time;
    public JsonObject Outcome => Service.Status(Operation)["outcome"]!.AsObject();
    public Game()
    {
        Service = new WorkflowService(() => Observe?.Invoke() ?? State.DeepClone().AsObject(), args =>
        {
            Requests.Add(args.DeepClone().AsObject());
            var id = Requests.Count.ToString();
            State["pending"] = new JsonObject { ["action"] = id, ["status"] = "accepted" };
            return new JsonObject { ["action"] = id, ["status"] = "accepted" };
        }, choice =>
        {
            var current = State["choices"]!.AsArray().OfType<JsonObject>().Single(c => c["ref"]!.ToJsonString() == choice["ref"]!.ToJsonString());
            Clicks.Add(current["ref"]!.GetValue<string>());
            if (OnChoose != null)
                OnChoose(current);
            else if (current["type"]!.GetValue<string>() == "NGridCardHolder")
                current["selected"] = true;
            else
                State["choices"]!.AsArray().Remove(current);
        }, owner => { Browses.Add(owner); OnBrowse!(owner); },
        room => { Travels.Add(room); OnTravel!(room); },
        top => { Pops.Add(top); OnPop!(top); }, () => _time);
    }
    public void Submit(string json) => Operation = Service.Start(JsonNode.Parse(json)!.AsObject())["operation"]!.GetValue<string>();
    public void Tick(Action? update = null)
    {
        _time += 100;
        update?.Invoke();
        Service.Advance();
    }
    public void Finish(string status = "completed", Action? update = null) => Tick(() =>
    {
        State["pending"]!["status"] = status;
        State["ready"] = true;
        update?.Invoke();
    });
    public void Map()
    {
        State["screen"] = "NMapScreen";
        State["act"] = 1;
        State["floor"] = 10;
        State["map"] = JsonNode.Parse("""[{"ref":"room","state":"Travelable"}]""");
    }
    public void Entrance()
    {
        Rewards();
        State["screen"] = "NEventRoom";
        State["shop"] = JsonNode.Parse("""{"control":"merchant","open":false,"canBrowse":true}""");
        State["player"] = JsonNode.Parse("""{"potions":[{"usable":true,"targets":[{"kind":"shopkeeper"}]}]}""");
    }
    public void Menu(string top)
    {
        Rewards();
        State["screen"] = "NTimelineScreen";
        State["navigation"] = new JsonObject
        {
            ["atMainMenu"] = false, ["canReturnToMenu"] = true,
            ["menuTop"] = top, ["menuDepth"] = 2, ["pendingEpochReveals"] = 3
        };
    }
    public void Hand(string id) => State["combat"]!["hand"]![0]!["ref"] = id;
    public void Selection()
    {
        State["screen"] = "NCombatPileCardSelectScreen";
        State["selection"] = JsonNode.Parse("""{"min":2,"max":2,"selected":[]}""");
        State["choices"] = JsonNode.Parse("""[{"ref":"one","label":"Defend","type":"NGridCardHolder","enabled":true,"selected":false,"detail":{"name":"Defend","cost":1}},{"ref":"two","label":"Defend","type":"NGridCardHolder","enabled":true,"selected":false,"detail":{"name":"Defend","cost":1}},{"ref":"confirm","label":"Confirm","type":"NConfirmButton","enabled":false}]""");
    }
    public void Shop()
    {
        State["combat"] = null;
        State["screen"] = "NMerchantInventory";
        State["choices"] = JsonNode.Parse("""[{"ref":"remove","offerRef":"removal","label":"Removal","type":"NMerchantCardRemoval","enabled":true},{"ref":"pot","offerRef":"potion","label":"Potion","type":"NMerchantPotion","enabled":true}]""");
    }
    public void Rewards()
    {
        State["combat"] = null;
        State["screen"] = "NRewardsScreen";
    }
    public void Event(bool finished)
    {
        Rewards();
        State["screen"] = "NEventRoom";
        State["event"] = new JsonObject { ["finished"] = finished, ["description"] = "Actual outcome" };
        State["choices"] = JsonNode.Parse("""[{"ref":"proceed","type":"NEventOptionButton","enabled":true,"label":"Proceed","detail":{"isProceed":true}}]""");
        OnChoose = _ => { State["screen"] = "NMapScreen"; State["choices"] = new JsonArray(); };
    }
    public void Is(string status) => Equal(Service.Status(Operation)["status"]!.GetValue<string>(), status);
    public void Equal<T>(T actual, T expected)
    {
        if (!Equals(actual, expected))
            throw new Exception($"Expected {expected}, got {actual}");
    }
}
