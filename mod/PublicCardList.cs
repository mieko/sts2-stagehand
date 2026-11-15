namespace Stagehand.Sts2;

// Sort before allocating references, then normalize equal-description copies
// with existing references so neither allocation nor output preserves shuffled order.
internal static class PublicCardList
{
    internal static TView[] Read<TCard, TView>(IEnumerable<TCard> cards,
        Func<TCard, string> publicDescription, Func<TCard, TView> view) =>
        cards.OrderBy(publicDescription, StringComparer.Ordinal).Select(view)
            .OrderBy(item => System.Text.Json.JsonSerializer.Serialize(item), StringComparer.Ordinal).ToArray();
}
