using System.Reflection;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace Stagehand.Sts2;

// Game-owned semantics stay outside the reusable Stagehand addon.
public sealed class Sts2Host
{
    private readonly Node _bridge;
    private readonly SceneTree _tree;
    private readonly Dictionary<CardModel, string> _cardRefs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, CardModel> _cards = new();
    private readonly Dictionary<CardModel, string> _deckRefs = new(ReferenceEqualityComparer.Instance);
    private int _nextDeckRef;
    private readonly Dictionary<PotionModel, string> _potionRefs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MerchantEntry, (object? Model, string Ref)> _offerRefs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, MerchantEntry> _offers = new();
    private MerchantInventory? _inventory;
    private ICombatState? _combat;
    private string _combatId = Guid.NewGuid().ToString("N");
    private int _nextCard;
    private string _fingerprint = "";
    private ulong _nextFrameCheck;
    private ulong _revision;
    private Pending? _pending;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Pending
    {
        public string Id = "";
        public string Kind = "";
        public string Status = "accepted";
        public string? Purpose;
        public string? Error;
        public GameAction? Action;
        public PotionModel? Potion;
        public Task? Task;
        public Task<bool>? Purchase;
        public int Round;
        public bool LeftPlayPhase;
    }

    private Sts2Host(Node bridge, SceneTree tree) { _bridge = bridge; _tree = tree; }

    public static void Install(Node bridge, SceneTree tree)
    {
        var host = new Sts2Host(bridge, tree);
        var node = GD.Load<GDScript>("res://stagehand_sts2/sts2.gd").New().As<Node>();
        node.Name = "Sts2";
        node.Set("read_callback", Callable.From(host.Read));
        node.Set("request_callback", Callable.From<string, string>(host.Request));
        node.Set("inspect_callback", Callable.From<string, string>(host.Inspect));
        node.Set("select_card_callback", Callable.From<string, string>(host.SelectCard));
        node.Set("frame_callback", Callable.From(host.Frame));
        bridge.AddChild(node);
        node.Call("configure", bridge);
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    private static object? Get(object? target, string name)
    {
        if (target == null) return null;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try { return target.GetType().GetProperty(name, flags)?.GetValue(target)
            ?? target.GetType().GetField(name, flags)?.GetValue(target); }
        catch (TargetInvocationException) { return null; }
    }
    private static string Text(object? value)
    {
        if (value == null) return "";
        var formatted = value.GetType().GetMethod("GetFormattedText", Type.EmptyTypes);
        var text = formatted?.Invoke(value, null)?.ToString() ?? value.ToString() ?? "";
        text = Regex.Replace(text, @"\[img[^\]]*\](.*?)\[/img\]", m => m.Groups[1].Value.Contains("energy_icon")
            ? " «Energy» " : " " + Path.GetFileNameWithoutExtension(m.Groups[1].Value).Replace('_', ' ') + " ");
        text = Regex.Replace(text, @"\[[^\]]*\]", "").Replace("«Energy»", "[Energy]");
        text = Regex.Replace(text, @"[ \t]+", " ");
        return Regex.Replace(text, @" +([.,!?;:])", "$1").Trim();
    }

    private static object[] Effects(object? tips) => tips is not IEnumerable items ? [] : items.Cast<object>()
        .Select(t => Get(t, "Card") is CardModel card
            ? new { Name = card.Title, Description = CardDescription(card, PileType.None),
                Card = (CardFacts?)CardDetails(card, PileType.None, printed: true), Mechanics = Array.Empty<string>() }
            : new { Name = Text(Get(t, "Title")), Description = Text(Get(t, "Description")),
                Card = (CardFacts?)null, Mechanics = Mechanics(Get(t, "CanonicalModel") as AbstractModel) })
        .Where(t => t.Name.Length > 0 && t.Description.Length > 0).Distinct().Cast<object>().ToArray();

    private static object ModelInfo(object? model) => new { Id = (model as AbstractModel)?.Id.Entry, Name = Text(Get(model, "Title")),
        Description = Text(Get(model, "DynamicDescription")),
        Counter = Get(model, "ShowCounter") is true ? Get(model, "DisplayAmount") : null,
        Status = Get(model, "Status")?.ToString(), UsedUp = Get(model, "IsUsedUp"), Stacks = Get(model, "StackCount"),
        Mechanics = Mechanics(model as AbstractModel),
        Effects = Effects(Get(model, "HoverTipsExcludingRelic") ?? Get(model, "ExtraHoverTips")) };

    private static object? CardModifier(object? modifier) => modifier == null ? null : new {
        Name = Text(Get(modifier, "Title")), Amount = Get(modifier, "Amount"),
        Status = Get(modifier, "Status")?.ToString(),
        Description = Text(Get(modifier, "DynamicDescription")), ExtraCardText = Text(Get(modifier, "DynamicExtraCardText")),
        Effects = Effects(Get(modifier, "HoverTips")) };

    private Player? Player => RunManager.Instance.IsInProgress
        ? LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()!) : null;
    private ICombatState? Combat => CombatManager.Instance.IsInProgress
        ? Player?.Creature.CombatState : null;
    private bool PlayPhase => Combat?.CurrentSide == CombatSide.Player
        && Player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play;
    private bool Idle => RunManager.Instance.ActionExecutor?.IsRunning != true
        && RunManager.Instance.ActionQueueSet?.IsEmpty != false;
    private bool CombatReady => PlayPhase && !CombatManager.Instance.PlayerActionsDisabled && Idle
        && Player?.Creature.IsAlive == true
        && NCombatRoom.Instance?.Ui.Hand is { InCardPlay: false, CurrentMode: NPlayerHand.Mode.Play };

    private void RefreshCombat()
    {
        if (Player == null) _deckRefs.Clear();
        var potions = new HashSet<PotionModel>(Player?.Potions ?? Enumerable.Empty<PotionModel>(), ReferenceEqualityComparer.Instance);
        foreach (var potion in _potionRefs.Keys.ToArray()) if (!potions.Contains(potion)) _potionRefs.Remove(potion);
        var combat = Combat;
        if (ReferenceEquals(combat, _combat)) return;
        _combat = combat;
        _combatId = Guid.NewGuid().ToString("N");
        _cardRefs.Clear(); _cards.Clear(); _nextCard = 0;
    }

    private string CardRef(CardModel card)
    {
        if (!_cardRefs.TryGetValue(card, out var id))
        {
            id = $"{_combatId}:card:{++_nextCard}";
            _cardRefs[card] = id; _cards[id] = card;
        }
        return id;
    }

    private string DeckRef(CardModel card)
    {
        if (!_deckRefs.TryGetValue(card, out var id)) _deckRefs[card] = id = $"deck:{++_nextDeckRef}";
        return id;
    }

    private string CreatureRef(Creature enemy) => $"{_combatId}:creature:{enemy.CombatId}";

    private IEnumerable<Creature> Allies => Combat?.Players.SelectMany(p => new[] { p.Creature }
        .Concat(p.PlayerCombatState?.Pets ?? Array.Empty<Creature>())).Distinct().Where(c => c.IsAlive)
        ?? Enumerable.Empty<Creature>();

    private string PotionRef(PotionModel potion)
    {
        if (!_potionRefs.TryGetValue(potion, out var id)) _potionRefs[potion] = id = "potion:" + Guid.NewGuid().ToString("N");
        return id;
    }

    private Control? ShopkeeperTarget(PotionModel potion) => Combat == null && potion is MegaCrit.Sts2.Core.Models.Potions.FoulPotion
        && Player?.RunState.CurrentRoom is { } room
        ? MegaCrit.Sts2.Core.Models.Potions.FoulPotion.GetFoulPotionMerchantTarget(room).button : null;

    private object[] PotionTargets(PotionModel potion) => ShopkeeperTarget(potion) is { } target && Visible(target)
        ? [new { Ref = _bridge.Call("reference", target).AsString(), Kind = "shopkeeper", Name = "Shopkeeper" }]
        : [];

