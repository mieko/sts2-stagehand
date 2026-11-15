using System.Reflection;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
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

using static Stagehand.Sts2.NativeData;
using static Stagehand.Sts2.ModelProjection;

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
    private readonly WorkflowService _workflows;
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

    private Sts2Host(Node bridge, SceneTree tree)
    {
        _bridge = bridge;
        _tree = tree;
        _workflows = new WorkflowService(
            () => JsonNode.Parse(Read())!.AsObject(),
            args => JsonNode.Parse(RequestNative(args.ToJsonString()))!.AsObject(),
            ActivateChoice,
            BrowseShop,
            () => (long)Time.GetTicksMsec());
    }

    public static void Install(Node bridge, SceneTree tree)
    {
        var host = new Sts2Host(bridge, tree);
        var node = GD.Load<GDScript>("res://stagehand_sts2/sts2.gd").New().As<Node>();
        node.Name = "Sts2";
        node.Set("read_callback", Callable.From(host.Read));
        node.Set("request_callback", Callable.From<string, string>(host.Request));
        node.Set("inspect_callback", Callable.From<string, string>(host.Inspect));
        node.Set("select_card_callback", Callable.From<string, string>(host.SelectCard));
        node.Set("workflow_callback", Callable.From<string, string>(host.Workflow));
        node.Set("frame_callback", Callable.From(host.Frame));
        bridge.AddChild(node);
        var handle = node.Call("configure", bridge);
        if (handle.VariantType != Variant.Type.String || string.IsNullOrEmpty(handle.AsString()))
            throw new InvalidOperationException("The Sts2 endpoint could not be registered");
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
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
        if (Player == null)
            _deckRefs.Clear();
        var potions = new HashSet<PotionModel>(Player?.Potions ?? Enumerable.Empty<PotionModel>(), ReferenceEqualityComparer.Instance);
        foreach (var potion in _potionRefs.Keys.ToArray())
            if (!potions.Contains(potion))
                _potionRefs.Remove(potion);
        var combat = Combat;
        if (ReferenceEquals(combat, _combat))
            return;

        _combat = combat;
        _combatId = Guid.NewGuid().ToString("N");
        _cardRefs.Clear();
        _cards.Clear();
        _nextCard = 0;
    }

    private string CardRef(CardModel card)
    {
        if (!_cardRefs.TryGetValue(card, out var id))
        {
            id = $"{_combatId}:card:{++_nextCard}";
            _cardRefs[card] = id;
            _cards[id] = card;
        }
        return id;
    }

    private string DeckRef(CardModel card)
    {
        if (!_deckRefs.TryGetValue(card, out var id))
            _deckRefs[card] = id = $"deck:{++_nextDeckRef}";
        return id;
    }

    private string CreatureRef(Creature enemy) => $"{_combatId}:creature:{enemy.CombatId}";

    private IEnumerable<Creature> Allies => Combat?.Players.SelectMany(p => new[] { p.Creature }
        .Concat(p.PlayerCombatState?.Pets ?? Array.Empty<Creature>())).Distinct().Where(c => c.IsAlive)
        ?? Enumerable.Empty<Creature>();

    private string PotionRef(PotionModel potion)
    {
        if (!_potionRefs.TryGetValue(potion, out var id))
            _potionRefs[potion] = id = "potion:" + Guid.NewGuid().ToString("N");
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
        if (_offerRefs.TryGetValue(entry, out var old) && ReferenceEquals(old.Model, model))
            return old.Ref;

        if (old.Ref != null)
            _offers.Remove(old.Ref);
        var id = "offer:" + Guid.NewGuid().ToString("N");
        _offerRefs[entry] = (model, id);
        _offers[id] = entry;
        return id;
    }

    private object Card(CardModel card, bool hand = false, bool upgrade = false, bool reference = true, bool deckReference = false)
    {
        var facts = CardDetails(card, hand ? PileType.Hand : PileType.None);
        bool? canPlay = null;
        string? reason = null;
        if (hand)
        {
            canPlay = card.CanPlay(out var why, out _);
            if (canPlay == false)
                reason = why.ToString();
        }
        return new
        {
            Ref = reference ? CardRef(card) : null,
            DeckRef = deckReference ? DeckRef(card) : null,
            facts.Id,
            facts.Name,
            facts.Description,
            facts.Cost,
            facts.StarCost,
            facts.CostScope,
            facts.Type,
            facts.Target,
            facts.Upgraded,
            facts.Mechanics,
            facts.Values,
            facts.ValueContext,
            Replay = card.GetEnchantedReplayCount(),
            CanUpgrade = card.IsUpgradable,
            CanRemove = card.IsRemovable,
            Upgrade = upgrade && card.IsUpgradable ? UpgradePreview(card) : null,
            LegalTargets = hand && Combat != null && card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly or TargetType.AnyPlayer or TargetType.Osty
                ? Combat.Enemies.Concat(Allies).Where(e => e.IsAlive && card.CanPlayTargeting(e)).Select(CreatureRef).ToArray() : null,
            CanPlay = canPlay,
            Reason = reason,
            Effects = Effects(card.HoverTips),
            Enchantment = CardModifier(card.Enchantment),
            Affliction = CardModifier(card.Affliction)
        };
    }

    private object UpgradePreview(CardModel card)
    {
        return Card(CreateUpgradePreview(card), reference: false);
    }

    private object Pet(Creature creature)
    {
        var player = Combat?.Players.FirstOrDefault(p => ReferenceEquals(p.Creature, creature));
        return new
        {
            Ref = CreatureRef(creature),
            Name = player != null ? Text(player.Character.Title) : Text(creature.Monster?.Title),
            Hp = creature.CurrentHp,
            MaxHp = creature.MaxHp,
            Block = creature.Block,
            Alive = creature.IsAlive,
            Statuses = Powers(creature)
        };
    }

    private object Enemy(Creature enemy)
    {
        var intents = new List<object>();
        if (enemy.Monster?.NextMove is { } move && _combat != null)
            foreach (var intent in move.Intents)
            {
                var tip = intent.GetHoverTip(_combat.PlayerCreatures, enemy);
                intents.Add(new
                {
                    Type = intent.IntentType.ToString(),
                    Label = Text(intent.GetIntentLabel(_combat.PlayerCreatures, enemy)),
                    Title = Text(tip.Title),
                    Description = Text(tip.Description),
                    IsAttack = intent is AttackIntent,
                    DamagePerHit = intent is AttackIntent attack ? (int?)attack.GetSingleDamage(_combat.PlayerCreatures, enemy) : null,
                    Hits = intent is MultiAttackIntent multi ? (int?)multi.Repeats : intent is SingleAttackIntent ? 1 : null,
                    TotalDamage = intent is AttackIntent damage ? (int?)damage.GetTotalDamage(_combat.PlayerCreatures, enemy) : null,
                    DamageScope = intent is AttackIntent ? "native_local_player_preview" : null
                });
            }
        return new
        {
            Ref = CreatureRef(enemy),
            Name = Text(enemy.Monster?.Title),
            Hp = enemy.CurrentHp,
            MaxHp = enemy.MaxHp,
            Block = enemy.Block,
            Statuses = Powers(enemy),
            Intents = intents,
            RelativePosition = SurroundedSide(Player == null ? null : Facing(Player.Creature),
                enemy.Powers.Any(p => p.IsVisible && p is MegaCrit.Sts2.Core.Models.Powers.BackAttackLeftPower),
                enemy.Powers.Any(p => p.IsVisible && p is MegaCrit.Sts2.Core.Models.Powers.BackAttackRightPower))
        };
    }

    private static MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction? Facing(Creature creature) =>
        creature.Powers.OfType<MegaCrit.Sts2.Core.Models.Powers.SurroundedPower>().FirstOrDefault(p => p.IsVisible)?.Facing;

    private static object? SurroundedSide(MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction? facing, bool left, bool right)
    {
        if (facing == null)
            return null;

        var side = left == right ? null : left ? "Left" : "Right";
        return new
        {
            Side = side,
            Behind = side == null ? (bool?)null
            : facing == MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right ? left : right,
            Source = "native_surrounded_directional_powers"
        };
    }

    private static IEnumerable<Node> Walk(Node root)
    {
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        var count = 0;
        while (queue.Count > 0 && count++ < 8000)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var child in node.GetChildren())
                queue.Enqueue(child);
        }
    }

    private static bool Visible(Node node)
    {
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion() || node is not Control control || !control.IsVisibleInTree())
            return false;

        for (Node? p = node; p != null; p = p.GetParent())
        {
            if (p is NMerchantInventory { IsOpen: false })
                return false;
        }
        return true;
    }

    private static bool HasInk(Node node)
    {
        for (Node? p = node; p != null; p = p.GetParent())
            if (p is CanvasItem canvas && (canvas.Modulate.A == 0 || canvas.SelfModulate.A == 0))
                return false;

        return true;
    }

    private static bool IsTimelineOverlay(Node node)
    {
        for (Node? p = node; p != null && p is not NTimelineScreen; p = p.GetParent())
            if (p is NEpochInspectScreen || p.GetType().Name.StartsWith("NUnlock") || p.GetType().Name == "NTimelineTutorial")
                return true;

        return false;
    }

    private static bool ChatAvailable(Control? hitbox) => hitbox is NClickableControl { IsEnabled: true } && Visible(hitbox);

    private static IEnumerable<string> Labels(Node root, bool ancientChat = false) => Walk(root)
        .Where(n => n is RichTextLabel or Label && Visible(n) && HasInk(n)
            && (!ancientChat || !HasParentType(n, nameof(NEventOptionButton))))
        .Select(n => n switch { RichTextLabel rich => rich.GetParsedText(), Label label => label.Text, _ => "" })
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(Text).Distinct();

    private Node Scope()
    {
        var root = _tree.CurrentScene ?? _tree.Root;
        var popup = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name.Contains("Ftue"));
        if (popup != null)
            return popup;

        if (NMapScreen.Instance is { IsOpen: true } map)
            return map;

        if (NOverlayStack.Instance?.Peek() is Node overlay && Visible(overlay))
        {
            var preview = Walk(overlay).OfType<Control>().FirstOrDefault(c => c.Name.ToString().Contains("PreviewContainer")
                && Visible(c) && Walk(c).Any(n => n is MegaCrit.Sts2.Core.Nodes.CommonUi.NConfirmButton { IsEnabled: true } && Visible(n)));
            if (preview != null)
                return preview;

            return overlay;
        }
        if (Walk(root).OfType<NTimelineScreen>().FirstOrDefault(Visible) is { } timeline)
        {
            if (timeline.CurrentUnlockScreen is { } unlock && Visible(unlock))
                return unlock;

            var inspect = Get(timeline, "_inspectScreen") as Control;
            if (inspect != null && Visible(inspect))
                return inspect;

            var tutorial = Walk(timeline).FirstOrDefault(n => n.GetType().Name == "NTimelineTutorial" && Visible(n));
            return tutorial ?? timeline;
        }
        foreach (var type in new[] { "NCardGridSelectionScreen", "NCharacterSelectScreen", "NSingleplayerSubmenu", "NSettingsScreen" })
        {
            var screen = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name == type);
            if (screen != null)
                return screen;
        }
        var roomType = Player?.RunState.CurrentRoom?.GetType().Name;
        var inventory = Walk(root).OfType<NMerchantInventory>().FirstOrDefault(i => i.IsOpen && Visible(i));
        if (inventory != null)
            return inventory;

        if (Combat != null && NCombatRoom.Instance is { } activeCombat && Visible(activeCombat))
            return activeCombat;

        if (roomType != null)
        {
            var room = Walk(root).FirstOrDefault(n => Visible(n) && n.GetType().Name == "N" + roomType);
            if (room != null)
                return room;
        }
        return root;
    }

    private static Control Hitbox(Control control)
    {
        var hitbox = control switch
        {
            NCardHolder holder => holder.Hitbox,
            NCardBundle bundle => bundle.Hitbox,
            NMerchantSlot slot => slot.Hitbox,
            _ => control
        };
        return GodotObject.IsInstanceValid(hitbox) ? hitbox : control;
    }

    private object Choice(Control control, bool skipped = false)
    {
        var type = control.GetType().Name;
        var hitbox = Hitbox(control);
        var rect = hitbox.GetGlobalRect();
        var label = string.Join(" · ", Labels(control).Take(6));
        if (control is NAncientDialogueHitbox)
        {
            label = "Advance dialogue";
            for (var owner = control.GetParent(); owner != null; owner = owner.GetParent())
                if (owner is NAncientEventLayout layout && Get(layout, "_fakeNextButton") is Node next)
                {
                    var caption = string.Join(" · ", Labels(next));
                    if (!string.IsNullOrWhiteSpace(caption))
                        label = caption;
                    break;
                }
        }
        var enabled = hitbox is not NClickableControl clickable || clickable.IsEnabled;
        string? blockedReason = null;
        string? blockedBy = null;
        if (control is BaseButton button && button.Disabled)
            enabled = false;
        if (!enabled && NGame.Instance?.MainMenu is { } menu && SaveManager.Instance.GetDiscoveredEpochCount() > 0
            && !SaveManager.Instance.HasRunSave && new[] { "_singleplayerButton", "_multiplayerButton", "_compendiumButton" }
                .Any(field => ReferenceEquals(Get(menu, field), control)))
            blockedReason = "Reveal the obtained Epochs in the Timeline before starting another run";
        object? detail = null;
        object? activation = null;
        if (control is NCardHolder holder && holder.CardModel is { } card)
        {
            label = card.Title;
            detail = Card(card, upgrade: NOverlayStack.Instance?.Peek() is NDeckUpgradeSelectScreen
                || NCombatRoom.Instance?.Ui.Hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect);
            if (holder is NPreviewCardHolder)
                enabled = false;
            else
            {
                Node target = holder;
                var signal = NCardHolder.SignalName.Pressed.ToString();
                if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen screen)
                {
                    var grid = Walk(screen).OfType<NCardGrid>().FirstOrDefault();
                    if (grid != null)
                    {
                        target = grid;
                        signal = NCardGrid.SignalName.HolderPressed.ToString();
                    }
                }
                activation = new
                {
                    Target = _bridge.Call("reference", target).AsString(),
                    Signal = signal,
                    Argument = _bridge.Call("reference", holder).AsString()
                };
            }
        }
        else if (control is NCardBundle bundle)
        {
            label = string.Join(" + ", bundle.Bundle.Select(c => c.Title));
            detail = new
            {
                Cards = bundle.Bundle.Select(c => Card(c)).ToArray()
            };
        }
        else if (control is NCharacterSelectButton character && character.Character is { } model)
        {
            label = Text(model.Title);
            enabled &= !character.IsLocked;
            detail = new
            {
                Id = model.Id.Entry,
                Locked = character.IsLocked,
                Random = character.IsRandom,
                Hp = character.IsRandom ? (int?)null : model.StartingHp,
                Gold = character.IsRandom ? (int?)null : model.StartingGold,
                Energy = character.IsRandom ? (int?)null : model.MaxEnergy,
                Description = Text(new LocString("characters", model.CharacterSelectDesc)),
                StartingRelics = character.IsRandom ? null : model.StartingRelics.Select(ModelInfo).ToArray()
            };
        }
        else if (control is NMapPoint point && point.Point is { } p)
        {
            label = $"{p.PointType} ({p.coord.col},{p.coord.row})";
            enabled = point.State.ToString() == "Travelable";
        }
        else if (control is NEpochSlot epoch)
        {
            var state = epoch.State.ToString();
            var info = epoch.model.UnlockInfo;
            info.Add("IsRevealed", state == "Complete");
            label = Text(epoch.model.Title);
            detail = new
            {
                Id = epoch.model.Id,
                State = state,
                Description = Text(info),
                Era = Get(epoch, "_era")?.ToString(),
                Position = epoch.eraPosition
            };
            if (state == "NotObtained")
            {
                enabled = false;
                blockedReason = "Epoch has not been obtained";
            }
        }
        var option = Get(control, "Option");
        if (option != null)
        {
            label = Text(Get(option, "Title"));
            detail = new
            {
                Description = Text(Get(option, "Description")),
                Effects = Effects(Get(option, "HoverTips")),
                IsProceed = Get(option, "IsProceed"),
                WasChosen = Get(option, "WasChosen")
            };
            if (Get(option, "IsLocked") is true || Get(option, "IsEnabled") is false)
                enabled = false;
        }
        var entry = Get(control, "Entry");
        if (entry != null)
        {
            var model = Get(entry, "Model") ?? Get(Get(entry, "CreationResult"), "Card");
            detail = new
            {
                Price = Get(entry, "Cost"),
                Stocked = Get(entry, "IsStocked"),
                Item = model is CardModel m ? Card(m) : ModelInfo(model)
            };
            label = Text(Get(model, "Title"));
            if (Get(entry, "EnoughGold") is false || Get(entry, "IsStocked") is false)
                enabled = false;
            if (control is NMerchantPotion && Player?.HasOpenPotionSlots == false)
            {
                enabled = false;
                blockedBy = "potion_inventory_full";
                blockedReason = "Potion inventory is full; use or discard a potion first";
            }
            if (control is NMerchantCardRemoval)
            {
                label = Text(Get(control, "Title"));
                var description = Get(control, "Description") as MegaCrit.Sts2.Core.Localization.LocString;
                description?.Add("Amount", MerchantCardRemovalEntry.PriceIncrease);
                detail = new
                {
                    Price = Get(entry, "Cost"),
                    Stocked = Get(entry, "IsStocked"),
                    Description = Text(description),
                    Category = "card_removal"
                };
            }
        }
        var relic = Get(Get(control, "Relic"), "Model");
        if (relic != null)
        {
            label = Text(Get(relic, "Title"));
            detail = ModelInfo(relic);
        }
        var reward = Get(control, "Reward");
        if (reward != null)
        {
            detail = new
            {
                Category = reward.GetType().Name,
                Amount = Get(reward, "Amount"),
                Description = Text(Get(reward, "Description")),
                Potion = Get(reward, "Potion") is { } rp ? ModelInfo(rp) : null,
                Relic = Get(reward, "IsPopulated") is true && Get(reward, "Relic") is { } rr ? ModelInfo(rr) : null
            };
            if (reward is MegaCrit.Sts2.Core.Rewards.PotionReward && Player?.HasOpenPotionSlots == false)
            {
                enabled = false;
                blockedBy = "potion_inventory_full";
                blockedReason = "Potion inventory is full; use or discard a potion first";
            }
        }
        if (control is NCrystalSphereCell cell)
        {
            var entity = cell.Entity;
            label = $"Cell ({entity.X},{entity.Y})";
            enabled &= entity.IsHidden;
            detail = new
            {
                X = entity.X,
                Y = entity.Y,
                Hidden = entity.IsHidden,
                Highlighted = entity.IsHighlighted,
                RevealedItem = !entity.IsHidden ? entity.Item?.GetType().Name : null
            };
        }
        if (type == "NDivinationButton")
            detail = new
            {
                Tool = control.Name.ToString().Contains("Big") ? "big" : "small",
                Active = control.GetNodeOrNull<Control>("%Outline")?.Visible == true
            };
        return new
        {
            Ref = _bridge.Call("reference", hitbox).AsString(),
            Label = string.IsNullOrEmpty(label) ? control.Name.ToString() : label,
            Type = type,
            Enabled = enabled,
            BlockedReason = blockedReason,
            BlockedBy = blockedBy,
            Skipped = skipped,
            Selected = Get(control, "IsSelected"),
            Detail = detail,
            CardRef = control is NCardHolder cardHolder && cardHolder.CardModel != null ? CardRef(cardHolder.CardModel) : null,
            IsSkip = Get(control, "IsSkip"),
            Activation = activation,
            OfferRef = entry is MerchantEntry merchantEntry ? OfferRef(merchantEntry) : null,
            Rect = new[] { rect.Position.X, rect.Position.Y, rect.Size.X, rect.Size.Y }
        };
    }

    private static object? Result(Node scope)
    {
        if (scope.GetType().Name != "NGameOverScreen")
            return null;

        var history = (RunHistory)Get(scope, "_history")!;
        var run = (RunState)Get(scope, "_runState")!;
        var serial = (SerializableRun)Get(scope, "_serializableRun")!;
        var player = (Player)Get(scope, "_localPlayer")!;
        var earned = player.DiscoveredEpochs.Count;
        var continueEnabled = Get(scope, "_continueButton") is NClickableControl { IsEnabled: true } next && Visible(next);
        var menuEnabled = Get(scope, "_mainMenuButton") is NClickableControl { IsEnabled: true } menu && Visible(menu);
        var leaderboardEnabled = Get(scope, "_leaderboardButton") is NClickableControl { IsEnabled: true } board && Visible(board);
        return new
        {
            Win = history.Win,
            Score = Get(scope, "_score"),
            Stats = new
            {
                FloorsClimbed = run.TotalFloor,
                GoldGained = serial.MapPointHistory.SelectMany(a => a).Sum(e => e.GetEntry(player.NetId).GoldGained),
                ElitesKilled = ScoreUtility.GetElitesKilledCount(serial.MapPointHistory),
                BossesSlain = ScoreUtility.GetBossesSlainCount(serial.MapPointHistory, history.Win),
                Ascension = history.Ascension
            },
            EarnedEpochCount = earned,
            PendingEpochReveals = SaveManager.Instance.GetDiscoveredEpochCount(),
            ScoreUnlocksRemaining = SaveManager.Instance.GetUnlocksRemaining(),
            Phase = ResultPhase(Get(scope, "_isAnimatingSummary") is true, continueEnabled, menuEnabled, leaderboardEnabled, earned)
        };
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
        return PublicCardList.Read(EligibleCards(screen), c => Serialize(Card(c, reference: false)), c => (object)new
        {
            Ref = CardRef(c),
            CardRef = CardRef(c),
            Label = c.Title,
            Type = "NGridCardHolder",
            Enabled = true,
            Selected = selected.Contains(c),
            Detail = Card(c, upgrade: screen is NDeckUpgradeSelectScreen),
            Activation = new
            {
                Kind = "select_card",
                Card = CardRef(c)
            }
        });
    }

    public string SelectCard(string json)
    {
        if (_workflows.Busy)
            return Serialize(new
            {
                error = "A service operation is still running"
            });

        return SelectCardNative(json);
    }

    private string SelectCardNative(string json)
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
            return Serialize(new
            {
                Status = "submitted"
            });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private object? VisibleBoss(NMapPoint point)
    {
        if (point is not NBossMapPoint || Player?.RunState is not { } run)
            return null;

        var encounter = ReferenceEquals(point.Point, run.Map.SecondBossMapPoint) ? run.Act.SecondBossEncounter : run.Act.BossEncounter;
        return encounter == null ? null : new
        {
            Id = encounter.Id.Entry,
            Name = Text(Get(encounter, "Title"))
        };
    }

    private (List<Control> Controls, object[] Choices) ReadChoices(Node scope, bool isUnlock, HashSet<Control> skippedRewards, bool ancientChat)
    {
        var controls = Walk(scope).OfType<Control>().Where(c =>
        {
            if (!GodotObject.IsInstanceValid(c))
                return false;

            var offered = Visible(c) && (scope is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen && Get(c, "Reward") != null
                || scope is NMerchantInventory && c is NMerchantSlot { Entry: not null }
                || scope is not NCombatRoom && c is NCardHolder { CardModel: not null });
            return offered || Visible(c) && Hitbox(c).GetGlobalRect().HasArea();
        }).ToList();
        if (ancientChat)
            controls.RemoveAll(c => c is NEventOptionButton || HasParentType(c, nameof(NEventOptionButton)));
        if (scope is NTimelineScreen)
            controls.RemoveAll(IsTimelineOverlay);
        if (scope.GetType().Name is "NMerchantRoom")
            controls.RemoveAll(c => HasParentType(c, "NMerchantInventory"));
        var choices = controls.Where(c => c is NClickableControl or BaseButton or NCardHolder or NMapPoint or NMerchantSlot or NCardBundle)
            .Where(c => c is not NAncientDialogueLine)
            .Where(c => !isUnlock || c.GetType().Name == "NUnlockConfirmButton")
            .Where(c => c is NCardHolder or NMerchantSlot or NCardBundle || !HasChoiceParent(c))
            .Where(c => !(CombatReady && HasParentType(c, "NPlayerHand")))
            .Where(c => scope is not NCardGridSelectionScreen || c is not NCardHolder)
            .Take(200).Select(c => Choice(c, skippedRewards.Contains(c))).ToArray();
        if (scope is NCardGridSelectionScreen gridScreen)
            choices = choices.Concat(GridChoices(gridScreen)).ToArray();
        return (controls, choices);
    }

    private bool InteractionReady(Node scope, object[] choices, bool ordinaryCombat)
    {
        var player = Player;
        var room = player?.RunState.CurrentRoom;
        var waitingForRewards = player != null && ReferenceEquals(scope, _tree.CurrentScene);
        waitingForRewards |= Combat == null && scope is NCombatRoom;
        var boundRoom = scope is NMerchantRoom merchant ? merchant.Room : Get(scope, "_room");
        if (boundRoom != null && !ReferenceEquals(boundRoom, room))
            waitingForRewards = true;
        if (scope is NEventRoom)
        {
            var expected = Get(room, "LocalMutableEvent") ?? Get(room, "CanonicalEvent");
            waitingForRewards |= expected != null && !ReferenceEquals(Get(scope, "_event"), expected);
        }

        // A required card choice can suspend the action executor before normal draw.
        var enabledChoice = choices.Any(c => Get(c, "Enabled") is true);
        var ready = ordinaryCombat ? CombatReady : enabledChoice
            && (Idle || ActiveCardSelection(scope) || RunManager.Instance.ActionExecutor?.IsPaused == true
                || _pending?.Status == "choice_required")
            && !waitingForRewards && (scope is not NMapScreen map || !map.IsTraveling && map.IsTravelEnabled);
        return ready && NGame.Instance?.Transition?.InTransition != true;
    }

    public string Read()
    {
        try
        {
            RefreshCombat();
            UpdatePending();
            var scope = Scope();
            var player = Player;
            var combat = Combat;
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
            {
                _inventory = inventory;
                _offerRefs.Clear();
                _offers.Clear();
            }
            var merchantScene = NMerchantRoom.Instance;
            if (!GodotObject.IsInstanceValid(merchantScene) || !ReferenceEquals(merchantScene?.Room, player?.RunState.CurrentRoom))
                merchantScene = null;
            var fakeMerchant = Walk(_tree.CurrentScene ?? _tree.Root).FirstOrDefault(n => n.GetType().Name == "NFakeMerchant" && Visible(n));
            var shopOwner = merchantScene as Node ?? fakeMerchant;
            var shopInventory = merchantScene?.Inventory ?? Get(fakeMerchant, "Inventory") as NMerchantInventory;
            var fakeButton = Get(fakeMerchant, "MerchantButton") as NClickableControl;
            var dialogueLayout = Walk(scope).OfType<NAncientEventLayout>().FirstOrDefault();
            var dialogueHitbox = Get(dialogueLayout, "_dialogueHitbox") as Control;
            var ancientChat = ChatAvailable(dialogueHitbox);
            var (controls, choices) = ReadChoices(scope, isUnlock, skippedRewards, ancientChat);
            var map = scope is NMapScreen ? Walk(scope).OfType<NMapPoint>().Where(p => p.Point != null)
                .Select(p => new
                {
                    Ref = _bridge.Call("reference", p).AsString(),
                    Col = p.Point!.coord.col,
                    Row = p.Point.coord.row,
                    Type = p.Point.PointType.ToString(),
                    Boss = VisibleBoss(p),
                    State = p.State.ToString(),
                    LeadsTo = p.Point.Children.Select(c => new { Col = c.coord.col, Row = c.coord.row }).ToArray()
                }).ToArray() : null;
            var pending = _pending == null ? null : new
            {
                Action = _pending.Id,
                Kind = _pending.Kind,
                Purpose = _pending.Purpose,
                Status = _pending.Status,
                Error = _pending.Error
            };
            var handSelection = combat != null && ActiveHandSelection(scope);
            var selectionHost = handSelection ? NCombatRoom.Instance?.Ui.Hand as object : NOverlayStack.Instance?.Peek() as object;
            var prefs = Get(selectionHost, "_prefs");
            var selected = Get(selectionHost, "_selectedCards") as IEnumerable;
            var combatView = combat != null && scope is NCombatRoom && !handSelection;
            var room = player?.RunState.CurrentRoom;
            var eventModel = (Get(room, "LocalMutableEvent") ?? Get(room, "CanonicalEvent")) as EventModel;
            var dialogueLine = Get(dialogueLayout, "_currentDialogueLine") as int?;
            var currentSpeech = Get(dialogueLayout, "_dialogueContainer") is Node lines && dialogueLine is int line
                && line >= 0 && line < lines.GetChildCount() ? lines.GetChild(line) : null;
            object? dialogue = dialogueLayout == null ? null : new
            {
                Source = "ancient_dialogue",
                Line = Get(dialogueLayout, "_currentDialogueLine"),
                LastLine = Get(dialogueLayout, "IsDialogueOnLastLine"),
                CanAdvance = ancientChat,
                Ref = ancientChat ? _bridge.Call("reference", dialogueHitbox!).AsString() : null,
                Text = currentSpeech == null ? [] : Labels(currentSpeech).ToArray()
            };
            // Architect speech uses combat-layout bubbles rather than an ancient
            // event layout. Read only the current visible bubble, never future lines.
            if (eventModel is MegaCrit.Sts2.Core.Models.Events.TheArchitect && Get(eventModel, "_speechBubble") is Node bubble
                && GodotObject.IsInstanceValid(bubble) && !bubble.IsQueuedForDeletion())
            {
                var spoken = Labels(bubble).ToArray();
                if (spoken.Length > 0)
                    dialogue = new
                    {
                        Source = "speech_bubble",
                        Line = Get(eventModel, "_currentLineIndex"),
                        LastLine = Get(eventModel, "IsOnLastLine"),
                        CanAdvance = false,
                        Text = spoken
                    };
            }
            var ready = InteractionReady(scope, choices, combatView);
            if (scope.GetType().Name == "NGameOverScreen")
                ready &= Walk(scope).OfType<NClickableControl>().Any(c => c.IsEnabled && Visible(c)
                    && c.GetType().Name is "NGameOverContinueButton" or "NReturnToMainMenuButton");
            if (scope is NEpochInspectScreen)
                ready &= Get(scope, "_closeButton") is NClickableControl { IsEnabled: true } close && Visible(close);
            if (scope is NTimelineScreen)
                ready &= !timelineBusy;
            if (scope is NCharacterSelectScreen)
                ready &= SaveManager.Instance.Progress.PendingCharacterUnlock == ModelId.none
                    && Get(scope, "_lobby") != null
                    && choices.Any(c => Get(c, "Type") as string == "NCharacterSelectButton" && Get(c, "Enabled") is true);
            if (scope is NCrystalSphereScreen && Get(Get(scope, "_entity"), "IsFinished") is true)
                ready &= choices.Any(c => Get(c, "Type") as string == "NProceedButton" && Get(c, "Enabled") is true);
            if (isUnlock)
                ready &= choices.Any(c => Get(c, "Enabled") is true && Get(c, "Type") as string == "NUnlockConfirmButton");
            if (scope is NMerchantRoom && merchantScene?.Inventory == null)
                ready = false;
            var preview = scope.Name.ToString().Contains("PreviewContainer");
            var snapshot = JsonSerializer.SerializeToNode(new
            {
                Screen = preview ? "CardPreview" : handSelection ? "HandSelection" : scope.GetType().Name,
                Preview = preview ? Walk(scope).OfType<NCardHolder>().Select(h => h.CardModel).OfType<CardModel>().Select(c => Card(c)).ToArray() : null,
                Result = Result(scope),
                Navigation = !GodotObject.IsInstanceValid(menu) || !Visible(menu!) ? null : new
                {
                    AtMainMenu = canNavigate && menu!.SubmenuStack.Peek() == null,
                    CanReturnToMenu = canNavigate,
                    PendingEpochReveals = SaveManager.Instance.GetDiscoveredEpochCount(),
                    MenuStack = _bridge.Call("reference", menu.SubmenuStack).AsString()
                },
                Timeline = timeline == null ? null : new
                {
                    Busy = timelineBusy,
                    BackEnabled = Get(timeline, "_backButton") is NClickableControl { IsEnabled: true },
                    CanReturnToMenu = canNavigate
                },
                Epoch = scope is not NEpochInspectScreen ? null : new
                {
                    Title = Text(Get(Get(scope, "_epoch"), "Title")),
                    Description = Text(Get(Get(scope, "_epoch"), "Description")),
                    Unlocks = Text(Get(Get(scope, "_epoch"), "UnlockText"))
                },
                Unlock = !isUnlock ? null : new
                {
                    Text = Labels(scope).ToArray(),
                    Cards = controls.OfType<NCardHolder>().Where(c => c.CardModel != null).Select(c => Card(c.CardModel!)).ToArray(),
                    Relics = controls.Select(c => Get(Get(c, "Relic"), "Model")).OfType<RelicModel>().Select(ModelInfo).ToArray(),
                    Potions = (Get(scope, "_potions") as IEnumerable)?.Cast<object>().OfType<PotionModel>().Select(ModelInfo).ToArray(),
                    Character = Get(scope, "_character") is not CharacterModel unlocked ? null : new
                    {
                        Id = unlocked.Id.Entry,
                        Name = Text(unlocked.Title)
                    }
                },
                Room = player?.RunState.CurrentRoom?.GetType().Name,
                Selection = prefs == null ? null : new
                {
                    Prompt = Text(Get(prefs, "Prompt")),
                    Min = Get(prefs, "MinSelect"),
                    Max = Get(prefs, "MaxSelect"),
                    Confirm = Get(prefs, "RequireManualConfirmation"),
                    CanCancel = Get(prefs, "Cancelable"),
                    Selected = selected?.Cast<object>().Select(c => c as CardModel ?? Get(c, "Model") as CardModel)
                        .OfType<CardModel>().Select(c => Card(c)).ToArray()
                },
                Shop = shopOwner == null ? null : new
                {
                    Control = _bridge.Call("reference", shopOwner).AsString(),
                    Kind = merchantScene != null ? "ordinary" : "fake",
                    Open = shopInventory?.IsOpen == true,
                    CanBrowse = shopInventory != null && (merchantScene != null || fakeButton is { IsEnabled: true } && Visible(fakeButton))
                },
                Rewards = scope is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen ? new
                {
                    Complete = Get(scope, "IsComplete"),
                    Remaining = rewardButtons?.Length,
                    Unresolved = rewardButtons?.Count(c => !skippedRewards.Contains(c))
                } : null,
                CrystalSphere = scope is NCrystalSphereScreen ? new
                {
                    Remaining = Get(Get(scope, "_entity"), "DivinationCount"),
                    Tool = Get(Get(scope, "_entity"), "CrystalSphereTool")?.ToString(),
                    Finished = Get(Get(scope, "_entity"), "IsFinished"),
                    RevealedItems = (Get(Get(scope, "_entity"), "_revealed") as IEnumerable)?.Cast<object>().Select(item => new
                    {
                        Type = item.GetType().Name,
                        X = Get(item, "Position") is Vector2I position ? position.X : (int?)null,
                        Y = Get(item, "Position") is Vector2I location ? location.Y : (int?)null,
                        Width = Get(item, "Size") is Vector2I size ? size.X : (int?)null,
                        Height = Get(item, "Size") is Vector2I dimensions ? dimensions.Y : (int?)null
                    }).ToArray()
                } : null,
                Event = eventModel == null ? null : new
                {
                    Title = Text(eventModel.Title),
                    Description = eventModel is AncientEventModel ? null : Text(eventModel.Description),
                    Finished = eventModel.IsFinished
                },
                Dialogue = dialogue,
                Combat = combat == null ? null : new
                {
                    Ref = _combatId,
                    Round = combat.RoundNumber,
                    Side = combat.CurrentSide.ToString(),
                    Phase = player?.PlayerCombatState?.Phase.ToString(),
                    Ready = CombatReady,
                    DrawCount = player!.PlayerCombatState!.DrawPile.Cards.Count,
                    DiscardCount = player.PlayerCombatState.DiscardPile.Cards.Count,
                    ExhaustCount = player.PlayerCombatState.ExhaustPile.Cards.Count,
                    OrbSlots = player.PlayerCombatState.OrbQueue.Capacity,
                    OrbOrder = "evoke_next_first",
                    Orbs = player.PlayerCombatState.OrbQueue.Orbs.Select(Orb).ToArray(),
                    Pets = player.PlayerCombatState.Pets.Select(Pet).ToArray(),
                    Allies = Allies.Select(Pet).ToArray(),
                    Hand = player!.PlayerCombatState!.Hand.Cards.Select(c => Card(c, true)).ToArray(),
                    Enemies = combat.Enemies.Where(c => c.IsAlive).Select(Enemy).ToArray(),
                    Ongoing = true,
                    NoLivingEnemies = !combat.Enemies.Any(c => c.IsAlive),
                    InactiveEnemies = combat.Enemies.Where(c => !c.IsAlive).Select(c => new
                    {
                        Ref = CreatureRef(c),
                        Name = Text(c.Monster?.Title),
                        Alive = false,
                        Hp = c.CurrentHp,
                        Statuses = Powers(c)
                    }).ToArray()
                },
                Player = player == null ? null : new
                {
                    Ref = combat != null ? CreatureRef(player.Creature) : null,
                    Name = Text(player.Character.Title),
                    Hp = player.Creature.CurrentHp,
                    Facing = Facing(player.Creature)?.ToString(),
                    MaxHp = player.Creature.MaxHp,
                    Block = player.Creature.Block,
                    Gold = player.Gold,
                    Energy = combat == null ? (int?)null : player.PlayerCombatState?.Energy,
                    MaxEnergy = combat == null ? (int?)null : player.PlayerCombatState?.MaxEnergy,
                    Stars = combat == null ? (int?)null : player.PlayerCombatState?.Stars,
                    Statuses = Powers(player.Creature),
                    MaxPotions = player.MaxPotionCount,
                    Potions = player.PotionSlots.Select(p => p == null ? null : new
                    {
                        Ref = PotionRef(p),
                        Name = Text(p.Title),
                        Description = Text(p.DynamicDescription),
                        Target = p.TargetType.ToString(),
                        Targets = PotionTargets(p),
                        Usable = !p.IsQueued && p.PassesCustomUsabilityCheck && p.Usage != PotionUsage.Automatic
                            && (p.Usage != PotionUsage.CombatOnly || CombatReady),
                        Effects = Effects(p.ExtraHoverTips),
                        Mechanics = Mechanics(p)
                    }).Where(p => p != null).ToArray(),
                    Relics = player.Relics.Select(ModelInfo).ToArray()
                },
                Act = player?.RunState.CurrentActIndex + 1,
                Floor = player?.RunState.ActFloor,
                TotalFloor = player?.RunState.TotalFloor,
                Ascension = Get(player?.RunState, "AscensionLevel"),
                Ready = ready,
                Pending = pending,
                Choices = choices,
                Text = Labels(scope, ancientChat).Take(100).ToArray(),
                Map = map,
                MapControl = scope is NMapScreen ? _bridge.Call("reference", scope).AsString() : null
            }, Json)!.AsObject();
            snapshot["operation"] = _workflows.Observe(snapshot);
            return snapshot.ToJsonString();
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private static bool HasParentType(Node node, string type)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent())
            if (p.GetType().Name == type)
                return true;

        return false;
    }
    private static bool HasChoiceParent(Node node)
    {
        for (var p = node.GetParent(); p != null; p = p.GetParent())
            if (p is NCardHolder or NMerchantSlot or NCardBundle)
                return true;

        return false;
    }

    public string Inspect(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.GetProperty("kind").GetString();
            if (kind == "game")
                return Serialize(GameInfo(MegaCrit.Sts2.Core.Debug.ReleaseInfoManager.Instance.ReleaseInfo));

            RefreshCombat();
            var player = Player ?? throw new InvalidOperationException("No run in progress");
            var upgrades = document.RootElement.TryGetProperty("upgrades", out var option) && option.ValueKind == JsonValueKind.True;
            if (kind == "card")
            {
                var id = document.RootElement.GetProperty("card").GetString()!;
                if (!_cards.TryGetValue(id, out var card))
                    throw new InvalidOperationException("Card reference is unknown; inspect a current card or offer again");

                return Serialize(Card(card, upgrade: upgrades));
            }
            if (kind == "deck")
            {
                foreach (var old in _deckRefs.Keys.ToArray())
                    if (!player.Deck.Cards.Contains(old))
                        _deckRefs.Remove(old);
                return Serialize(new
                {
                    Cards = player.Deck.Cards.Select(c => Card(c, upgrade: upgrades, deckReference: true)).ToArray()
                });
            }
            if (kind != "piles")
                throw new InvalidOperationException("Unknown inspection");

            if (Combat == null)
                throw new InvalidOperationException("No active combat");

            var state = player.PlayerCombatState!;
            // The UI permits pile inspection; the shuffled draw order is not visible.
            var draw = PublicCardList.Read(state.DrawPile.Cards,
                c => Serialize(Card(c, reference: false)), c => Card(c));
            return Serialize(new
            {
                Draw = new
                {
                    Order = "unknown",
                    Cards = draw
                },
                Discard = state.DiscardPile.Cards.Select(c => Card(c)).ToArray(),
                Exhaust = state.ExhaustPile.Cards.Select(c => Card(c)).ToArray()
            });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private static object GameInfo(MegaCrit.Sts2.Core.Debug.ReleaseInfo? release) => new
    {
        Source = "native_release_info",
        Available = release != null,
        Version = release?.Version,
        Commit = release?.Commit,
        Branch = release?.Branch,
        Date = release?.Date,
        MainAssemblyHash = release?.MainAssemblyHash
    };

    public string Request(string json)
    {
        if (_workflows.Busy)
            return Serialize(new
            {
                error = "A service operation is still running"
            });

        return RequestNative(json);
    }

    public string Workflow(string json)
    {
        try
        {
            var args = JsonNode.Parse(json)!.AsObject();
            return Serialize(args["command"]?.GetValue<string>() switch
            {
                "start" => _workflows.Start(args["args"]!.AsObject()),
                "status" => _workflows.Status(args["operation"]?.GetValue<string>() ?? ""),
                _ => throw new ArgumentException("Unknown service callback command")
            });
        }
        catch (Exception error) { return Serialize(new { error = error.GetBaseException().Message }); }
    }

    private Node ResolveNode(string id) => _bridge.Call("resolve_object", id).AsGodotObject() as Node
        ?? throw new WorkflowService.ActivationRejected("UI reference is stale");

    private void BrowseShop(string id)
    {
        var owner = ResolveNode(id);
        var open = owner.GetType().GetMethod("OpenInventory")
            ?? throw new InvalidOperationException("Native shop inventory handler is unavailable");
        open.Invoke(owner, null);
    }

    private void ActivateChoice(JsonObject choice)
    {
        if (choice["activation"] is JsonObject activation)
        {
            if (activation["kind"]?.GetValue<string>() == "select_card")
            {
                var result = JsonNode.Parse(SelectCardNative(new JsonObject { ["card"] = activation["card"]!.DeepClone() }.ToJsonString()))!;
                if (result["error"] != null)
                    throw new InvalidOperationException(result["error"]!.GetValue<string>());

                return;
            }
            var target = ResolveNode(activation["target"]!.GetValue<string>());
            target.EmitSignal(activation["signal"]!.GetValue<string>(), ResolveNode(activation["argument"]!.GetValue<string>()));
            return;
        }
        var control = ResolveNode(choice["ref"]!.GetValue<string>());
        if (control is not Control visible || !Visible(visible) || control is NClickableControl { IsEnabled: false })
            throw new WorkflowService.ActivationRejected("Choice is no longer enabled and visible");

        var name = control is NCharacterSelectButton ? "Select" : "ForceClick";
        var method = control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new WorkflowService.ActivationRejected("No native activation is available; use generic input explicitly");
        method.Invoke(control, null);
    }

    private string RequestNative(string json)
    {
        Pending? submitted = null;
        try
        {
            RefreshCombat();
            UpdatePending();
            using var document = JsonDocument.Parse(json);
            var args = document.RootElement;
            var kind = args.GetProperty("kind").GetString()!;
            if (_pending?.Status is "accepted" or "running" or "choice_required")
                throw new InvalidOperationException("An action is still pending");

            var player = Player ?? throw new InvalidOperationException("No run in progress");
            var pending = new Pending { Id = Guid.NewGuid().ToString("N"), Kind = kind, Round = Combat?.RoundNumber ?? 0 };
            if (kind == "play")
            {
                if (!CombatReady)
                    throw new InvalidOperationException("Combat is not ready for a card decision");

                var id = args.GetProperty("card").GetString()!;
                if (!_cards.TryGetValue(id, out var card) || !player.PlayerCombatState!.Hand.Cards.Any(c => ReferenceEquals(c, card)))
                    throw new InvalidOperationException("Card reference is stale or no longer in hand");

                if (!card.CanPlay(out var reason, out _))
                    throw new InvalidOperationException("Card cannot be played: " + reason);

                var target = Target(card.TargetType, args, player);
                if (target != null && !card.CanPlayTargeting(target))
                    throw new InvalidOperationException("Card cannot be played on that target");

                pending.Action = new PlayCardAction(card, target);
                _pending = submitted = pending;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(pending.Action);
            }
            else if (kind == "endTurn")
            {
                if (!CombatReady)
                    throw new InvalidOperationException("Combat is not ready to end turn");

                _pending = submitted = pending;
                PlayerCmd.EndTurn(player, canBackOut: false);
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

                if (OfferRef(entry) != id)
                    throw new InvalidOperationException("The shop has restocked this offer; inspect again");

                if (!entry.IsStocked || !entry.EnoughGold)
                    throw new InvalidOperationException("Offer is sold out or unaffordable");

                _pending = submitted = pending;
                pending.Purpose = entry is MerchantCardRemovalEntry ? "card_removal" : null;
                pending.Purchase = entry.OnTryPurchaseWrapper(inventory);
                pending.Task = pending.Purchase;
            }
            else if (kind == "divineCell")
            {
                if (Scope() is not NCrystalSphereScreen screen || Get(Get(screen, "_entity"), "IsFinished") is true)
                    throw new InvalidOperationException("No active Crystal Sphere decision");

                var x = args.GetProperty("x").GetInt32();
                var y = args.GetProperty("y").GetInt32();
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
                    if (!player.CanRemovePotions || potion.IsQueued)
                        throw new InvalidOperationException("Potion cannot be discarded now");

                    pending.Potion = potion;
                    _pending = submitted = pending;
                    pending.Task = PotionCmd.Discard(potion);
                    return Serialize(new
                    {
                        Action = pending.Id,
                        Status = "accepted"
                    });
                }
                if (potion.IsQueued || !potion.PassesCustomUsabilityCheck || potion.Usage == PotionUsage.Automatic)
                    throw new InvalidOperationException("Potion cannot be used now");

                if (potion.Usage == PotionUsage.CombatOnly && !CombatReady)
                    throw new InvalidOperationException("Potion requires ready combat");

                if (Combat != null && !CombatReady)
                    throw new InvalidOperationException("Combat is not ready for a potion decision");

                if (potion.TargetType == TargetType.TargetedNoCreature)
                {
                    var shopkeeper = ShopkeeperTarget(potion);
                    if (shopkeeper == null || !Visible(shopkeeper) || !args.TryGetProperty("target", out var targetRef)
                        || targetRef.GetString() != _bridge.Call("reference", shopkeeper).AsString())
                        throw new InvalidOperationException("Choose the current visible shopkeeper target before opening the inventory");
                }
                var target = potion.TargetType == TargetType.Self ? player.Creature : Target(potion.TargetType, args, player);
                pending.Potion = potion;
                _pending = submitted = pending;
                var queue = RunManager.Instance.ActionQueueSet;
                var slot = (uint)player.PotionSlots.ToList().FindIndex(p => ReferenceEquals(p, potion));
                Action<GameAction>? started = null;
                started = action =>
                {
                    if (action is not UsePotionAction use || use.Player != player || use.PotionIndex != slot)
                        return;

                    pending.Action = action;
                    queue.ActionEnqueued -= started;
                };
                queue.ActionEnqueued += started;
                try
                {
                    potion.EnqueueManualUse(target);
                }
                catch { queue.ActionEnqueued -= started; throw; }
            }
            else
                throw new InvalidOperationException("Unknown adapter request");

            return Serialize(new
            {
                Action = pending.Id,
                Status = "accepted"
            });
        }
        catch (Exception error)
        {
            if (submitted != null)
            {
                submitted.Status = "failed";
                submitted.Error = error.GetBaseException().Message;
            }
            return Serialize(new
            {
                error = error.GetBaseException().Message
            });
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
            if (!args.TryGetProperty("target", out var selection) || selection.ValueKind == JsonValueKind.Null)
                return player.Creature;

            var candidates = type == TargetType.AnyPlayer ? Combat?.Players.Select(p => p.Creature) : Allies;
            return candidates?.FirstOrDefault(c => c.IsAlive && CreatureRef(c) == selection.GetString())
                ?? throw new InvalidOperationException("Target reference is stale or not a valid ally");
        }
        if (type == TargetType.Osty)
            return player.IsOstyAlive ? player.Osty : throw new InvalidOperationException("Osty is unavailable");

        if (type is TargetType.None or TargetType.Self or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AllAllies or TargetType.TargetedNoCreature)
            return null;

        throw new InvalidOperationException("Unsupported target type: " + type);
    }

    private static bool IsCardSelection(object? scope) => scope is NCardGridSelectionScreen
        or NChooseACardSelectionScreen || scope is NPlayerHand { IsInCardSelection: true };

    private static bool ActiveHandSelection(Node scope) => scope is NCombatRoom room
        && IsCardSelection(room.Ui.Hand) && Visible(room.Ui.Hand);

    private static bool ActiveCardSelection(Node scope) => IsCardSelection(scope) || ActiveHandSelection(scope);

    private void UpdatePending()
    {
        var p = _pending;
        if (p == null || p.Status is "completed" or "failed" or "cancelled")
            return;

        if (p.Action != null)
        {
            var state = p.Action.State.ToString();
            if (state == "Canceled")
            {
                p.Status = "cancelled";
                p.Error = "The game cancelled this action";
                p.Action = null;
                p.Potion = null;
                return;
            }
            p.Status = state == "GatheringPlayerChoice" || ActiveCardSelection(Scope()) ? "choice_required" : "running";
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
                p.Status = ActiveCardSelection(Scope()) ? "choice_required" : "running";
                return;
            }
            p.Error = p.Task.Exception?.GetBaseException().Message;
            p.Status = p.Task.IsFaulted ? "failed" : p.Task.IsCanceled || p.Purchase is { IsCompletedSuccessfully: true, Result: false } ? "cancelled" : "completed";
        }
        else if (p.Kind == "abandonRun")
        {
            if (RunManager.Instance.IsAbandoned && Scope().GetType().Name == "NGameOverScreen")
                p.Status = "completed";
        }
        else if (p.Kind == "endTurn")
        {
            if (!PlayPhase || Combat?.RoundNumber != p.Round)
                p.LeftPlayPhase = true;
            if (Combat == null || (p.LeftPlayPhase && CombatReady && Combat.RoundNumber > p.Round))
                p.Status = "completed";
            else
                p.Status = ActiveCardSelection(Scope()) ? "choice_required" : "running";
        }
        else if (p.Potion != null && Player != null && !Player.PotionSlots.Any(x => ReferenceEquals(x, p.Potion)) && Idle)
            p.Status = "completed";
        if (p.Status is "completed" or "failed" or "cancelled")
        {
            p.Action = null;
            p.Potion = null;
            p.Task = null;
            p.Purchase = null;
        }
    }

    public string Frame()
    {
        if (Time.GetTicksMsec() < _nextFrameCheck)
            return "";

        _nextFrameCheck = Time.GetTicksMsec() + 100;
        try
        {
            RefreshCombat();
            UpdatePending();
            _workflows.Advance();
            var scope = Scope();
            var layout = Walk(scope).OfType<NAncientEventLayout>().FirstOrDefault();
            var ancientChat = ChatAvailable(Get(layout, "_dialogueHitbox") as Control);
            NTimelineScreen? timeline = null;
            for (Node? parent = scope; parent != null; parent = parent.GetParent())
                if (parent is NTimelineScreen found)
                {
                    timeline = found;
                    break;
                }
            var timelineBlocked = Get(timeline, "_inputBlocker") is Control { Visible: true };
            var skippedRewards = (Get(scope, "_skippedRewardButtons") as ICollection)?.Count;
            var enabled = Walk(scope).Count(n => Visible(n) && n is NClickableControl { IsEnabled: true });
            var selectionOwner = scope is NCardGridSelectionScreen ? scope : NCombatRoom.Instance?.Ui.Hand as object;
            var player = Player;
            var hand = NCombatRoom.Instance?.Ui.Hand;
            var key = Serialize(new
            {
                Selection = SelectedCards(selectionOwner).Select(CardRef).Order(StringComparer.Ordinal).ToArray(),
                Screen = scope.GetInstanceId(),
                Round = Combat?.RoundNumber,
                Phase = player?.PlayerCombatState?.Phase,
                CombatManager.Instance.PlayerActionsDisabled,
                Idle,
                Pending = _pending == null ? null : new
                {
                    _pending.Id,
                    _pending.Status
                },
                Operation = _workflows.Summary,
                HandMode = hand?.CurrentMode,
                InCardPlay = hand?.InCardPlay,
                Gold = player?.Gold,
                Hp = player?.Creature.CurrentHp,
                Facing = player == null ? null : Facing(player.Creature),
                EnabledControls = enabled,
                MapTraveling = NMapScreen.Instance?.IsTraveling,
                MapTravelEnabled = NMapScreen.Instance?.IsTravelEnabled,
                InventoryOpen = NMerchantRoom.Instance?.Inventory?.IsOpen,
                InTransition = NGame.Instance?.Transition?.InTransition,
                TimelineBlocked = timelineBlocked,
                TimelineQueued = timeline?.IsScreenQueued(),
                SkippedRewards = skippedRewards,
                Complete = Get(scope, "IsComplete"),
                SummaryAnimating = Get(scope, "_isAnimatingSummary"),
                Text = Labels(scope, ancientChat).Take(100).ToArray()
            });
            if (key == _fingerprint)
                return "";

            _fingerprint = key;
            return (++_revision).ToString();
        }
        catch { return ""; }
    }
}
