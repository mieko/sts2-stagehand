using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stagehand.Sts2;

// Bounded StS operations over the host's existing native action and choice paths.
// The service outlives a caller's socket; it never retries an uncertain mutation.
internal sealed class WorkflowService(
    Func<JsonObject> read,
    Func<JsonObject, JsonObject> request,
    Action<JsonObject> activate,
    Action<string> browse,
    Action<string> travel,
    Action<string> popMenu,
    Func<long> clock)
{
    // A native activation port can establish rejection before any input is applied.
    internal sealed class ActivationRejected(string message) : Exception(message);
    private JsonObject? _receipt;
    private TaskCompletionSource? _frame;
    private string? _browsedShop;
    private int _pacingMs;
    private bool _autoSkipDialogue;

    public bool Busy
    {
        get; private set;
    }
    public JsonObject? Summary => _receipt == null ? null : new JsonObject
    {
        ["operation"] = Text(_receipt, "operation"),
        ["kind"] = Text(_receipt, "kind"),
        ["status"] = Text(_receipt, "status"),
        ["completionTimeoutMs"] = Copy(_receipt["completionTimeoutMs"])
    };

    public JsonObject Start(JsonObject args)
    {
        if (Busy)
            throw new InvalidOperationException("A service operation is still running");

        var kind = Text(args, "kind");
        if (kind is not ("playCards" or "selectCards" or "buy" or "claimRewards" or "collectLoot" or "choose" or "settle"
            or "travel" or "openShop" or "returnToMenu" or "startRun"))
            throw new InvalidOperationException("Unknown service operation");

        var pacing = args["pacingMs"]?.Deserialize<double>() ?? 0;
        if (!double.IsFinite(pacing) || pacing != Math.Floor(pacing) || pacing is < 0 or > 5000)
            throw new ArgumentException("pacingMs must be in 0..5000");

        _pacingMs = (int)pacing;
        _autoSkipDialogue = Flag(args, "autoSkipDialogue");

        var receipt = new JsonObject
        {
            ["operation"] = Guid.NewGuid().ToString("N"),
            ["kind"] = kind,
            ["status"] = "accepted",
            ["completionTimeoutMs"] = CompletionTimeout(args)
        };
        _receipt = receipt;
        Busy = true;
        _ = Run(args.DeepClone().AsObject(), receipt);
        return Summary!;
    }

    public JsonObject Status(string operation = "")
    {
        if (_receipt == null || operation != "" && Text(_receipt, "operation") != operation)
            throw new InvalidOperationException("Unknown or expired service operation");

        return _receipt.DeepClone().AsObject();
    }

    public JsonObject? Observe(JsonObject state)
    {
        var summary = Summary;
        if (summary == null)
            return null;

        summary["atChoiceStop"] = Text(_receipt, "status") == "choice_required"
            && _receipt?["outcome"]?["state"] is JsonObject stopped
            && Fingerprint(state) == Fingerprint(stopped);
        return summary;
    }

    // A caller's completion wait is separate from its short connection/RPC deadline.
    // This is an allowance for animation and pacing, not a gameplay cancellation timer.
    private int CompletionTimeout(JsonObject args)
    {
        long Count(JsonNode? items) => items == null ? 0 : items is JsonArray list ? list.Count : 1;
        var steps = Text(args, "kind") switch
        {
            "playCards" => Count(args["plays"]) + (args["plays"] as JsonArray ?? [])
                .OfType<JsonObject>().Sum(play => Count(play["select"]) + (play["select"] == null ? 0 : 1))
                + (Flag(args, "endTurn") ? 1 : 0),
            "selectCards" => Count(args["cards"]) + (Flag(args, "confirm", true) ? 1 : 0),
            "buy" or "claimRewards" => Count(args["items"]),
            "collectLoot" => Choices(Read()).Count(choice => Text(choice, "type") == "NRewardButton") + 1,
            "choose" or "travel" or "openShop" => 1,
            "startRun" => 4,
            "returnToMenu" => Read()["navigation"]?["menuDepth"]?.GetValue<int>() ?? 1,
            _ => 0
        };
        return (int)Math.Min(int.MaxValue, 60_000L + steps * (30_000L + _pacingMs));
    }

    // Called on Godot's main thread. All native work resumes on that same thread.
    public void Advance()
    {
        var waiting = _frame;
        _frame = null;
        waiting?.SetResult();
    }

    private Task NextFrame() => (_frame = new TaskCompletionSource()).Task;

    private async Task Run(JsonObject args, JsonObject receipt)
    {
        JsonObject outcome;
        try
        {
            // Publish acceptance before starting any of the ordered work.
            await NextFrame();
            receipt["status"] = "running";
            outcome = Text(args, "kind") switch
            {
                "playCards" => await PlayCards(args),
                "selectCards" => await SelectCards(args["cards"], Flag(args, "confirm", true)),
                "buy" => await Buy(List(args["items"])),
                "claimRewards" => await ClaimRewards(List(args["items"])),
                "collectLoot" => await CollectLoot(args),
                "choose" => await Choose(args["choice"]),
                "travel" => await Travel(args["room"]),
                "openShop" => await OpenShop(),
                "returnToMenu" => await ReturnToMenu(),
                "startRun" => await StartRun(args),
                _ => args["outcome"]?.DeepClone().AsObject() ?? Result("ready", Read())
            };
            outcome = await Settle(outcome);
        }
        catch (WorkflowFailure error) { outcome = error.Outcome; }
        catch (Exception error) { outcome = Result("failed", SafeRead(), error: error.GetBaseException().Message); }

        outcome["operation"] = receipt["operation"]!.DeepClone();
        receipt["outcome"] = outcome;
        receipt["status"] = Text(outcome, "status");
        if (outcome["state"] is JsonObject state)
            state["operation"] = Observe(state);
        Busy = false;
    }

    private JsonObject Read()
    {
        var state = read();
        if (state["error"] != null)
            throw new InvalidOperationException(Text(state, "error"));

        return state;
    }

    private JsonObject? SafeRead()
    {
        try
        {
            return Read();
        }
        catch { return null; }
    }

    private async Task Pace()
    {
        var until = clock() + _pacingMs;
        while (clock() < until)
            await NextFrame();
    }

    private async Task<JsonObject> Wait(Func<JsonObject, bool> predicate, int budgetMs = 0)
    {
        var deadline = clock() + budgetMs;
        while (true)
        {
            var state = Read();
            if (predicate(state))
                return state;

            if (budgetMs > 0 && clock() >= deadline)
                throw new WorkflowFailure(Result("unconfirmed", state,
                    error: "UI change was not confirmed; no action was cancelled or retried"));

            await NextFrame();
        }
    }

    private async Task<JsonObject> Native(string kind, JsonObject? parameters = null)
    {
        await Pace();
        var args = parameters?.DeepClone().AsObject() ?? new JsonObject();
        args["kind"] = kind;
        var accepted = request(args);
        if (accepted["error"] != null)
            throw new InvalidOperationException(Text(accepted, "error"));

        return await WaitAction(Text(accepted, "action"));
    }

    private async Task<JsonObject> WaitAction(string action)
    {
        JsonObject result;
        try
        {
            var state = await Wait(s => Text(s["pending"], "action") == action
                && Text(s["pending"], "status") is not ("accepted" or "running")
                && (Flag(s, "ready") || Text(s["pending"], "status") is "failed" or "cancelled"));
            result = Result(Text(state["pending"], "status"), state);
            result["error"] = Copy(state["pending"]?["error"]);
        }
        catch (Exception error) { result = Unconfirmed(error); }
        result["action"] = action;
        return result;
    }

    private async Task<JsonObject> Choose(JsonNode? wanted)
    {
        var before = Read();
        var choice = CurrentChoice(before, wanted);

        if (Text(choice, "type") == "NBackButton" && Flag(before["shop"], "open"))
            _browsedShop = Text(before["shop"], "control");

        var prior = Fingerprint(before);
        await Pace();
        // Revalidate after pacing; it is never safe to activate a recycled UI reference.
        var current = CurrentChoice(Read(), choice);

        JsonObject result;
        try
        {
            activate(current);
            var state = await Wait(s => Flag(s, "ready") && Fingerprint(s) != prior, 8000);
            result = Result("observed_change", state);
        }
        catch (Exception error) when (error is not ActivationRejected) { result = Unconfirmed(error); }
        result["selectedChoice"] = choice.DeepClone();
        return result;
    }

    private JsonObject Unconfirmed(Exception error) => error is WorkflowFailure flow ? flow.Outcome
        : Result("unconfirmed", SafeRead(), error: "Action outcome is unknown; no action was cancelled or retried: "
            + error.GetBaseException().Message);

    // These UI handlers own input, not a tracked GameAction. An observed arrival
    // establishes their contract; a lost observation retains the attempted step.
    private async Task<JsonObject> ActivateAndWait(JsonObject attempt, Action activation,
        Func<JsonObject, bool> arrived, int budgetMs = 8000)
    {
        JsonObject result;
        try
        {
            activation();
            result = Result("completed", await Wait(s => Flag(s, "ready") && arrived(s), budgetMs));
        }
        catch (Exception error) when (error is not ActivationRejected) { result = Unconfirmed(error); }
        result["attempted"] = attempt.DeepClone();
        return result;
    }

    private async Task<JsonObject> Travel(JsonNode? room)
    {
        await Pace();
        var before = Read();
        var target = Items(before["map"]).SingleOrDefault(p => Text(p, "ref") == Text(room, "ref"));
        if (!Flag(before, "ready") || Text(before, "screen") != "NMapScreen"
            || target == null || Text(target, "state") != "Travelable")
            throw new InvalidOperationException("Pass a currently travelable room from the ready map");

        return await ActivateAndWait(new JsonObject { ["kind"] = "travel", ["room"] = target.DeepClone() },
            () => travel(Text(target, "ref")), s => Text(s, "screen") != "NMapScreen"
                && (!JsonNode.DeepEquals(s["act"], before["act"]) || !JsonNode.DeepEquals(s["floor"], before["floor"])), 0);
    }

    private async Task<JsonObject> OpenShop()
    {
        var state = Read();
        if (Flag(state["shop"], "open") && Text(state, "screen").Contains("MerchantInventory"))
            return Result("ready", state);

        if (!ShopEntrance(state) || !Flag(state, "ready"))
            throw new InvalidOperationException("Shop entrance is unavailable");

        if (!Flag(state["shop"], "canBrowse") || Text(state["shop"], "control") == "")
            return await Choose(OneChoice(state, c => Text(c, "type") == "NMerchantButton"));

        var owner = Text(state["shop"], "control");
        await Pace();
        state = Read();
        if (!ShopEntrance(state) || !Flag(state, "ready") || !Flag(state["shop"], "canBrowse")
            || Flag(state["shop"], "open") || Text(state["shop"], "control") != owner)
            throw new ActivationRejected("Shop entrance changed before activation");

        _browsedShop = owner;
        return await ActivateAndWait(new JsonObject { ["kind"] = "openShop", ["shop"] = owner },
            () => browse(owner), s => Text(s, "screen").Contains("MerchantInventory"));
    }

    private async Task<JsonObject> ReturnToMenu()
    {
        var results = new JsonArray();
        try
        {
            while (true)
            {
                var state = await Wait(s => Flag(s, "ready") || !Flag(s["navigation"], "canReturnToMenu"), 8000);
                if (Flag(state["navigation"], "atMainMenu"))
                    return Batch(Result("completed", state), results, []);
                if (!Flag(state["navigation"], "canReturnToMenu"))
                    throw new InvalidOperationException("Return to menu is unavailable while a run or unlock operation is active");

                await Pace();
                state = Read();
                var top = Text(state["navigation"], "menuTop");
                if (Flag(state["navigation"], "atMainMenu"))
                    continue;
                if (!Flag(state["navigation"], "canReturnToMenu") || top == "")
                    throw new ActivationRejected("Menu navigation changed before activation");

                var result = await ActivateAndWait(new JsonObject { ["kind"] = "returnToMenu", ["menu"] = top },
                    () => popMenu(top), s => Flag(s["navigation"], "atMainMenu") || Text(s["navigation"], "menuTop") != top);
                results.Add(result.DeepClone());
                if (Text(result, "status") != "completed")
                    return Batch(result, results, []);
            }
        }
        catch (Exception error) { throw Partial(error, results, []); }
    }

    private static JsonObject OneChoice(JsonObject state, Func<JsonObject, bool> matches,
        string error = "No single enabled native choice is available")
    {
        var choices = Choices(state).Where(c => Flag(c, "enabled") && matches(c)).ToArray();
        if (choices.Length != 1)
            throw new InvalidOperationException(error);
        return choices[0];
    }

    private async Task<JsonObject> StartRun(JsonObject args)
    {
        var results = new JsonArray();
        try
        {
            var state = Read();
            if (state["player"] != null)
                throw new InvalidOperationException("A run is already in progress");

            foreach (var action in new[] { "singleplayer", "standard" })
            {
                var choice = Choices(state).SingleOrDefault(c => Flag(c, "enabled") && Text(c["detail"], "menuAction") == action);
                if (choice == null)
                    continue;
                var result = await Choose(choice);
                results.Add(result.DeepClone());
                if (Text(result, "status") != "observed_change")
                    return Batch(result, results, []);
                state = result["state"]!.AsObject();
            }
            if (Text(state, "screen") != "NCharacterSelectScreen")
                return Batch(Result("choice_required", state), results, []);

            return await StartCharacter(Text(args, "character"), results);
        }
        catch (Exception error) { throw Partial(error, results, []); }
    }

    private async Task<JsonObject> StartCharacter(string character, JsonArray results)
    {
        var state = await Wait(s => Text(s, "screen") != "NCharacterSelectScreen" || Flag(s, "ready"), 8000);
        if (Text(state, "screen") != "NCharacterSelectScreen")
            return Batch(Result("choice_required", state), results, []);

        if (character != "")
        {
            var choice = OneChoice(state, c => Text(c, "type") == "NCharacterSelectButton"
                && (SameName(Text(c, "label"), character) || SameName(Text(c["detail"], "id"), character)),
                "Character is missing, locked or ambiguous");
            if (!Flag(choice, "selected"))
            {
                var selected = await Choose(choice);
                results.Add(selected.DeepClone());
                if (Text(selected, "status") != "observed_change")
                    return Batch(selected, results, []);
                state = selected["state"]!.AsObject();
            }
        }
        var result = await Choose(OneChoice(state, c => Text(c, "type") == "NConfirmButton"));
        results.Add(result.DeepClone());
        if (Text(result, "status") == "observed_change")
            result["status"] = result["state"]?["player"] != null ? "completed" : "choice_required";
        return Batch(result, results, []);
    }

    private async Task<JsonObject> PlayCards(JsonObject args)
    {
        var plays = args["plays"] as JsonArray ?? throw new ArgumentException("plays must be a list");
        var results = new JsonArray();
        var endTurn = Flag(args, "endTurn");
        var last = Result("completed", Read());
        for (var index = 0; index < plays.Count; index++)
        {
            JsonObject? result = null;
            try
            {
                var item = plays[index] is JsonValue || plays[index]?["ref"] != null
                    ? new JsonObject { ["card"] = Copy(plays[index]) } : plays[index]!.AsObject();
                var state = Read();
                var card = CardMatch(Items(state["combat"]?["hand"]), item["card"]);
                var target = Target(state, item["target"]);
                result = await Native("play", new JsonObject { ["card"] = card["ref"]!.DeepClone(), ["target"] = target });
                if (Text(result, "status") == "choice_required" && item["select"] != null)
                {
                    JsonObject selection;
                    try
                    {
                        selection = await SelectCards(item["select"]);
                    }
                    catch (WorkflowFailure error)
                    {
                        result["selection"] = error.Outcome;
                        results.Add(result.DeepClone());
                        throw Failure(error.Outcome, results, Tail(plays, index + 1), endTurn);
                    }
                    if (Text(selection, "status") != "completed")
                    {
                        result["selection"] = selection.DeepClone();
                        results.Add(result.DeepClone());
                        return Batch(selection, results, Tail(plays, index + 1), endTurn);
                    }
                    result = await WaitAction(Text(result, "action"));
                    result["selection"] = selection;
                }
            }
            catch (WorkflowFailure) { throw; }
            catch (Exception error)
            {
                throw Failure(Result("failed", SafeRead(), error: error.Message), results,
                    Tail(plays, index + (result == null ? 0 : 1)), endTurn);
            }
            results.Add(result.DeepClone());
            last = result;
            if (Text(result, "status") != "completed")
                return Batch(result, results, Tail(plays, index + 1), endTurn);

            if (result["state"]?["combat"] == null)
            {
                var ended = Batch(result, results, Tail(plays, index + 1));
                ended["combatEnded"] = true;
                ended["turnEnded"] = false;
                return ended;
            }
        }
        if (endTurn && last["state"]?["combat"] != null)
        {
            try
            {
                last = await Native("endTurn");
            }
            catch (Exception error)
            {
                throw Failure(Result("failed", SafeRead(), error: error.Message), results, new JsonArray(), true);
            }
            results.Add(last.DeepClone());
            if (Text(last, "status") != "completed")
                return Batch(last, results, new JsonArray());
        }
        var completed = Batch(last, results, new JsonArray());
        completed["status"] = "completed";
        completed["turnEnded"] = endTurn && results.Count > plays.Count;
        return completed;
    }

    private async Task<JsonObject> SelectCards(JsonNode? cards, bool confirm = true)
    {
        var requested = List(cards);
        var results = new JsonArray();
        var state = Read();
        for (var index = 0; index < requested.Count; index++)
        {
            if (!CardSelection(state))
                return Batch(Result("choice_required", state), results, Tail(requested, index));

            try
            {
                var candidates = Choices(state).Where(c => Flag(c, "enabled") && !Flag(c, "selected")
                    && Text(c, "type").Contains("CardHolder") && !Text(c, "type").Contains("Preview"));
                var choice = CardMatch(candidates, requested[index], selection: true);
                var result = await Choose(choice);
                results.Add(result.DeepClone());
                if (Text(result, "status") != "observed_change")
                    return Batch(result, results, Tail(requested, index + 1));
                state = result["state"]!.DeepClone().AsObject();
            }
            catch (Exception error) { throw Partial(error, results, Tail(requested, index)); }
        }
        var button = Choices(state).FirstOrDefault(c => Text(c, "type") == "NConfirmButton" && Flag(c, "enabled"));
        if (confirm && button != null)
        {
            try
            {
                var result = await Choose(button);
                results.Add(result.DeepClone());
                if (Text(result, "status") != "observed_change")
                    return Batch(result, results, new JsonArray());
                state = result["state"]!.DeepClone().AsObject();
            }
            catch (Exception error) { throw Partial(error, results, new JsonArray()); }
        }
        return Batch(Result("completed", state), results, new JsonArray());
    }

    private async Task<JsonObject> Buy(JsonArray items)
    {
        var results = new JsonArray();
        var state = Read();
        for (var index = 0; index < items.Count; index++)
        {
            try
            {
                state = Read();
                if (!Text(state, "screen").Contains("MerchantInventory"))
                    throw new InvalidOperationException("Open the shop inventory before buying");

                var offer = Match(Choices(state).Where(c => Text(c, "type") is "NMerchantCard" or "NMerchantRelic"
                    or "NMerchantPotion" or "NMerchantCardRemoval"), items[index], "offerRef", "Shop offer");
                if (Text(offer, "blockedBy") == "potion_inventory_full")
                    return InventoryChoice(offer, state, results, Tail(items, index));

                if (!Flag(offer, "enabled") || offer["offerRef"] == null)
                    throw new InvalidOperationException(Text(offer, "blockedReason", "Offer is sold out or unaffordable"));

                var result = await Native("buy", new JsonObject { ["offer"] = Copy(offer["offerRef"]) });
                results.Add(result.DeepClone());
                if (Text(result, "status") != "completed")
                    return Batch(result, results, Tail(items, index + 1));
                state = result["state"]!.DeepClone().AsObject();
            }
            catch (Exception error) { throw Partial(error, results, Tail(items, index)); }
        }
        return Batch(Result("completed", state), results, new JsonArray());
    }

    private async Task<JsonObject> ClaimRewards(JsonArray items)
    {
        var results = new JsonArray();
        var state = Read();
        for (var index = 0; index < items.Count; index++)
        {
            try
            {
                state = Read();
                if (Text(state, "screen") != "NRewardsScreen")
                    throw new InvalidOperationException("No reward list is open");

                var reward = Match(Choices(state).Where(c => Text(c, "type") == "NRewardButton"), items[index], "ref", "Reward");
                if (Text(reward, "blockedBy") == "potion_inventory_full")
                    return InventoryChoice(reward, state, results, Tail(items, index));
                var result = await Choose(reward);
                results.Add(result.DeepClone());
                if (Text(result, "status") != "observed_change")
                    return Batch(result, results, Tail(items, index + 1));
                state = result["state"]!.DeepClone().AsObject();

                if (Text(result["state"], "screen") != "NRewardsScreen")
                {
                    var stopped = Batch(result, results, Tail(items, index + 1));
                    stopped["status"] = index + 1 == items.Count ? "completed" : "choice_required";
                    return stopped;
                }
            }
            catch (Exception error) { throw Partial(error, results, Tail(items, index)); }
        }
        return Batch(Result("completed", state), results, new JsonArray());
    }

    private async Task<JsonObject> CollectLoot(JsonObject args)
    {
        var state = Read();
        if (Text(state, "screen") != "NRewardsScreen")
            throw new InvalidOperationException("No reward list is open");

        if (!BooleanOrAbsent(args["gold"]) || !BooleanOrAbsent(args["openCardReward"])
            || args["potions"] is not (null or JsonArray) && !BooleanOrAbsent(args["potions"])
            || args["relics"] is not (null or JsonArray))
            throw new ArgumentException("Use gold/openCardReward booleans, potions boolean or list, and an explicit relic list");

        var rewards = Choices(state).Where(c => Text(c, "type") == "NRewardButton").ToArray();
        var requested = new JsonArray();
        if (Flag(args, "gold", true))
            foreach (var reward in rewards.Where(c => Text(c["detail"], "category") == "GoldReward"))
                requested.Add(reward.DeepClone());
        Add(args["potions"], "PotionReward");
        Add(args["relics"], "RelicReward");
        var claimed = await ClaimRewards(requested);
        if (Text(claimed, "status") != "completed")
            return claimed;

        if (Text(claimed["state"], "screen") != "NRewardsScreen")
        {
            claimed["status"] = "choice_required";
            return claimed;
        }
        var ordinary = Choices(claimed["state"]!.AsObject()).FirstOrDefault(c => Flag(c, "enabled")
            && Text(c, "type") == "NRewardButton" && Text(c["detail"], "category") == "CardReward");
        if (Flag(args, "openCardReward", true) && ordinary != null)
        {
            try
            {
                var opened = await Choose(ordinary);
                claimed["results"]!.AsArray().Add(opened.DeepClone());
                opened["status"] = Text(opened, "status") == "observed_change" ? "choice_required" : Text(opened, "status");
                return Batch(opened, claimed["results"]!.AsArray(), new JsonArray());
            }
            catch (Exception error) { throw Partial(error, claimed["results"]!.AsArray(), new JsonArray()); }
        }
        return claimed;

        void Add(JsonNode? wanted, string category)
        {
            if (wanted == null || wanted is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag)
                return;

            var eligible = rewards.Where(c => Text(c["detail"], "category") == category).ToArray();
            var all = wanted is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
            if (all && category == "RelicReward")
                throw new ArgumentException("Relics require an explicit list");

            var selected = all ? eligible : (wanted as JsonArray ?? throw new ArgumentException("Expected a boolean or list"))
                .Select(item => Match(eligible, item, "ref", "Requested loot")).ToArray();
            foreach (var reward in selected)
                if (!Items(requested).Any(c => Text(c, "ref") == Text(reward, "ref")))
                    requested.Add(reward.DeepClone());
        }
    }

    private async Task<JsonObject> Settle(JsonObject outcome)
    {
        if (outcome["state"] is not JsonObject state || Text(outcome, "status") is "failed" or "cancelled" or "unconfirmed")
            return outcome;

        var steps = outcome["automaticSteps"] as JsonArray
            ?? new JsonArray(Items(outcome["results"]).SelectMany(r => ItemsAsText(r["automaticSteps"]))
                .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        var text = ItemsAsText(outcome["outcomeText"])
            .Concat(Items(outcome["results"]).SelectMany(r => ItemsAsText(r["outcomeText"]))).ToHashSet();
        var executionStatus = Text(outcome, "status");
        RetainEventText(state, text);
        outcome["automaticSteps"] = steps.DeepClone();
        for (var count = 0; count < 32; count++)
        {
            try
            {
                // Outcome snapshots retain provenance, never authority to advance stale UI.
                state = Read();
                if (!Flag(state, "ready"))
                    state = await Wait(s => Flag(s, "ready"), 8000);
                outcome["state"] = state.DeepClone();
                outcome["outcomeText"] = new JsonArray(text.Select(s => JsonValue.Create(s)).ToArray());
                if (ShopEntrance(state) && Flag(state["shop"], "open"))
                    state = await Wait(s => Flag(s, "ready") && Text(s, "screen").Contains("MerchantInventory"), 8000);
                if (Text(state, "screen") == "NRewardsScreen"
                    && state["rewards"]?["remaining"]?.GetValue<int>() == 0
                    && !Flag(state["rewards"], "complete"))
                    state = await Wait(s => Flag(s, "ready") && (Text(s, "screen") != "NRewardsScreen"
                        || Flag(s["rewards"], "complete") || (s["rewards"]?["remaining"]?.GetValue<int>() ?? 0) > 0), 8000);
                if (Text(state, "screen") == "NEventRoom" && Flag(state["event"], "finished") && state["shop"] == null)
                {
                    RetainEventText(state, text);
                    if (!Choices(state).Any(c => Flag(c, "enabled") && Flag(c["detail"], "isProceed")))
                        state = await Wait(s => Flag(s, "ready") && (Text(s, "screen") != "NEventRoom"
                            || Choices(s).Any(c => Flag(c, "enabled") && Flag(c["detail"], "isProceed"))), 8000);
                }
                if (Text(state, "screen") == "NTreasureRoom" && !Choices(state).Any(c => Flag(c, "enabled")
                    && Text(c, "type") is "NTreasureButton" or "NProceedButton"))
                    state = await Wait(s => Flag(s, "ready") && (Text(s, "screen") != "NTreasureRoom"
                        || Choices(s).Any(c => Flag(c, "enabled") && Text(c, "type") == "NProceedButton")), 8000);

                var step = ForcedStep(state);
                if (step == null)
                {
                    outcome["state"] = state;
                    outcome["outcomeText"] = new JsonArray(text.Select(s => JsonValue.Create(s)).ToArray());
                    return outcome;
                }
                foreach (var line in ItemsAsText(state["dialogue"]?["text"]))
                    text.Add(line);
                outcome["automaticOutcome"] = new JsonObject { ["kind"] = step.Value.Kind, ["status"] = "attempting" };
                var next = step.Value.Choice == null ? await OpenShop() : await Choose(step.Value.Choice);
                if (Text(next, "status") is not ("completed" or "ready" or "observed_change"))
                {
                    next["kind"] = step.Value.Kind;
                    outcome["automaticOutcome"] = next;
                    outcome["state"] = Copy(next["state"]) ?? state.DeepClone();
                    if (next["state"] == null)
                        outcome["stateScope"] = "last_observed";
                    outcome["executionStatus"] = executionStatus;
                    outcome["status"] = Text(next, "status");
                    outcome["error"] = Copy(next["error"]);
                    return outcome;
                }
                state = next["state"]!.DeepClone().AsObject();
                outcome["automaticSteps"]!.AsArray().Add(step.Value.Kind);
                outcome.Remove("automaticOutcome");
            }
            catch (StaleChoice)
            {
                // The game advanced before activation; no input has been submitted.
                outcome.Remove("automaticOutcome");
                continue;
            }
            catch (Exception error)
            {
                var failure = error is WorkflowFailure flow ? flow.Outcome : Result("failed", SafeRead(), error: error.Message);
                failure["kind"] = Copy(outcome["automaticOutcome"]?["kind"]);
                outcome["state"] = Copy(failure["state"]) ?? state.DeepClone();
                if (failure["state"] == null)
                    outcome["stateScope"] = "last_observed";
                outcome["outcomeText"] = new JsonArray(text.Select(s => JsonValue.Create(s)).ToArray());
                outcome["automaticOutcome"] = failure;
                outcome["executionStatus"] = executionStatus;
                outcome["status"] = Text(failure, "status");
                outcome["error"] = Copy(failure["error"]);
                throw new WorkflowFailure(outcome);
            }
        }
        outcome["status"] = "unconfirmed";
        outcome["executionStatus"] = executionStatus;
        outcome["error"] = "Automatic UI progression did not reach a decision; inspect state";
        throw new WorkflowFailure(outcome);
    }

    private (string Kind, JsonObject? Choice)? ForcedStep(JsonObject state)
    {
        var enabled = Choices(state).Where(c => Flag(c, "enabled")).ToArray();
        var shopkeeper = Items(state["player"]?["potions"]).Any(p => Flag(p, "usable")
            && Items(p["targets"]).Any(t => Text(t, "kind") == "shopkeeper"));
        if (ShopEntrance(state) && Flag(state["shop"], "canBrowse") && !Flag(state["shop"], "open")
            && Text(state["shop"], "control") != _browsedShop && !shopkeeper)
            return ("browse_shop", null);

        var screen = Text(state, "screen");
        var proceed = enabled.FirstOrDefault(c => Text(c, "type") == "NProceedButton");
        var chest = enabled.FirstOrDefault(c => Text(c, "type") == "NTreasureButton");
        if (screen == "NTreasureRoom")
        {
            if (chest != null && proceed == null)
                return ("open_chest", chest);

            if (proceed != null && !enabled.Any(c => Text(c, "type") == "NTreasureRoomRelicHolder")
                && proceed["isSkip"]?.GetValue<bool>() == false)
                return ("leave_resolved_treasure", proceed);
        }
        var merchant = enabled.FirstOrDefault(c => Text(c, "type") == "NMerchantButton");
        if (merchant != null && state["shop"] == null && screen is "NMerchantRoom" or "NEventRoom" && !shopkeeper)
            return ("browse_shop", merchant);

        if (screen == "NCrystalSphereScreen" && Flag(state["crystalSphere"], "finished") && proceed != null)
            return ("leave_finished_sphere", proceed);

        if (screen == "NRewardsScreen" && proceed != null)
        {
            if (Flag(state["rewards"], "complete") && proceed["isSkip"]?.GetValue<bool>() == false)
                return ("leave_empty_rewards", proceed);

            if ((state["rewards"]?["remaining"]?.GetValue<int>() ?? 0) > 0
                && state["rewards"]?["unresolved"]?.GetValue<int>() == 0)
                return ("leave_resolved_rewards", proceed);
        }
        if (screen == "NRestSiteRoom" && !enabled.Any(c => Text(c, "type") == "NRestSiteButton") && proceed != null)
            return ("leave_resolved_rest", proceed);

        var eventProceed = enabled.FirstOrDefault(c => Text(c, "type") == "NEventOptionButton" && Flag(c["detail"], "isProceed"));
        if (screen == "NEventRoom" && Flag(state["event"], "finished") && state["shop"] == null && eventProceed != null)
            return ("leave_finished_event", eventProceed);

        var dialogue = enabled.FirstOrDefault(c => Text(c, "ref") == Text(state["dialogue"], "ref"));
        if (_autoSkipDialogue && Flag(state["dialogue"], "canAdvance") && dialogue != null
            && !enabled.Any(c => Text(c, "type") == "NEventOptionButton"))
            return ("advance_dialogue", dialogue);

        return null;
    }

    private static void RetainEventText(JsonObject state, HashSet<string> text)
    {
        if (Text(state, "screen") == "NEventRoom" && Flag(state["event"], "finished")
            && state["shop"] == null && !string.IsNullOrEmpty(Text(state["event"], "description")))
            text.Add(Text(state["event"], "description"));
    }

    private static string? Target(JsonObject state, JsonNode? wanted)
    {
        if (wanted == null)
            return null;

        if (wanted is JsonObject obj)
            return Text(obj, "ref");

        var text = wanted.GetValue<string>();
        var matches = Items(state["combat"]?["enemies"]).Concat(Items(state["combat"]?["allies"]))
            .Where(c => Text(c, "ref") == text || SameName(Text(c, "name"), text)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Target is missing or ambiguous; use a creature object");

        return Text(matches[0], "ref");
    }

    private static JsonObject CardMatch(IEnumerable<JsonObject> cards, JsonNode? wanted, bool selection = false)
    {
        var candidates = cards.ToArray();
        if (wanted is JsonObject)
        {
            var id = Text(wanted, "ref");
            return candidates.Single(c => Text(c, "ref") == id || selection && Text(c, "cardRef") == id);
        }
        var name = wanted?.GetValue<string>();
        var matches = candidates.Where(c => SameName(Text(c, "name", Text(c, "label")), name)).ToArray();
        var variants = matches.Select(c => c["detail"] ?? c)
            .Select(c => new JsonArray(new[]
            {
                "id", "name", "cost", "starCost", "type", "description", "target", "upgraded",
                "enchantment", "affliction", "replay", "upgrade", "canPlay", "reason",
                "legalTargets", "values", "valueContext"
            }.Select(key => Copy(c[key])).ToArray()).ToJsonString()).Distinct().Count();
        if (matches.Length == 0 || variants > 1)
            throw new InvalidOperationException("Card name is missing or has different variants; pass a specific card object");
        return matches[0];
    }

    private static JsonObject Match(IEnumerable<JsonObject> choices, JsonNode? wanted, string reference, string subject)
    {
        var matches = choices.Where(c => wanted is JsonValue
            ? SameName(Text(c, "label"), wanted.GetValue<string>())
                || Text(c, reference) == wanted.GetValue<string>()
            : wanted?[reference] != null && Text(c, reference) == Text(wanted, reference)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"{subject} is missing or ambiguous; pass its current object");
        return matches[0];
    }

    private static JsonObject CurrentChoice(JsonObject state, JsonNode? wanted)
    {
        JsonObject choice;
        try
        {
            choice = Match(Choices(state), wanted, "ref", "Choice");
        }
        catch (InvalidOperationException error) { throw new StaleChoice(error.Message); }

        if (wanted is JsonObject && wanted["label"] != null && Text(wanted, "label") != Text(choice, "label"))
            throw new StaleChoice("Choice now represents different content; observe again");

        if (!Flag(choice, "enabled"))
            throw new StaleChoice(Text(choice, "blockedReason", "That choice is disabled"));

        return choice;
    }

    private static bool CardSelection(JsonObject state) => state["selection"] != null || Text(state, "screen") == "HandSelection"
        || Text(state, "screen") != "NCardRewardSelectionScreen" && Text(state, "screen").Contains("Select")
        && Choices(state).Any(c => Text(c, "type").Contains("CardHolder"));
    private static bool ShopEntrance(JsonObject state) =>
        Text(state, "screen") is "NMerchantRoom" or "NEventRoom" && state["shop"] != null;
    private static bool SameName(string name, string? wanted) =>
        string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<JsonObject> Items(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];
    private static IEnumerable<JsonObject> Choices(JsonObject state) => Items(state["choices"]);
    private static IEnumerable<string> ItemsAsText(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n!.GetValue<string>()) : [];
    private static string Text(JsonNode? node, string key, string fallback = "") => node?[key]?.GetValue<string>() ?? fallback;
    private static bool Flag(JsonNode? node, string key, bool fallback = false) => node?[key]?.GetValue<bool>() ?? fallback;
    private static JsonNode? Copy(JsonNode? node) => node?.DeepClone();
    private static JsonArray List(JsonNode? node) =>
        node is JsonArray array ? array.DeepClone().AsArray() : new JsonArray(Copy(node));
    private static JsonArray Tail(JsonArray array, int start) => new(array.Skip(start).Select(Copy).ToArray());
    private static JsonObject Result(string status, JsonObject? state, string? error = null) => new()
    {
        ["status"] = status,
        ["state"] = Copy(state),
        ["error"] = error
    };
    private static JsonObject Batch(JsonObject result, JsonArray results, JsonArray remaining, bool? endTurn = null)
    {
        var batch = result.DeepClone().AsObject();
        batch["results"] = results.DeepClone();
        batch["remaining"] = remaining.DeepClone();
        if (endTurn != null)
            batch["endTurnPending"] = endTurn;
        return batch;
    }
    private static JsonObject InventoryChoice(JsonObject choice, JsonObject state,
        JsonArray results, JsonArray remaining)
    {
        var blocked = Batch(Result("choice_required", state), results, remaining);
        blocked["blocker"] = new JsonObject
        {
            ["kind"] = "potion_inventory_full",
            ["choice"] = choice.DeepClone()
        };
        return blocked;
    }
    private static WorkflowFailure Failure(JsonObject result, JsonArray results, JsonArray remaining, bool? endTurn = null) => new(Batch(result, results, remaining, endTurn));
    private WorkflowFailure Partial(Exception error, JsonArray results, JsonArray remaining) => Failure(
        error is WorkflowFailure flow ? flow.Outcome : Result("failed", SafeRead(), error: error.GetBaseException().Message), results, remaining);
    private static bool BooleanOrAbsent(JsonNode? node) => node == null
        || node is JsonValue value && value.TryGetValue<bool>(out _);
    private static string Fingerprint(JsonObject state) => new JsonObject(new[]
    {
        "screen", "room", "result", "selection", "preview", "player", "combat", "event"
    }.Select(key => KeyValuePair.Create(key, Copy(state[key]))))
    {
        ["choices"] = new JsonArray(Choices(state).OrderBy(c => Text(c, "ref"), StringComparer.Ordinal).Select(c => (JsonNode?)new JsonObject
        {
            ["ref"] = Copy(c["ref"]),
            ["label"] = Copy(c["label"]),
            ["enabled"] = Copy(c["enabled"]),
            ["selected"] = Copy(c["selected"]),
            ["detail"] = Copy(c["detail"])
        }).ToArray()),
        ["dialogueLine"] = Copy(state["dialogue"]?["line"])
    }.ToJsonString();

    private sealed class WorkflowFailure(JsonObject outcome) : Exception(Text(outcome, "error", Text(outcome, "status")))
    {
        public JsonObject Outcome { get; } = outcome;
    }
    private sealed class StaleChoice(string message) : Exception(message);
}