    private string OfferRef(MerchantEntry entry)
    {
        var model = Get(entry, "Model") ?? Get(Get(entry, "CreationResult"), "Card");
        if (_offerRefs.TryGetValue(entry, out var old) && ReferenceEquals(old.Model, model)) return old.Ref;
        if (old.Ref != null) _offers.Remove(old.Ref);
        var id = "offer:" + Guid.NewGuid().ToString("N");
        _offerRefs[entry] = (model, id); _offers[id] = entry;
        return id;
    }

    private static string CardDescription(CardModel card, PileType pile)
    {
        try { return Text(card.GetDescriptionForPile(pile)); }
        catch (Exception error) { return $"Description unavailable ({error.GetType().Name})"; }
    }

    private sealed record CardCosts(object Energy, object? Stars, string Scope);
    private static CardCosts Costs(CardModel card, bool printed)
    {
        var stars = printed ? card.CurrentStarCost : card.GetStarCostWithModifiers();
        return new(card.EnergyCost.CostsX ? "X" : printed ? card.EnergyCost.GetWithModifiers(CostModifiers.Local) : card.EnergyCost.GetAmountToSpend(),
            card.HasStarCostX ? "X" : stars < 0 ? null : stars, printed ? "printed_related_card" : "current_card");
    }

    private sealed record CardFacts(string Id, string Name, string Description, object Cost, object? StarCost,
        string CostScope, string Type, string Target, bool Upgraded, string[] Mechanics);

    private static CardFacts CardDetails(CardModel card, PileType pile, bool printed = false)
    {
        var costs = Costs(card, printed);
        return new(card.Id.Entry, card.Title, CardDescription(card, pile), costs.Energy, costs.Stars, costs.Scope,
            card.Type.ToString(), card.TargetType.ToString(), card.IsUpgraded, Mechanics(card));
    }

    // Native-backed clarifications for rules that the localized prose leaves
    // implicit. These describe mechanics, not a separate prediction/rules engine.
    private static string[] Mechanics(AbstractModel? model) => model switch {
        MegaCrit.Sts2.Core.Models.Cards.VoidForm or MegaCrit.Sts2.Core.Models.Powers.VoidFormPower => [
            "Waives fixed energy and star costs for the first stated number of manual plays each turn. X costs retain native X spending rules. Already-free cards count; automatic plays do not.",
            "A play consumes its slot after completion, including any required selection. Costs shown during that selection do not predict the next queued card's cost.",
            "Applications add their amounts. Playing Void Form ends the current turn; the free-play counter resets at the next player-turn start."],
        MegaCrit.Sts2.Core.Models.Cards.BeatIntoShape => [
            "Counts other powered Attack damage hits on this enemy this turn, including separate hits of a multi-hit attack. Its own hits are excluded; unpowered pulses such as Black Hole do not count."],
        MegaCrit.Sts2.Core.Models.Cards.AstralPulse => [
            "Both hits belong to one Attack command. Vigor benefits both hits and is consumed after the complete attack."],
        MegaCrit.Sts2.Core.Models.Cards.Radiate => [
            "Hit count uses positive star gains this turn, including starting stars granted by a relic. Spending or waiving stars is not a star gain."],
        MegaCrit.Sts2.Core.Models.Cards.Panache or MegaCrit.Sts2.Core.Models.Powers.PanachePower => [
            "Panache's own play does not count toward its first trigger. Counts subsequent card plays; the displayed counter is cards remaining and resets each turn."],
        MegaCrit.Sts2.Core.Models.Potions.PowerPotion or MegaCrit.Sts2.Core.Models.Potions.AttackPotion or MegaCrit.Sts2.Core.Models.Potions.SkillPotion => [
            "The chosen card has fixed energy and star costs set to zero for this turn. X costs still spend their native X budget; free does not preserve that energy. Offer tooltips retain printed costs; the generated hand card exposes its actual costs."],
        MegaCrit.Sts2.Core.Models.Cards.DeathMarch => [
            "Normal starting-hand draws are excluded. Counts this owner's actual extra draw events this turn, including drawing this card; adding a chosen card to hand is not itself a draw."],
        MegaCrit.Sts2.Core.Models.Cards.Neurosurge or MegaCrit.Sts2.Core.Models.Powers.NeurosurgePower => [
            "Playing the card grants its energy/draw and applies the recurring power, without immediately adding Doom. The power adds self-Doom at subsequent player-turn starts; ordinary Doom death is checked at player-turn end."],
        MegaCrit.Sts2.Core.Models.Powers.DoomPower => [
            "Ordinary Doom checks at the affected creature's own side-turn end and kills when current HP is at or below Doom. Crossing that threshold from enemy damage does not itself trigger the check; explicit Doom-triggering effects can differ."],
        MegaCrit.Sts2.Core.Models.Cards.ReaperForm or MegaCrit.Sts2.Core.Models.Powers.ReaperFormPower => [
            "Uses resolved attack damage including both blocked and unblocked damage, not HP loss or printed damage. This player's and owned pets' powered Attack hits qualify; unpowered damage does not."],
        MegaCrit.Sts2.Core.Models.Relics.IntimidatingHelmet => [
            "For manual plays, the threshold uses the modified energy payment rather than printed cost, so a surcharge can qualify a formerly cheaper card. Automatic plays use their native EnergyValue, which need not equal energy actually spent."],
        MegaCrit.Sts2.Core.Models.Powers.PlatingPower => [
            "For the player: grants current Plating as Block at turn end, before end-of-turn damage effects. Loses one stack at subsequent player-turn starts, not when taking hits."],
        _ => [] };

    private object Card(CardModel card, bool hand = false, bool upgrade = false, bool reference = true, bool deckReference = false)
    {
        var facts = CardDetails(card, hand ? PileType.Hand : PileType.None);
        bool? canPlay = null; string? reason = null;
        if (hand) { canPlay = card.CanPlay(out var why, out _); if (canPlay == false) reason = why.ToString(); }
        return new { Ref = reference ? CardRef(card) : null, DeckRef = deckReference ? DeckRef(card) : null,
            facts.Id, facts.Name, facts.Description, facts.Cost, facts.StarCost, facts.CostScope, facts.Type, facts.Target, facts.Upgraded, facts.Mechanics,
            Replay = card.GetEnchantedReplayCount(),
            CanUpgrade = card.IsUpgradable, CanRemove = card.IsRemovable,
            Upgrade = upgrade && card.IsUpgradable ? UpgradePreview(card) : null,
            LegalTargets = hand && Combat != null && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer or TargetType.Osty
                ? Combat.Enemies.Concat(Allies).Where(e => e.IsAlive && card.CanPlayTargeting(e)).Select(CreatureRef).ToArray() : null,
            CanPlay = canPlay, Reason = reason, Effects = Effects(card.HoverTips),
            Enchantment = CardModifier(card.Enchantment), Affliction = CardModifier(card.Affliction) };
    }

    private object UpgradePreview(CardModel card)
    {
        return Card(CreateUpgradePreview(card), reference: false);
    }

    private static CardModel CreateUpgradePreview(CardModel card)
    {
        // Mirror NUpgradePreview/NCard on a detached clone, outside the run's
        // registries. Dynamic previews include local enchantment modifiers.
        var preview = (CardModel)card.ClonePreservingMutability();
        preview.UpgradeInternal();
        preview.UpgradePreviewType = card.Pile?.IsCombatPile == true ? CardUpgradePreviewType.Combat : CardUpgradePreviewType.Deck;
        preview.DynamicVars.ClearPreview();
        preview.UpdateDynamicVarPreview(CardPreviewMode.Upgrade, null, preview.DynamicVars);
        if (preview.Enchantment != null)
        {
            preview.Enchantment.DynamicVars.ClearPreview();
            preview.UpdateDynamicVarPreview(CardPreviewMode.Upgrade, null, preview.Enchantment.DynamicVars);
        }
        return preview;
    }

