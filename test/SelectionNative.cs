using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using Stagehand.Sts2;

// Managed shells exercise the real installed game types and production extraction
// without loading an engine, creating native nodes, or touching a game/save.
static object Shell(Type type) => RuntimeHelpers.GetUninitializedObject(type);
static void Set(object owner, string field, object value) => owner.GetType()
    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
var read = typeof(Sts2Host).GetMethod("EligibleCards", BindingFlags.Static | BindingFlags.NonPublic)!;
var eligible = Enumerable.Range(0, 31).Select(_ => (CardModel)Shell(typeof(Hotfix))).ToList();
var excluded = (CardModel)Shell(typeof(Hotfix));
var cases = 0;
foreach (var type in new[] { typeof(NCombatPileCardSelectScreen), typeof(NDeckCardSelectScreen),
    typeof(NDeckUpgradeSelectScreen), typeof(NDeckTransformSelectScreen) })
{
    var screen = Shell(type);
    var grid = Shell(typeof(NCardGrid));
    Set(screen, "_grid", grid);
    Set(screen, "_cards", Array.Empty<CardModel>());
    Set(grid, "_cards", eligible);
    // Materialized holders are intentionally absent. They cannot establish the
    // full eligible set, particularly for a target after the first 25 cards.
    Set(grid, "_cardRows", new List<List<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder>>());
    var actual = (CardModel[])read.Invoke(null, [screen])!;
    if (actual.Length != 31 || !ReferenceEquals(actual[30], eligible[30]))
        throw new Exception(type.Name + " omitted an eligible unmaterialized card");
    Set(screen, "_cards", new[] { excluded });
    Set(grid, "_cards", new List<CardModel> { eligible[30] });
    actual = (CardModel[])read.Invoke(null, [screen])!;
    if (actual.Length != 1 || !ReferenceEquals(actual[0], eligible[30]) || actual.Contains(excluded))
        throw new Exception(type.Name + " used a stale/unfiltered screen list");
    cases += 2;
}
Console.WriteLine($"Native selection ownership: {cases} cases passed (no engine/game launched)");

