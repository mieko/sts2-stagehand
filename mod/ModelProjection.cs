using System.Collections;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using static Stagehand.Sts2.NativeData;

namespace Stagehand.Sts2;

// Public model facts shared by cards, offers, piles, hover tips and status views.
// References and game interaction stay with Sts2Host.
internal static class ModelProjection
{
    internal static object[] Effects(object? tips) => tips is not IEnumerable items ? [] : items.Cast<object>()
        .Select(t => Get(t, "Card") is CardModel card
            ? new
            {
                Name = card.Title,
                Description = CardDescription(card, PileType.None),
                Card = (CardFacts?)CardDetails(card, PileType.None, printed: true),
                Mechanics = Array.Empty<string>()
            }
            : new
            {
                Name = Text(Get(t, "Title")),
                Description = Text(Get(t, "Description")),
                Card = (CardFacts?)null,
                Mechanics = Mechanics(Get(t, "CanonicalModel") as AbstractModel)
            })
        .Where(t => t.Name.Length > 0 && t.Description.Length > 0).Distinct().Cast<object>().ToArray();

    internal static object ModelInfo(object? model) => new
    {
        Id = (model as AbstractModel)?.Id.Entry,
        Name = Text(Get(model, "Title")),
        Description = Text(Get(model, "DynamicDescription")),
        Counter = Get(model, "ShowCounter") is true ? Get(model, "DisplayAmount") : null,
        Status = Get(model, "Status")?.ToString(),
        UsedUp = Get(model, "IsUsedUp"),
        Stacks = Get(model, "StackCount"),
        Mechanics = Mechanics(model as AbstractModel),
        Effects = Effects(Get(model, "HoverTipsExcludingRelic") ?? Get(model, "ExtraHoverTips"))
    };

    internal static object? CardModifier(object? modifier) => modifier == null ? null : new
    {
        Name = Text(Get(modifier, "Title")),
        Amount = Get(modifier, "Amount"),
        Status = Get(modifier, "Status")?.ToString(),
        Description = Text(Get(modifier, "DynamicDescription")),
        ExtraCardText = Text(Get(modifier, "DynamicExtraCardText")),
        Effects = Effects(Get(modifier, "HoverTips"))
    };

    internal static string CardDescription(CardModel card, PileType pile)
    {
        try
        {
            return Text(card.GetDescriptionForPile(pile));
        }
        catch (Exception error) { return $"Description unavailable ({error.GetType().Name})"; }
    }

    internal sealed record CardCosts(object Energy, object? Stars, string Scope);
    internal static CardCosts Costs(CardModel card, bool printed)
    {
        var stars = printed ? card.CurrentStarCost : card.GetStarCostWithModifiers();
        return new(card.EnergyCost.CostsX ? "X" : printed ? card.EnergyCost.GetWithModifiers(CostModifiers.Local) : card.EnergyCost.GetAmountToSpend(),
            card.HasStarCostX ? "X" : stars < 0 ? null : stars, printed ? "printed_related_card" : "current_card");
    }

    internal sealed record CardFacts(string Id, string Name, string Description, object Cost, object? StarCost,
        string CostScope, string Type, string Target, bool Upgraded, string[] Mechanics, object Values, object ValueContext);

    internal static CardFacts CardDetails(CardModel card, PileType pile, bool printed = false)
    {
        var costs = Costs(card, printed);
        return new(card.Id.Entry, card.Title, CardDescription(card, pile), costs.Energy, costs.Stars, costs.Scope,
            card.Type.ToString(), card.TargetType.ToString(), card.IsUpgraded, Mechanics(card), CardValues(card), CardValueContext(card, pile));
    }

    // These are the model's public display variables, not an effect AST. Reading
    // them must not refresh or mutate the game's target-dependent preview state.
    internal static object CardValues(CardModel card)
    {
        return card.DynamicVars.Where(pair => pair.Value is not BoolVar and not StringVar)
            .ToDictionary(pair => pair.Key, pair =>
            {
                var value = pair.Value;
                var type = value.GetType();
                var power = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PowerVar<>)
                    ? type.GetGenericArguments()[0].Name : null;
                var strengthLoss = card is MegaCrit.Sts2.Core.Models.Cards.CrushUnder && pair.Key == "StrengthLoss";
                var unit = value switch
                {
                    DamageVar or CalculatedDamageVar or ExtraDamageVar => "damage",
                    BlockVar or CalculatedBlockVar => "block",
                    RepeatVar => card is MegaCrit.Sts2.Core.Models.Cards.CelestialMight ? "hits" : "repetitions",
                    CardsVar => "cards",
                    EnergyVar => "energy",
                    StarsVar => "stars",
                    _ => power != null || strengthLoss ? "stacks" : null
                };

                return (object)new
                {
                    NativeType = type.Name.Split('`')[0],
                    Unit = unit,
                    Power = power,
                    Base = value.BaseValue,
                    Enchanted = value.EnchantedValue,
                    Preview = value.PreviewValue,
                    Meaning = strengthLoss
                        ? new
                        {
                            Effect = "temporary_strength",
                            Direction = "loss",
                            Target = "AllEnemies",
                            Duration = "affected_side_turn_end"
                        } : null
                };
            });
    }

    internal static object CardValueContext(CardModel card, PileType descriptionPile) => new
    {
        Source = "cached_native_model",
        DescriptionPile = descriptionPile.ToString(),
        ModelPile = card.Pile?.Type.ToString(),
        UpgradePreview = card.UpgradePreviewType.ToString(),
        GlobalHooksEligible = card.CombatState != null && (card.Pile?.Type is PileType.Hand or PileType.Play
            || card.UpgradePreviewType == CardUpgradePreviewType.Combat),
        Target = "not_recorded",
        ProspectiveDamage = false
    };

    // Native-backed clarifications for rules that the localized prose leaves
    // implicit. These describe mechanics, not a separate prediction/rules engine.
    internal static string[] Mechanics(AbstractModel? model) => model switch
    {
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
        _ => []
    };

    internal static CardModel CreateUpgradePreview(CardModel card)
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

    internal static object[] Powers(Creature creature) => creature.Powers.Where(p => p.IsVisible)
        .Select(p => (object)new
        {
            Name = Text(p.Title),
            Amount = p.DisplayAmount,
            Description = Text(Get(p.HoverTips.FirstOrDefault(t => t.Id == p.Id.ToString()), "Description") ?? p.SmartDescription),
            Mechanics = Mechanics(p)
        }).ToArray();

    internal static object Orb(OrbModel orb, int queueIndex)
    {
        var description = orb.SmartDescription;
        description.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
        description.Add("Passive", orb.PassiveVal);
        description.Add("Evoke", orb.EvokeVal);
        return new
        {
            QueuePosition = queueIndex + 1,
            EvokeNext = queueIndex == 0,
            Name = Text(orb.Title),
            Passive = orb.PassiveVal,
            Evoke = orb.EvokeVal,
            Description = Text(description),
            Effects = Effects(orb.HoverTips)
        };
    }

}