    private static object[] Powers(Creature creature) => creature.Powers.Where(p => p.IsVisible)
        .Select(p => (object)new { Name = Text(p.Title), Amount = p.DisplayAmount,
            Description = Text(Get(p.HoverTips.FirstOrDefault(t => t.Id == p.Id.ToString()), "Description") ?? p.SmartDescription),
            Mechanics = Mechanics(p) }).ToArray();

    private object Pet(Creature creature)
    {
        var player = Combat?.Players.FirstOrDefault(p => ReferenceEquals(p.Creature, creature));
        return new { Ref = CreatureRef(creature), Name = player != null ? Text(player.Character.Title) : Text(creature.Monster?.Title),
            Hp = creature.CurrentHp, MaxHp = creature.MaxHp, Block = creature.Block, Alive = creature.IsAlive, Statuses = Powers(creature) };
    }

    private static object Orb(OrbModel orb, int queueIndex)
    {
        var description = orb.SmartDescription;
        description.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
        description.Add("Passive", orb.PassiveVal); description.Add("Evoke", orb.EvokeVal);
        return new { QueuePosition = queueIndex + 1, EvokeNext = queueIndex == 0,
            Name = Text(orb.Title), Passive = orb.PassiveVal, Evoke = orb.EvokeVal,
            Description = Text(description), Effects = Effects(orb.HoverTips) };
    }

    private object Enemy(Creature enemy)
    {
        var intents = new List<object>();
        if (enemy.Monster?.NextMove is { } move && _combat != null)
            foreach (var intent in move.Intents)
            {
                var tip = intent.GetHoverTip(_combat.PlayerCreatures, enemy);
                intents.Add(new { Type = intent.IntentType.ToString(), Label = Text(intent.GetIntentLabel(_combat.PlayerCreatures, enemy)),
                    Title = Text(tip.Title), Description = Text(tip.Description),
                    IsAttack = intent is AttackIntent,
                    DamagePerHit = intent is AttackIntent attack ? (int?)attack.GetSingleDamage(_combat.PlayerCreatures, enemy) : null,
                    Hits = intent is MultiAttackIntent multi ? (int?)multi.Repeats : intent is SingleAttackIntent ? 1 : null,
                    TotalDamage = intent is AttackIntent damage ? (int?)damage.GetTotalDamage(_combat.PlayerCreatures, enemy) : null,
                    DamageScope = intent is AttackIntent ? "native_local_player_preview" : null });
            }
        return new { Ref = CreatureRef(enemy), Name = Text(enemy.Monster?.Title), Hp = enemy.CurrentHp,
            MaxHp = enemy.MaxHp, Block = enemy.Block, Statuses = Powers(enemy), Intents = intents };
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        var queue = new Queue<Node>(); queue.Enqueue(root); var count = 0;
        while (queue.Count > 0 && count++ < 8000)
        {
            var node = queue.Dequeue(); yield return node;
            foreach (var child in node.GetChildren()) queue.Enqueue(child);
        }
    }

