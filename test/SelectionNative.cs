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
var projection = typeof(Sts2Host).Assembly.GetType("Stagehand.Sts2.ModelProjection")!;
var makePreview = projection.GetMethod("CreateUpgradePreview", BindingFlags.Static | BindingFlags.NonPublic)!;
var upgraded = (CardModel)makePreview.Invoke(null, [original])!;
if (upgraded.DynamicVars.Damage.BaseValue != 4 || upgraded.DynamicVars.Damage.PreviewValue != 8)
    throw new Exception("Native Radiate upgrade preview must include Instinct: base4 doubled8");

if (original.IsUpgraded || original.DynamicVars.Damage.BaseValue != 3 || original.DynamicVars.Damage.PreviewValue != 6
    || !ReferenceEquals(original.Enchantment, enchantment) || ReferenceEquals(upgraded.Enchantment, enchantment)
    || ReferenceEquals(upgraded.DynamicVars, original.DynamicVars))
    throw new Exception("Detached upgrade preview mutated or retained mutable source state");

Console.WriteLine("Native enchanted upgrade preview: 6→8, source card/enchantment untouched");

// A reward is not in the deck. Its native model still supports the same detached
// preview, including keyword-only upgrades that have no numeric value change.
var offered = (KnowThyPlace)Shell(typeof(KnowThyPlace));
Mutable(offered);
Field(offered, typeof(CardModel), "_keywords", new HashSet<CardKeyword> { CardKeyword.Exhaust });
Field(offered, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
Field(offered, typeof(CardModel), "_canonicalInstance", offered);
Field(offered, typeof(CardModel), "_owner", owner);
Field(offered, typeof(CardModel), "_energyCost", new CardEnergyCost(offered, 0, false));
var offeredVars = new DynamicVarSet([
    new PowerVar<MegaCrit.Sts2.Core.Models.Powers.WeakPower>(1m),
    new PowerVar<MegaCrit.Sts2.Core.Models.Powers.VulnerablePower>(1m) ]);
offeredVars.InitializeWithOwner(offered);
Field(offered, typeof(CardModel), "_dynamicVars", offeredVars);
var offerPreview = (CardModel)makePreview.Invoke(null, [offered])!;
if (!offerPreview.IsUpgraded || offerPreview.Keywords.Contains(CardKeyword.Exhaust)
    || offerPreview.DynamicVars.Weak.BaseValue != 1 || offerPreview.DynamicVars.Vulnerable.BaseValue != 1
    || offerPreview.EnergyCost.GetWithModifiers(CostModifiers.Local) != 0
    || offerPreview.UpgradePreviewType != CardUpgradePreviewType.Deck)
    throw new Exception("Unowned Know Thy Place preview must remove only Exhaust, at native deck-preview scope");

if (offered.IsUpgraded || !offered.Keywords.Contains(CardKeyword.Exhaust)
    || offered.DynamicVars.Weak.BaseValue != 1 || offered.DynamicVars.Vulnerable.BaseValue != 1
    || offered.EnergyCost.GetWithModifiers(CostModifiers.Local) != 0
    || ReferenceEquals(offered.DynamicVars, offerPreview.DynamicVars)
    || ReferenceEquals(offered.EnergyCost, offerPreview.EnergyCost) || offered.Pile != null)
    throw new Exception("Offered-card upgrade inspection mutated or attached the unowned source model");

Console.WriteLine("Native unowned Know Thy Place preview: Exhaust removed, costs/debuffs and source unchanged");

var numbers = projection.GetMethod("CardValues", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("Card inspection must expose native structured values");
static System.Text.Json.JsonElement Project(MethodInfo method, params object[] args) =>
    System.Text.Json.JsonSerializer.SerializeToElement(method.Invoke(null, args), new System.Text.Json.JsonSerializerOptions {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
CardModel NativeCard(Type type)
{
    var card = (CardModel)Shell(type);
    Mutable(card);
    Field(card, typeof(CardModel), "_keywords", new HashSet<CardKeyword>(card.CanonicalKeywords));
    Field(card, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
    Field(card, typeof(CardModel), "_canonicalInstance", card);
    Field(card, typeof(CardModel), "_owner", owner);
    var nativeVars = (IEnumerable<DynamicVar>)typeof(CardModel)
        .GetProperty("CanonicalVars", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card)!;
    var values = new DynamicVarSet(nativeVars);
    values.InitializeWithOwner(card);
    Field(card, typeof(CardModel), "_dynamicVars", values);
    return card;
}
var crush = NativeCard(typeof(CrushUnder));
var celestial = NativeCard(typeof(CelestialMight));
var crushValues = Project(numbers, crush);
var crushUpgrade = (CardModel)makePreview.Invoke(null, [crush])!;
var crushNextValues = Project(numbers, crushUpgrade);
if (crushValues.GetProperty("Damage").GetProperty("preview").GetDecimal() != 7
    || crushValues.GetProperty("StrengthLoss").GetProperty("preview").GetDecimal() != 1
    || crushNextValues.GetProperty("Damage").GetProperty("preview").GetDecimal() != 8
    || crushNextValues.GetProperty("StrengthLoss").GetProperty("preview").GetDecimal() != 2
    || crushValues.GetProperty("StrengthLoss").GetProperty("meaning").GetProperty("direction").GetString() != "loss"
    || crushValues.GetProperty("StrengthLoss").GetProperty("meaning").GetProperty("duration").GetString() != "affected_side_turn_end")
    throw new Exception("Crush Under must expose native damage and temporary Strength loss for current/upgrade models");

var celestialValues = Project(numbers, celestial);
var celestialUpgrade = (CardModel)makePreview.Invoke(null, [celestial])!;
if (celestialValues.GetProperty("Damage").GetProperty("preview").GetDecimal() != 6
    || celestialValues.GetProperty("Repeat").GetProperty("preview").GetDecimal() != 3
    || Project(numbers, celestialUpgrade).GetProperty("Repeat").GetProperty("preview").GetDecimal() != 4
    || celestialValues.GetProperty("Repeat").GetProperty("unit").GetString() != "hits")
    throw new Exception("Celestial Might must expose native per-hit damage and hit count independently");

original.DynamicVars.Damage.PreviewValue = 12;
var cached = Project(numbers, original).GetProperty("Damage");
if (cached.GetProperty("base").GetDecimal() != 3 || cached.GetProperty("enchanted").GetDecimal() != 6
    || cached.GetProperty("preview").GetDecimal() != 12 || original.DynamicVars.Damage.PreviewValue != 12)
    throw new Exception("Native cached previews and base/enchantment values must stay distinct and unchanged");

var valueContext = projection.GetMethod("CardValueContext", BindingFlags.Static | BindingFlags.NonPublic)!;
var context = Project(valueContext, original, PileType.None);
if (context.GetProperty("source").GetString() != "cached_native_model"
    || context.GetProperty("target").GetString() != "not_recorded"
    || context.GetProperty("prospectiveDamage").GetBoolean())
    throw new Exception("Numeric card previews must not masquerade as prospective target damage");

var weak = Project(numbers, offered).GetProperty("WeakPower");
if (weak.GetProperty("power").GetString() != "WeakPower" || weak.GetProperty("unit").GetString() != "stacks")
    throw new Exception("Native typed power values must retain their power identity");

var unknown = NativeCard(typeof(KnowThyPlace));
var templateVars = new DynamicVarSet([new DynamicVar("Unmapped", 2), new BoolVar("Flag", true), new StringVar("Text", "template")]);
templateVars.InitializeWithOwner(unknown);
Field(unknown, typeof(CardModel), "_dynamicVars", templateVars);
var unknownValues = Project(numbers, unknown);
if (unknownValues.GetProperty("Unmapped").GetProperty("unit").ValueKind != System.Text.Json.JsonValueKind.Null
    || unknownValues.GetProperty("Unmapped").GetProperty("preview").GetDecimal() != 2
    || unknownValues.TryGetProperty("Flag", out _) || unknownValues.TryGetProperty("Text", out _))
    throw new Exception("Unknown numeric units stay unknown; nonnumeric template data must not masquerade as zero effects");

Console.WriteLine("Native card values: current/upgrade damage, hit count, temporary Strength, cached context and source preservation passed");

var relativeSide = typeof(Sts2Host).GetMethod("SurroundedSide", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var (facing, left, right, expectedSide, behind) in new[] {
    (MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right, true, false, "Left", true),
    (MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Left, true, false, "Left", false),
    (MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Left, false, true, "Right", true),
    (MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right, false, true, "Right", false) })
{
    var side = Project(relativeSide, facing, left, right);
    if (side.GetProperty("side").GetString() != expectedSide || side.GetProperty("behind").GetBoolean() != behind)
        throw new Exception("Surrounded relative side must match native directional power/facing semantics");
}
if (Project(relativeSide, null, true, false).ValueKind != System.Text.Json.JsonValueKind.Null
    || Project(relativeSide, MegaCrit.Sts2.Core.Models.Powers.SurroundedPower.Direction.Right, false, false)
        .GetProperty("behind").ValueKind != System.Text.Json.JsonValueKind.Null)
    throw new Exception("Absent Surrounded/side information must remain unknown");

Console.WriteLine("Native Surrounded facing: both directions/relative sides and unknown cases passed");

var freeCard = (NeutronAegis)Shell(typeof(NeutronAegis));
Mutable(freeCard);
Field(freeCard, typeof(CardModel), "_temporaryStarCosts", new List<TemporaryCardCost>());
Field(freeCard, typeof(CardModel), "_energyCost", new CardEnergyCost(freeCard, 1, false));
freeCard.SetToFreeThisTurn();
var costs = projection.GetMethod("Costs", BindingFlags.Static | BindingFlags.NonPublic)!;
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