// A native choice can suspend BeforeHandDraw while the executor is busy and
// PlayerCombatState.Phase is Start. Its active screen is the interaction boundary.
var awaitsChoice = typeof(Sts2Host).GetMethod("IsCardSelection", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("The host must recognize native card-choice boundaries");
foreach (var type in new[] { typeof(NCombatPileCardSelectScreen), typeof(NDeckCardSelectScreen),
    typeof(NDeckUpgradeSelectScreen), typeof(NDeckTransformSelectScreen), typeof(NChooseACardSelectionScreen) })
{
    if (!(bool)awaitsChoice.Invoke(null, [Shell(type)])!)
        throw new Exception(type.Name + " was not recognized as a native card choice");
}
if ((bool)awaitsChoice.Invoke(null, [null])! || (bool)awaitsChoice.Invoke(null, [Shell(typeof(NCardGrid))])!)
    throw new Exception("An absent overlay or bare grid is not a card-choice boundary");
Console.WriteLine("Native card-choice boundaries: five real screens recognized, absent/non-screen rejected");

// Comparison IDs describe permanent-deck model identity, independently of the
// action references that reset at a combat boundary.
var host = Shell(typeof(Sts2Host));
Set(host, "_deckRefs", new Dictionary<CardModel, string>(ReferenceEqualityComparer.Instance));
var deckRef = typeof(Sts2Host).GetMethod("DeckRef", BindingFlags.Instance | BindingFlags.NonPublic)!;
var first = (string)deckRef.Invoke(host, [eligible[0]])!;
var duplicate = (string)deckRef.Invoke(host, [eligible[1]])!;
if (first == duplicate || (string)deckRef.Invoke(host, [eligible[0]])! != first)
    throw new Exception("Permanent deck comparison identity merged or changed a card copy");
Console.WriteLine("Permanent deck comparison identity: distinct copies and stable reread passed");

static void Field(object owner, Type type, string name, object value) => type
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
static void Mutable(AbstractModel model) => Field(model, typeof(AbstractModel), "<IsMutable>k__BackingField", true);

var original = (Radiate)Shell(typeof(Radiate));
Mutable(original);
Field(original, typeof(CardModel), "_keywords", new HashSet<CardKeyword>());
Field(original, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
Field(original, typeof(CardModel), "_canonicalInstance", original);
var vars = new DynamicVarSet([new DamageVar(3, ValueProp.Move)]);
vars.InitializeWithOwner(original);
Field(original, typeof(CardModel), "_dynamicVars", vars);
var owner = (Player)Shell(typeof(Player));
Set(owner, "_runState", NullRunState.Instance);
Set(owner, "_runPiles", Array.Empty<CardPile>());
Field(original, typeof(CardModel), "_owner", owner);
var enchantment = (Instinct)Shell(typeof(Instinct));
Mutable(enchantment);
Field(original, typeof(CardModel), "<Enchantment>k__BackingField", enchantment);
original.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, original.DynamicVars);
if (original.DynamicVars.Damage.PreviewValue != 6) throw new Exception("Instinct fixture should show native base3 doubled6");
var makePreview = typeof(Sts2Host).GetMethod("CreateUpgradePreview", BindingFlags.Static | BindingFlags.NonPublic)!;
var upgraded = (CardModel)makePreview.Invoke(null, [original])!;
if (upgraded.DynamicVars.Damage.BaseValue != 4 || upgraded.DynamicVars.Damage.PreviewValue != 8)
    throw new Exception("Native Radiate upgrade preview must include Instinct: base4 doubled8");
if (original.IsUpgraded || original.DynamicVars.Damage.BaseValue != 3 || original.DynamicVars.Damage.PreviewValue != 6
    || !ReferenceEquals(original.Enchantment, enchantment) || ReferenceEquals(upgraded.Enchantment, enchantment)
    || ReferenceEquals(upgraded.DynamicVars, original.DynamicVars))
    throw new Exception("Detached upgrade preview mutated or retained mutable source state");
Console.WriteLine("Native enchanted upgrade preview: 6→8, source card/enchantment untouched");

var freeCard = (NeutronAegis)Shell(typeof(NeutronAegis));
Mutable(freeCard);
Field(freeCard, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
Field(freeCard, typeof(CardModel), "_energyCost", new CardEnergyCost(freeCard, 1, false));
freeCard.SetToFreeThisTurn();
var costs = typeof(Sts2Host).GetMethod("Costs", BindingFlags.Static | BindingFlags.NonPublic)!;
var actualCosts = costs.Invoke(null, [freeCard, false])!;
if ((int)actualCosts.GetType().GetProperty("Energy")!.GetValue(actualCosts)! != 0
    || (int)actualCosts.GetType().GetProperty("Stars")!.GetValue(actualCosts)! != 0)
    throw new Exception("Native Power Potion free-play costs must waive both budgets");
Console.WriteLine("Native temporary free-play projection: energy0/stars0");

var blade = (SovereignBlade)Shell(typeof(SovereignBlade));
Mutable(blade);
Field(blade, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
Field(blade, typeof(CardModel), "_energyCost", new CardEnergyCost(blade, 2, false));
var printed = costs.Invoke(null, [blade, true])!;
if ((int)printed.GetType().GetProperty("Energy")!.GetValue(printed)! != 2
    || printed.GetType().GetProperty("Stars")!.GetValue(printed) != null
    || (string)printed.GetType().GetProperty("Scope")!.GetValue(printed)! != "printed_related_card")
    throw new Exception("Related card costs must be marked printed, without claiming playability");
blade.EnergyCost.UpgradeBy(-1);
printed = costs.Invoke(null, [blade, true])!;
if ((int)printed.GetType().GetProperty("Energy")!.GetValue(printed)! != 1)
    throw new Exception("Printed cost must retain native local upgrade changes");
Console.WriteLine("Related card costs: printed scope, absent stars and upgraded energy preserved");

var resultPhase = typeof(Sts2Host).GetMethod("ResultPhase", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("Result phase must follow native control readiness, not container visibility");
foreach (var (animating, next, menu, board, earned, expected) in new[] {
    (false, true, false, false, 4, "outcome"),
    (true, false, false, false, 5, "summary_animation"),
    (false, false, true, false, 5, "earned_epochs"),
    (false, false, true, false, 0, "summary"),
    (false, false, false, true, 5, "leaderboard"),
    (false, false, false, false, 5, "transition") })
{
    if ((string)resultPhase.Invoke(null, [animating, next, menu, board, earned])! != expected)
        throw new Exception("Incorrect native result interaction phase: " + expected);
}
Console.WriteLine("Native ending phase: outcome, score animation, earned epochs, summary, leaderboard and transition passed");