    private static bool Visible(Node node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion() || node is not Control control || !control.IsVisibleInTree()) return false;
        for (Node? p = node; p != null; p = p.GetParent())
        {
            if (p is NMerchantInventory { IsOpen: false }) return false;
        }
        return true;
    }

    private static bool HasInk(Node node)
    {
        for (Node? p = node; p != null; p = p.GetParent())
            if (p is CanvasItem canvas && (canvas.Modulate.A == 0 || canvas.SelfModulate.A == 0)) return false;
        return true;
    }

    private static bool IsTimelineOverlay(Node node)
    {
        for (Node? p = node; p != null && p is not NTimelineScreen; p = p.GetParent())
            if (p is NEpochInspectScreen || p.GetType().Name.StartsWith("NUnlock") || p.GetType().Name == "NTimelineTutorial") return true;
        return false;
    }

    private static IEnumerable<string> Labels(Node root) => Walk(root).Where(n => n is RichTextLabel or Label && Visible(n) && HasInk(n))
        .Select(n => n switch { RichTextLabel rich => rich.GetParsedText(), Label label => label.Text, _ => "" })
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(Text).Distinct();

    private Node Scope()
    {
        var root = _tree.CurrentScene ?? _tree.Root;
        var popup = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name.Contains("Ftue"));
        if (popup != null) return popup;
        if (NMapScreen.Instance is { IsOpen: true } map) return map;
        if (NOverlayStack.Instance?.Peek() is Node overlay && Visible(overlay))
        {
            var preview = Walk(overlay).OfType<Control>().FirstOrDefault(c => c.Name.ToString().Contains("PreviewContainer")
                && Visible(c) && Walk(c).Any(n => n is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton { IsEnabled: true } && Visible(n)));
            if (preview != null) return preview;
            return overlay;
        }
        if (Walk(root).OfType<NTimelineScreen>().FirstOrDefault(Visible) is { } timeline)
        {
            if (timeline.CurrentUnlockScreen is { } unlock && Visible(unlock)) return unlock;
            var inspect = Get(timeline, "_inspectScreen") as Control;
            if (inspect != null && Visible(inspect)) return inspect;
            var tutorial = Walk(timeline).FirstOrDefault(n => n.GetType().Name == "NTimelineTutorial" && Visible(n));
            return tutorial ?? timeline;
        }
        foreach (var type in new[] { "NCardGridSelectionScreen", "NCharacterSelectScreen", "NSingleplayerSubmenu", "NSettingsScreen" })
        {
            var screen = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name == type);
            if (screen != null) return screen;
        }
        var roomType = Player?.RunState.CurrentRoom?.GetType().Name;
        var inventory = Walk(root).OfType<NMerchantInventory>().FirstOrDefault(i => i.IsOpen && Visible(i));
        if (inventory != null) return inventory;
        if (Combat != null && NCombatRoom.Instance is { } activeCombat && Visible(activeCombat)) return activeCombat;
        if (roomType != null)
        {
            var room = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name == "N" + roomType);
            if (room != null) return room;
        }
        return root;
    }

    private static Control Hitbox(Control control)
    {
        var hitbox = control switch {
            NCardHolder holder => holder.Hitbox,
            NCardBundle bundle => bundle.Hitbox,
            NMerchantSlot slot => slot.Hitbox, _ => control };
        return GodotObject.IsInstanceValid(hitbox) ? hitbox : control;
    }

    private object Choice(Control control, bool skipped = false)
    {
        var type = control.GetType().Name;
        var hitbox = Hitbox(control);
        var rect = hitbox.GetGlobalRect();
        var label = string.Join(" · ", Labels(control).Take(6));
        var enabled = hitbox is not NClickableControl clickable || clickable.IsEnabled;
        string? blockedReason = null;
        string? blockedBy = null;
        if (control is BaseButton button && button.Disabled) enabled = false;
        if (!enabled && NGame.Instance?.MainMenu is { } menu && SaveManager.Instance.GetDiscoveredEpochCount() > 0
            && !SaveManager.Instance.HasRunSave && new[] { "_singleplayerButton", "_multiplayerButton", "_compendiumButton" }
                .Any(field => ReferenceEquals(Get(menu, field), control)))
            blockedReason = "Reveal the obtained Epochs in the Timeline before starting another run";
        object? detail = null;
        object? activation = null;
        if (control is NCardHolder holder && holder.CardModel is { } card)
        {
            label = card.Title; detail = Card(card, upgrade: NOverlayStack.Instance?.Peek() is NDeckUpgradeSelectScreen
                || NCombatRoom.Instance?.Ui.Hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect);
            if (holder is NPreviewCardHolder) enabled = false;
            else
            {
                Node target = holder; var signal = NCardHolder.SignalName.Pressed.ToString();
                if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen screen)
                {
                    var grid = Walk(screen).OfType<NCardGrid>().FirstOrDefault();
                    if (grid != null) { target = grid; signal = NCardGrid.SignalName.HolderPressed.ToString(); }
                }
                activation = new { Target = _bridge.Call("reference", target).AsString(), Signal = signal,
                    Argument = _bridge.Call("reference", holder).AsString() };
            }
        }
        else if (control is NCardBundle bundle)
        { label = string.Join(" + ", bundle.Bundle.Select(c => c.Title)); detail = new { Cards = bundle.Bundle.Select(c => Card(c)).ToArray() }; }
        else if (control is NCharacterSelectButton character && character.Character is { } model)
        { label = Text(model.Title); enabled &= !character.IsLocked;
            detail = new { Id = model.Id.Entry, Locked = character.IsLocked, Random = character.IsRandom,
                Hp = character.IsRandom ? (int?)null : model.StartingHp,
                Gold = character.IsRandom ? (int?)null : model.StartingGold,
                Energy = character.IsRandom ? (int?)null : model.MaxEnergy,
                Description = Text(new LocString("characters", model.CharacterSelectDesc)),
                StartingRelics = character.IsRandom ? null : model.StartingRelics.Select(ModelInfo).ToArray() }; }
        else if (control is NMapPoint point && point.Point is { } p)
        { label = $"{p.PointType} ({p.coord.col},{p.coord.row})"; enabled = point.State.ToString() == "Travelable"; }
        else if (control is NEpochSlot epoch)
        {
            var state = epoch.State.ToString();
            var info = epoch.model.UnlockInfo;
            info.Add("IsRevealed", state == "Complete");
            label = Text(epoch.model.Title);
            detail = new { Id = epoch.model.Id, State = state, Description = Text(info),
                Era = Get(epoch, "_era")?.ToString(), Position = epoch.eraPosition };
            if (state == "NotObtained") { enabled = false; blockedReason = "Epoch has not been obtained"; }
        }
        var option = Get(control, "Option");
        if (option != null)
        {
            label = Text(Get(option, "Title"));
            detail = new { Description = Text(Get(option, "Description")), Effects = Effects(Get(option, "HoverTips")),
                IsProceed = Get(option, "IsProceed"), WasChosen = Get(option, "WasChosen") };
            if (Get(option, "IsLocked") is true || Get(option, "IsEnabled") is false) enabled = false;
        }
        var entry = Get(control, "Entry");
        if (entry != null)
        {
            var model = Get(entry, "Model") ?? Get(Get(entry, "CreationResult"), "Card");
            detail = new { Price = Get(entry, "Cost"), Stocked = Get(entry, "IsStocked"),
                Item = model is CardModel m ? Card(m) : ModelInfo(model) };
            label = Text(Get(model, "Title"));
            if (Get(entry, "EnoughGold") is false || Get(entry, "IsStocked") is false) enabled = false;
            if (control is NMerchantPotion && Player?.HasOpenPotionSlots == false)
            { enabled = false; blockedBy = "potion_inventory_full"; blockedReason = "Potion inventory is full; use or discard a potion first"; }
            if (control is NMerchantCardRemoval) { label = Text(Get(control, "Title"));
                var description = Get(control, "Description") as MegaCrit.Sts2.Core.Localization.LocString;
                description?.Add("Amount", MerchantCardRemovalEntry.PriceIncrease);
                detail = new { Price = Get(entry, "Cost"), Stocked = Get(entry, "IsStocked"),
                    Description = Text(description), Category = "card_removal" }; }
        }
        var relic = Get(Get(control, "Relic"), "Model");
        if (relic != null) { label = Text(Get(relic, "Title")); detail = ModelInfo(relic); }
        var reward = Get(control, "Reward");
        if (reward != null)
        {
            detail = new { Category = reward.GetType().Name, Amount = Get(reward, "Amount"), Description = Text(Get(reward, "Description")),
                Potion = Get(reward, "Potion") is { } rp ? ModelInfo(rp) : null,
                Relic = Get(reward, "IsPopulated") is true && Get(reward, "Relic") is { } rr ? ModelInfo(rr) : null };
            if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward && Player?.HasOpenPotionSlots == false)
            { enabled = false; blockedBy = "potion_inventory_full"; blockedReason = "Potion inventory is full; use or discard a potion first"; }
        }
        if (control is NCrystalSphereCell cell)
        {
            var entity = cell.Entity;
            label = $"Cell ({entity.X},{entity.Y})";
            enabled &= entity.IsHidden;
            detail = new { X = entity.X, Y = entity.Y, Hidden = entity.IsHidden,
                Highlighted = entity.IsHighlighted, RevealedItem = !entity.IsHidden ? entity.Item?.GetType().Name : null };
        }
        if (type == "NDivinationButton")
            detail = new { Tool = control.Name.ToString().Contains("Big") ? "big" : "small",
                Active = control.GetNodeOrNull<Control>("%Outline")?.Visible == true };
        return new { Ref = _bridge.Call("reference", hitbox).AsString(), Label = string.IsNullOrEmpty(label) ? control.Name.ToString() : label,
            Type = type, Enabled = enabled, BlockedReason = blockedReason, BlockedBy = blockedBy, Skipped = skipped,
            Selected = Get(control, "IsSelected"), Detail = detail,
            CardRef = control is NCardHolder cardHolder && cardHolder.CardModel != null ? CardRef(cardHolder.CardModel) : null,
            IsSkip = Get(control, "IsSkip"),
            Activation = activation,
            OfferRef = entry is MerchantEntry merchantEntry ? OfferRef(merchantEntry) : null,
            Rect = new[] { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y } };
    }

    private static object? Result(Node scope)
    {
        if (scope.GetType().Name != "NGameOverScreen") return null;
        var history = (RunHistory)Get(scope, "_history")!;
        var run = (RunState)Get(scope, "_runState")!;
        var serial = (SerializableRun)Get(scope, "_serializableRun")!;
        var player = (Player)Get(scope, "_localPlayer")!;
        var earned = player.DiscoveredEpochs.Count;
        var continueEnabled = Get(scope, "_continueButton") is NClickableControl { IsEnabled: true } next && Visible(next);
        var menuEnabled = Get(scope, "_mainMenuButton") is NClickableControl { IsEnabled: true } menu && Visible(menu);
        var leaderboardEnabled = Get(scope, "_leaderboardButton") is NClickableControl { IsEnabled: true } board && Visible(board);
        return new { Win = history.Win, Score = Get(scope, "_score"),
            Stats = new { FloorsClimbed = run.TotalFloor,
                GoldGained = serial.MapPointHistory.SelectMany(a => a).Sum(e => e.GetEntry(player.NetId).GoldGained),
                ElitesKilled = ScoreUtility.GetElitesKilledCount(serial.MapPointHistory),
                BossesSlain = ScoreUtility.GetBossesSlainCount(serial.MapPointHistory, history.Win), Ascension = history.Ascension },
            EarnedEpochCount = earned, PendingEpochReveals = SaveManager.Instance.GetDiscoveredEpochCount(),
            ScoreUnlocksRemaining = SaveManager.Instance.GetUnlocksRemaining(),
            Phase = ResultPhase(Get(scope, "_isAnimatingSummary") is true, continueEnabled, menuEnabled, leaderboardEnabled, earned) };
    }

    private static string ResultPhase(bool animating, bool continueEnabled, bool menuEnabled, bool leaderboardEnabled, int earned) =>
        animating ? "summary_animation" : menuEnabled ? earned > 0 ? "earned_epochs" : "summary"
        : leaderboardEnabled ? "leaderboard" : continueEnabled ? "outcome" : "transition";

    // The grid owns the full filtered list. Combat-pile screens deliberately
    // leave their inherited list empty; displayed holders are virtualized.
    private static CardModel[] EligibleCards(NCardGridSelectionScreen screen) =>
        (Get(Get(screen, "_grid"), "_cards") as IEnumerable)?.Cast<object>().OfType<CardModel>().ToArray() ?? [];

    private static HashSet<CardModel> SelectedCards(object? owner) => new(
        (Get(owner, "_selectedCards") as IEnumerable)?.Cast<object>()
            .Select(c => c as CardModel ?? Get(c, "Model") as CardModel).OfType<CardModel>() ?? [],
        ReferenceEqualityComparer.Instance);

    private object[] GridChoices(NCardGridSelectionScreen screen)
    {
        var selected = SelectedCards(screen);
        // Eligible model order can originate in a shuffled pile; holder allocation
        // and native storage order must not reveal that order through references.
        return PublicCardList.Read(EligibleCards(screen), c => Serialize(Card(c, reference: false)), c => (object)new {
            Ref = CardRef(c), CardRef = CardRef(c), Label = c.Title, Type = "NGridCardHolder", Enabled = true,
            Selected = selected.Contains(c), Detail = Card(c, upgrade: screen is NDeckUpgradeSelectScreen),
            Activation = new { Kind = "select_card", Card = CardRef(c) } });
    }

    public string SelectCard(string json)
    {
        try
        {
            RefreshCombat();
            using var document = JsonDocument.Parse(json);
            var id = document.RootElement.GetProperty("card").GetString()!;
            if (Scope() is not NCardGridSelectionScreen screen || !Visible(screen)
                || NGame.Instance?.Transition?.InTransition == true)
                throw new InvalidOperationException("No active eligible-card selection");
            if (!_cards.TryGetValue(id, out var card) || !EligibleCards(screen).Contains(card))
                throw new InvalidOperationException("Card is not eligible in the current selection");
            var method = screen.GetType().GetMethod("OnCardClicked", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Native card selection handler is unavailable");
            // Same handler invoked by the native grid's HolderPressed connection.
            method.Invoke(screen, [card]);
            return Serialize(new { Status = "submitted" });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private object? VisibleBoss(NMapPoint point)
    {
        if (point is not NBossMapPoint || Player?.RunState is not { } run) return null;
        var encounter = ReferenceEquals(point.Point, run.Map.SecondBossMapPoint) ? run.Act.SecondBossEncounter : run.Act.BossEncounter;
        return encounter == null ? null : new { Id = encounter.Id.Entry, Name = Text(Get(encounter, "Title")) };
    }

    public string Read()
    {
        try
        {
            RefreshCombat(); UpdatePending();
            var scope = Scope(); var player = Player; var combat = Combat;
            var timeline = Walk(_tree.CurrentScene ?? _tree.Root).OfType<NTimelineScreen>().FirstOrDefault(Visible);
            var inspectEpoch = timeline == null ? null : Get(timeline, "_inspectScreen") as Control;
            var timelineBusy = timeline != null && Get(timeline, "_inputBlocker") is not Control { Visible: false };
            var isUnlock = timeline?.CurrentUnlockScreen != null && ReferenceEquals(scope, timeline.CurrentUnlockScreen);
            var menu = NGame.Instance?.MainMenu;
            var canNavigate = player == null && !scope.GetType().Name.Contains("Ftue")
                && GodotObject.IsInstanceValid(menu) && Visible(menu!)
                && (timeline == null || !timelineBusy && inspectEpoch?.Visible != true
                    && !timeline.IsScreenQueued() && timeline.CurrentUnlockScreen == null);
            var rewardButtons = (Get(scope, "_rewardButtons") as IEnumerable)?.Cast<Control>().ToArray();
            var skippedRewards = new HashSet<Control>((Get(scope, "_skippedRewardButtons") as IEnumerable)?.Cast<Control>() ?? [],
                ReferenceEqualityComparer.Instance);
            var inventory = (scope as NMerchantInventory)?.Inventory;
            if (inventory != null && !ReferenceEquals(inventory, _inventory) || player == null)
            { _inventory = inventory; _offerRefs.Clear(); _offers.Clear(); }
            var merchantScene = NMerchantRoom.Instance;
            if (!GodotObject.IsInstanceValid(merchantScene) || !ReferenceEquals(merchantScene?.Room, player?.RunState.CurrentRoom)) merchantScene = null;
            var fakeMerchant = Walk(_tree.CurrentScene ?? _tree.Root).FirstOrDefault(n => n.GetType().Name == "NFakeMerchant" && Visible(n));
            var shopOwner = merchantScene as Node ?? fakeMerchant;
            var shopInventory = merchantScene?.Inventory ?? Get(fakeMerchant, "Inventory") as NMerchantInventory;
            var fakeButton = Get(fakeMerchant, "MerchantButton") as NClickableControl;
            var controls = Walk(scope).OfType<Control>().Where(c => {
                if (!GodotObject.IsInstanceValid(c)) return false;
                var offered = Visible(c) && (scope is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen && Get(c, "Reward") != null
                    || scope is NMerchantInventory && c is NMerchantSlot { Entry: not null }
                    || scope is not NCombatRoom && c is NCardHolder { CardModel: not null });
                return offered || Visible(c) && Hitbox(c).GetGlobalRect().HasArea();
            }).ToList();
            if (scope is NTimelineScreen) controls.RemoveAll(IsTimelineOverlay);
            if (scope.GetType().Name is "NMerchantRoom") controls.RemoveAll(c => HasParentType(c, "NMerchantInventory"));
            var choices = controls.Where(c => c is NClickableControl or BaseButton or NCardHolder or NMapPoint or NMerchantSlot or NCardBundle)
                .Where(c => !isUnlock || c.GetType().Name == "NUnlockConfirmButton")
                .Where(c => c is NCardHolder or NMerchantSlot or NCardBundle || !HasChoiceParent(c))
                .Where(c => !(CombatReady && HasParentType(c, "NPlayerHand")))
                .Where(c => scope is not NCardGridSelectionScreen || c is not NCardHolder)
                .Take(200).Select(c => Choice(c, skippedRewards.Contains(c))).ToArray();
            if (scope is NCardGridSelectionScreen gridScreen) choices = choices.Concat(GridChoices(gridScreen)).ToArray();
            var map = scope is NMapScreen ? Walk(scope).OfType<NMapPoint>().Where(p => p.Point != null)
                .Select(p => new { Ref = _bridge.Call("reference", p).AsString(), Col = p.Point!.coord.col, Row = p.Point.coord.row,
                    Type = p.Point.PointType.ToString(), Boss = VisibleBoss(p), State = p.State.ToString(),
                    LeadsTo = p.Point.Children.Select(c => new { Col = c.coord.col, Row = c.coord.row }).ToArray() }).ToArray() : null;
            var pending = _pending == null ? null : new { Action = _pending.Id, Kind = _pending.Kind, Purpose = _pending.Purpose,
                Status = _pending.Status, Error = _pending.Error };
            var handSelection = combat != null && NCombatRoom.Instance?.Ui.Hand.CurrentMode != NPlayerHand.Mode.Play;
            var selectionHost = handSelection ? NCombatRoom.Instance?.Ui.Hand as object : NOverlayStack.Instance?.Peek() as object;
            var prefs = Get(selectionHost, "_prefs");
            var selected = Get(selectionHost, "_selectedCards") as IEnumerable;
            var combatView = combat != null && scope is NCombatRoom && !handSelection;
            var enabledChoice = choices.Any(c => Get(c, "Enabled") is true);
            var waitingForRewards = player != null && ReferenceEquals(scope, _tree.CurrentScene);
            waitingForRewards |= combat == null && scope is NCombatRoom;
            var room = player?.RunState.CurrentRoom;
            var eventModel = (Get(room, "LocalMutableEvent") ?? Get(room, "CanonicalEvent")) as EventModel;
            var dialogueLayout = Walk(scope).OfType<NAncientEventLayout>().FirstOrDefault();
            var dialogueHitbox = Get(dialogueLayout, "_dialogueHitbox") as Control;
            object? dialogue = dialogueLayout == null ? null : new {
                Source = "ancient_dialogue", Line = Get(dialogueLayout, "_currentDialogueLine"),
                LastLine = Get(dialogueLayout, "IsDialogueOnLastLine"),
                CanAdvance = dialogueHitbox != null && Visible(dialogueHitbox) && dialogueHitbox is NClickableControl { IsEnabled: true },
                Ref = dialogueHitbox != null && Visible(dialogueHitbox) ? _bridge.Call("reference", dialogueHitbox).AsString() : null,
                Text = Labels(dialogueLayout).ToArray() };
            // Architect speech uses combat-layout bubbles rather than an ancient
            // event layout. Read only the current visible bubble, never future lines.
            if (eventModel is MegaCrit.Sts2.Core.Models.Events.TheArchitect && Get(eventModel, "_speechBubble") is Node bubble
                && GodotObject.IsInstanceValid(bubble) && !bubble.IsQueuedForDeletion())
            {
                var spoken = Labels(bubble).ToArray();
                if (spoken.Length > 0) dialogue = new { Source = "speech_bubble", Line = Get(eventModel, "_currentLineIndex"),
                    LastLine = Get(eventModel, "IsOnLastLine"), CanAdvance = false, Text = spoken };
            }
            var boundRoom = scope is NMerchantRoom merchant ? merchant.Room : Get(scope, "_room");
            if (boundRoom != null && !ReferenceEquals(boundRoom, room)) waitingForRewards = true;
            if (scope is NEventRoom)
            {
                var expected = Get(room, "LocalMutableEvent") ?? Get(room, "CanonicalEvent");
                waitingForRewards |= expected != null && !ReferenceEquals(Get(scope, "_event"), expected);
            }
            // Card-selection hooks can await input before normal drawing, while
            // the action executor remains busy in the player's Start phase.
            var ready = combatView ? CombatReady : enabledChoice && (Idle || IsCardSelection(scope)
                || RunManager.Instance.ActionExecutor?.IsPaused == true || _pending?.Status == "choice_required")
                && !waitingForRewards && (scope is not NMapScreen m || !m.IsTraveling && m.IsTravelEnabled);
            ready &= NGame.Instance?.Transition?.InTransition != true;
            if (scope.GetType().Name == "NGameOverScreen")
                ready &= Walk(scope).OfType<NClickableControl>().Any(c => c.IsEnabled && Visible(c)
                    && c.GetType().Name is "NGameOverContinueButton" or "NReturnToMainMenuButton");
            if (scope is NEpochInspectScreen)
                ready &= Get(scope, "_closeButton") is NClickableControl { IsEnabled: true } close && Visible(close);
            if (scope is NTimelineScreen) ready &= !timelineBusy;
            if (scope is NCharacterSelectScreen)
                ready &= SaveManager.Instance.Progress.PendingCharacterUnlock == ModelId.none
                    && Get(scope, "_lobby") != null
                    && choices.Any(c => Get(c, "Type") as string == "NCharacterSelectButton" && Get(c, "Enabled") is true);
            if (scope is NCrystalSphereScreen && Get(Get(scope, "_entity"), "IsFinished") is true)
                ready &= choices.Any(c => Get(c, "Type") as string == "NProceedButton" && Get(c, "Enabled") is true);
            if (isUnlock) ready &= choices.Any(c => Get(c, "Enabled") is true && Get(c, "Type") as string == "NUnlockConfirmButton");
            if (scope is NMerchantRoom && merchantScene?.Inventory == null) ready = false;
            var preview = scope.Name.ToString().Contains("PreviewContainer");
            return Serialize(new { Screen = preview ? "CardPreview" : handSelection ? "HandSelection" : scope.GetType().Name,
                Preview = preview ? Walk(scope).OfType<NCardHolder>().Select(h => h.CardModel).OfType<CardModel>().Select(c => Card(c)).ToArray() : null,
                Result = Result(scope),
                Navigation = !GodotObject.IsInstanceValid(menu) || !Visible(menu!) ? null : new {
                    AtMainMenu = canNavigate && menu!.SubmenuStack.Peek() == null, CanReturnToMenu = canNavigate,
                    PendingEpochReveals = SaveManager.Instance.GetDiscoveredEpochCount(),
                    MenuStack = _bridge.Call("reference", menu.SubmenuStack).AsString() },
                Timeline = timeline == null ? null : new { Busy = timelineBusy,
                    BackEnabled = Get(timeline, "_backButton") is NClickableControl { IsEnabled: true },
                    CanReturnToMenu = canNavigate },
                Epoch = scope is not NEpochInspectScreen ? null : new {
                    Title = Text(Get(Get(scope, "_epoch"), "Title")), Description = Text(Get(Get(scope, "_epoch"), "Description")),
                    Unlocks = Text(Get(Get(scope, "_epoch"), "UnlockText")) },
                Unlock = !isUnlock ? null : new { Text = Labels(scope).ToArray(),
                    Cards = controls.OfType<NCardHolder>().Where(c => c.CardModel != null).Select(c => Card(c.CardModel!)).ToArray(),
                    Relics = controls.Select(c => Get(Get(c, "Relic"), "Model")).OfType<RelicModel>().Select(ModelInfo).ToArray(),
                    Potions = (Get(scope, "_potions") as IEnumerable)?.Cast<object>().OfType<PotionModel>().Select(ModelInfo).ToArray(),
                    Character = Get(scope, "_character") is not CharacterModel unlocked ? null : new {
                        Id = unlocked.Id.Entry, Name = Text(unlocked.Title) } },
                Room = player?.RunState.CurrentRoom?.GetType().Name,
                Selection = prefs == null ? null : new { Prompt = Text(Get(prefs, "Prompt")), Min = Get(prefs, "MinSelect"), Max = Get(prefs, "MaxSelect"),
                    Confirm = Get(prefs, "RequireManualConfirmation"), CanCancel = Get(prefs, "Cancelable"),
                    Selected = selected?.Cast<object>().Select(c => c as CardModel ?? Get(c, "Model") as CardModel)
                        .OfType<CardModel>().Select(c => Card(c)).ToArray() },
                Shop = shopOwner == null ? null : new { Control = _bridge.Call("reference", shopOwner).AsString(),
                    Kind = merchantScene != null ? "ordinary" : "fake", Open = shopInventory?.IsOpen == true,
                    CanBrowse = shopInventory != null && (merchantScene != null || fakeButton is { IsEnabled: true } && Visible(fakeButton)) },
                Rewards = scope is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen ? new {
                    Complete = Get(scope, "IsComplete"), Remaining = rewardButtons?.Length,
                    Unresolved = rewardButtons?.Count(c => !skippedRewards.Contains(c)) } : null,
                CrystalSphere = scope is NCrystalSphereScreen ? new {
                    Remaining = Get(Get(scope, "_entity"), "DivinationCount"),
                    Tool = Get(Get(scope, "_entity"), "CrystalSphereTool")?.ToString(),
                    Finished = Get(Get(scope, "_entity"), "IsFinished"),
                    RevealedItems = (Get(Get(scope, "_entity"), "_revealed") as IEnumerable)?.Cast<object>().Select(item => new {
                        Type = item.GetType().Name,
                        X = Get(item, "Position") is Vector2I position ? position.X : (int?)null,
                        Y = Get(item, "Position") is Vector2I location ? location.Y : (int?)null,
                        Width = Get(item, "Size") is Vector2I size ? size.X : (int?)null,
                        Height = Get(item, "Size") is Vector2I dimensions ? dimensions.Y : (int?)null }).ToArray() } : null,
                Event = eventModel == null ? null : new { Title = Text(eventModel.Title),
                    Description = eventModel is AncientEventModel ? null : Text(eventModel.Description), Finished = eventModel.IsFinished },
                Dialogue = dialogue,
                Combat = combat == null ? null : new { Ref = _combatId, Round = combat.RoundNumber, Side = combat.CurrentSide.ToString(),
                    Phase = player?.PlayerCombatState?.Phase.ToString(), Ready = CombatReady,
                    DrawCount = player!.PlayerCombatState!.DrawPile.Cards.Count,
                    DiscardCount = player.PlayerCombatState.DiscardPile.Cards.Count,
                    ExhaustCount = player.PlayerCombatState.ExhaustPile.Cards.Count,
                    OrbSlots = player.PlayerCombatState.OrbQueue.Capacity, OrbOrder = "evoke_next_first",
                    Orbs = player.PlayerCombatState.OrbQueue.Orbs.Select(Orb).ToArray(),
                    Pets = player.PlayerCombatState.Pets.Select(Pet).ToArray(),
                    Allies = Allies.Select(Pet).ToArray(),
                    Hand = player!.PlayerCombatState!.Hand.Cards.Select(c => Card(c, true)).ToArray(),
                    Enemies = combat.Enemies.Where(c => c.IsAlive).Select(Enemy).ToArray(),
                    Ongoing = true, NoLivingEnemies = !combat.Enemies.Any(c => c.IsAlive),
                    InactiveEnemies = combat.Enemies.Where(c => !c.IsAlive).Select(c => new {
                        Ref = CreatureRef(c), Name = Text(c.Monster?.Title), Alive = false, Hp = c.CurrentHp,
                        Statuses = Powers(c) }).ToArray() },
                Player = player == null ? null : new { Ref = combat != null ? CreatureRef(player.Creature) : null,
                    Name = Text(player.Character.Title), Hp = player.Creature.CurrentHp,
                    MaxHp = player.Creature.MaxHp, Block = player.Creature.Block, Gold = player.Gold,
                    Energy = combat == null ? (int?)null : player.PlayerCombatState?.Energy,
                    MaxEnergy = combat == null ? (int?)null : player.PlayerCombatState?.MaxEnergy,
                    Stars = combat == null ? (int?)null : player.PlayerCombatState?.Stars,
                    Statuses = Powers(player.Creature),
                    MaxPotions = player.MaxPotionCount,
                    Potions = player.PotionSlots.Select(p => p == null ? null : new { Ref = PotionRef(p),
                        Name = Text(p.Title), Description = Text(p.DynamicDescription), Target = p.TargetType.ToString(),
                        Targets = PotionTargets(p),
                        Usable = !p.IsQueued && p.PassesCustomUsabilityCheck && p.Usage != PotionUsage.Automatic
                            && (p.Usage != PotionUsage.CombatOnly || CombatReady), Effects = Effects(p.ExtraHoverTips),
                        Mechanics = Mechanics(p) }).Where(p => p != null).ToArray(),
                    Relics = player.Relics.Select(ModelInfo).ToArray() },
                Act = player?.RunState.CurrentActIndex + 1, Floor = player?.RunState.ActFloor,
                TotalFloor = player?.RunState.TotalFloor, Ascension = Get(player?.RunState, "AscensionLevel"),
                Ready = ready, Pending = pending, Choices = choices, Text = Labels(scope).Take(100).ToArray(), Map = map,
                MapControl = scope is NMapScreen ? _bridge.Call("reference", scope).AsString() : null });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private static bool HasParentType(Node node, string type)
    { for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p.GetType().Name == type) return true; return false; }
    private static bool HasChoiceParent(Node node)
    { for (var p = node.GetParent(); p != null; p = p.GetParent()) if (p is NCardHolder or NMerchantSlot or NCardBundle) return true; return false; }

    public string Inspect(string json)
    {
        try
        {
            RefreshCombat();
            var player = Player ?? throw new InvalidOperationException("No run in progress");
            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.GetProperty("kind").GetString();
            if (kind == "deck")
            {
                var upgrades = document.RootElement.TryGetProperty("upgrades", out var option) && option.ValueKind == JsonValueKind.True;
                foreach (var old in _deckRefs.Keys.ToArray()) if (!player.Deck.Cards.Contains(old)) _deckRefs.Remove(old);
                return Serialize(new { Cards = player.Deck.Cards.Select(c => Card(c, upgrade: upgrades, deckReference: true)).ToArray() });
            }
            if (kind != "piles") throw new InvalidOperationException("Unknown inspection");
            if (Combat == null) throw new InvalidOperationException("No active combat");
            var state = player.PlayerCombatState!;
            // The UI permits pile inspection; the shuffled draw order is not visible.
            var draw = PublicCardList.Read(state.DrawPile.Cards,
                c => Serialize(Card(c, reference: false)), c => Card(c));
            return Serialize(new { Draw = new { Order = "unknown", Cards = draw },
                Discard = state.DiscardPile.Cards.Select(c => Card(c)).ToArray(),
                Exhaust = state.ExhaustPile.Cards.Select(c => Card(c)).ToArray() });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    public string Request(string json)
    {
        Pending? submitted = null;
        try
        {
            RefreshCombat(); UpdatePending();
            using var document = JsonDocument.Parse(json); var args = document.RootElement;
            var kind = args.GetProperty("kind").GetString()!;
            if (_pending?.Status is "accepted" or "running" or "choice_required") throw new InvalidOperationException("An action is still pending");
            var player = Player ?? throw new InvalidOperationException("No run in progress");
            var pending = new Pending { Id = Guid.NewGuid().ToString("N"), Kind = kind, Round = Combat?.RoundNumber ?? 0 };
            if (kind == "play")
            {
                if (!CombatReady) throw new InvalidOperationException("Combat is not ready for a card decision");
                var id = args.GetProperty("card").GetString()!;
                if (!_cards.TryGetValue(id, out var card) || !player.PlayerCombatState!.Hand.Cards.Any(c => ReferenceEquals(c, card)))
                    throw new InvalidOperationException("Card reference is stale or no longer in hand");
                if (!card.CanPlay(out var reason, out _)) throw new InvalidOperationException("Card cannot be played: " + reason);
                var target = Target(card.TargetType, args, player);
                if (target != null && !card.CanPlayTargeting(target)) throw new InvalidOperationException("Card cannot be played on that target");
                pending.Action = new PlayCardAction(card, target);
                _pending = submitted = pending;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(pending.Action);
            }
            else if (kind == "endTurn")
            {
                if (!CombatReady) throw new InvalidOperationException("Combat is not ready to end turn");
                _pending = submitted = pending; PlayerCmd.EndTurn(player, canBackOut: false);
            }
            else if (kind == "abandonRun")
            {
                if (RunManager.Instance.NetService.Type.ToString() != "Singleplayer")
                    throw new InvalidOperationException("Abandon is supported only for an active single-player run");
                _pending = submitted = pending;
                // The native confirmation popup delegates to this same operation.
                RunManager.Instance.Abandon();
            }
            else if (kind == "buy")
            {
                var id = args.GetProperty("offer").GetString()!;
                var inventory = (Scope() as NMerchantInventory)?.Inventory;
                if (inventory == null || !ReferenceEquals(inventory, _inventory)
                    || !_offers.TryGetValue(id, out var entry) || !inventory.AllEntries.Any(e => ReferenceEquals(e, entry)))
                    throw new InvalidOperationException("Shop offer is stale or no longer belongs to the current shop");
                if (OfferRef(entry) != id) throw new InvalidOperationException("The shop has restocked this offer; inspect again");
                if (!entry.IsStocked || !entry.EnoughGold) throw new InvalidOperationException("Offer is sold out or unaffordable");
                _pending = submitted = pending;
                pending.Purpose = entry is MerchantCardRemovalEntry ? "card_removal" : null;
                pending.Purchase = entry.OnTryPurchaseWrapper(inventory);
                pending.Task = pending.Purchase;
            }
            else if (kind == "divineCell")
            {
                if (Scope() is not NCrystalSphereScreen screen || Get(Get(screen, "_entity"), "IsFinished") is true)
                    throw new InvalidOperationException("No active Crystal Sphere decision");
                var x = args.GetProperty("x").GetInt32(); var y = args.GetProperty("y").GetInt32();
                var cell = Walk(screen).OfType<NCrystalSphereCell>().SingleOrDefault(c => c.Entity.X == x && c.Entity.Y == y);
                if (cell == null || !Visible(cell) || !cell.Entity.IsHidden)
                    throw new InvalidOperationException("Cell is missing, hidden from the UI, or already revealed");
                _pending = submitted = pending;
                pending.Task = (Task)typeof(NCrystalSphereScreen).GetMethod("OnCellClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(screen, new object[] { cell })!;
            }
            else if (kind is "potion" or "discardPotion")
            {
                var id = args.GetProperty("potion").GetString();
                var potion = player.Potions.FirstOrDefault(p => PotionRef(p) == id)
                    ?? throw new InvalidOperationException("Potion reference is stale");
                if (kind == "discardPotion")
                {
                    if (!player.CanRemovePotions || potion.IsQueued) throw new InvalidOperationException("Potion cannot be discarded now");
                    pending.Potion = potion; _pending = submitted = pending;
                    pending.Task = PotionCmd.Discard(potion);
                    return Serialize(new { Action = pending.Id, Status = "accepted" });
                }
                if (potion.IsQueued || !potion.PassesCustomUsabilityCheck || potion.Usage == PotionUsage.Automatic)
                    throw new InvalidOperationException("Potion cannot be used now");
                if (potion.Usage == PotionUsage.CombatOnly && !CombatReady) throw new InvalidOperationException("Potion requires ready combat");
                if (Combat != null && !CombatReady) throw new InvalidOperationException("Combat is not ready for a potion decision");
                if (potion.TargetType == TargetType.TargetedNoCreature)
                {
                    var shopkeeper = ShopkeeperTarget(potion);
                    if (shopkeeper == null || !Visible(shopkeeper) || !args.TryGetProperty("target", out var targetRef)
                        || targetRef.GetString() != _bridge.Call("reference", shopkeeper).AsString())
                        throw new InvalidOperationException("Choose the current visible shopkeeper target before opening the inventory");
                }
                var target = potion.TargetType == TargetType.Self ? player.Creature : Target(potion.TargetType, args, player);
                pending.Potion = potion; _pending = submitted = pending;
                var queue = RunManager.Instance.ActionQueueSet;
                var slot = (uint)player.PotionSlots.ToList().FindIndex(p => ReferenceEquals(p, potion));
                Action<GameAction>? started = null;
                started = action => {
                    if (action is not UsePotionAction use || use.Player != player || use.PotionIndex != slot) return;
                    pending.Action = action; queue.ActionEnqueued -= started;
                };
                queue.ActionEnqueued += started;
                try { potion.EnqueueManualUse(target); }
                catch { queue.ActionEnqueued -= started; throw; }
            }
            else throw new InvalidOperationException("Unknown adapter request");
            return Serialize(new { Action = pending.Id, Status = "accepted" });
        }
        catch (Exception error)
        {
            if (submitted != null) { submitted.Status = "failed"; submitted.Error = error.GetBaseException().Message; }
            return Serialize(new { error = error.GetBaseException().Message });
        }
    }

    private Creature? Target(TargetType type, JsonElement args, Player player)
    {
        if (type == TargetType.AnyEnemy)
        {
            var id = args.GetProperty("target").GetString();
            return Combat?.Enemies.FirstOrDefault(e => e.IsAlive && CreatureRef(e) == id)
                ?? throw new InvalidOperationException("Target reference is stale or not a living enemy");
        }
        if (type is TargetType.AnyPlayer or TargetType.AnyAlly)
        {
            if (!args.TryGetProperty("target", out var selection) || selection.ValueKind == JsonValueKind.Null) return player.Creature;
            var candidates = type == TargetType.AnyPlayer ? Combat?.Players.Select(p => p.Creature) : Allies;
            return candidates?.FirstOrDefault(c => c.IsAlive && CreatureRef(c) == selection.GetString())
                ?? throw new InvalidOperationException("Target reference is stale or not a valid ally");
        }
        if (type == TargetType.Osty) return player.IsOstyAlive ? player.Osty : throw new InvalidOperationException("Osty is unavailable");
        if (type is TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies or TargetType.TargetedNoCreature) return null;
        throw new InvalidOperationException("Unsupported target type: " + type);
    }

    private static bool IsCardSelection(object? scope) => scope is NCardGridSelectionScreen or NChooseACardSelectionScreen;

    private void UpdatePending()
    {
        var p = _pending;
        if (p == null || p.Status is "completed" or "failed" or "cancelled") return;
        if (p.Action != null)
        {
            var state = p.Action.State.ToString();
            if (state == "Canceled") { p.Status = "cancelled"; p.Error = "The game cancelled this action"; p.Action = null; p.Potion = null; return; }
            p.Status = state == "GatheringPlayerChoice" ? "choice_required" : "running";
            if (p.Action.CompletionTask.IsCompleted)
            {
                p.Error = p.Action.Exception?.Message;
                p.Status = p.Error != null || p.Action.CompletionTask.IsFaulted ? "failed" : p.Action.CompletionTask.IsCanceled ? "cancelled" : "completed";
            }
        }
        else if (p.Task != null)
        {
            if (!p.Task.IsCompleted)
            {
                p.Status = IsCardSelection(NOverlayStack.Instance?.Peek()) ? "choice_required" : "running";
                return;
            }
            p.Error = p.Task.Exception?.GetBaseException().Message;
            p.Status = p.Task.IsFaulted ? "failed" : p.Task.IsCanceled || p.Purchase is { IsCompletedSuccessfully: true, Result: false } ? "cancelled" : "completed";
        }
        else if (p.Kind == "abandonRun")
        {
            if (RunManager.Instance.IsAbandoned && Scope().GetType().Name == "NGameOverScreen") p.Status = "completed";
        }
        else if (p.Kind == "endTurn")
        {
            if (!PlayPhase || Combat?.RoundNumber != p.Round) p.LeftPlayPhase = true;
            if (Combat == null || (p.LeftPlayPhase && CombatReady && Combat.RoundNumber > p.Round)) p.Status = "completed";
            else p.Status = IsCardSelection(NOverlayStack.Instance?.Peek()) ? "choice_required" : "running";
        }
        else if (p.Potion != null && Player != null && !Player.PotionSlots.Any(x => ReferenceEquals(x, p.Potion)) && Idle)
            p.Status = "completed";
        if (p.Status is "completed" or "failed" or "cancelled") { p.Action = null; p.Potion = null; p.Task = null; p.Purchase = null; }
    }

    public string Frame()
    {
        if (Time.GetTicksMsec() < _nextFrameCheck) return "";
        _nextFrameCheck = Time.GetTicksMsec() + 100;
        try
        {
            RefreshCombat(); UpdatePending();
            var scope = Scope();
            NTimelineScreen? timeline = null;
            for (Node? parent = scope; parent != null; parent = parent.GetParent())
                if (parent is NTimelineScreen found) { timeline = found; break; }
            var timelineBlocked = Get(timeline, "_inputBlocker") is Control { Visible: true };
            var skippedRewards = (Get(scope, "_skippedRewardButtons") as ICollection)?.Count;
            var enabled = Walk(scope).Count(n => Visible(n) && n is NClickableControl { IsEnabled: true });
            var selectionOwner = scope is NCardGridSelectionScreen ? scope : NCombatRoom.Instance?.Ui.Hand as object;
            var selectionKey = string.Join(",", SelectedCards(selectionOwner).Select(CardRef).Order(StringComparer.Ordinal));
            var key = $"{selectionKey}:{scope.GetInstanceId()}:{Combat?.RoundNumber}:{Player?.PlayerCombatState?.Phase}:{CombatManager.Instance.PlayerActionsDisabled}:{Idle}:{_pending?.Id}:{_pending?.Status}:{NCombatRoom.Instance?.Ui.Hand.CurrentMode}:{NCombatRoom.Instance?.Ui.Hand.InCardPlay}:{Player?.Gold}:{Player?.Creature.CurrentHp}:{enabled}:{NMapScreen.Instance?.IsTraveling}:{NMapScreen.Instance?.IsTravelEnabled}:{NMerchantRoom.Instance?.Inventory?.IsOpen}:{NGame.Instance?.Transition?.InTransition}:{timelineBlocked}:{timeline?.IsScreenQueued()}:{skippedRewards}:{Get(scope, "IsComplete")}:{Get(scope, "_isAnimatingSummary")}:" + string.Join("|", Labels(scope).Take(100));
            if (key == _fingerprint) return "";
            _fingerprint = key; return (++_revision).ToString();
        }
        catch { return ""; }
    }
}
