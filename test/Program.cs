using Stagehand.Sts2;
using System.Text.Json;

string Inspect(Card[] shuffled, Dictionary<Card, int> known)
{
    var refs = new Dictionary<Card, int>(known);
    var next = refs.Values.DefaultIfEmpty(0).Max();
    var visible = PublicCardList.Read(shuffled, card => card.Name, card => {
        if (!refs.TryGetValue(card, out var id)) refs[card] = id = ++next;
        return new { Ref = id, Name = card.Name };
    });
    return JsonSerializer.Serialize(visible);
}
bool Check(string name, Card[] firstOrder, Card[] secondOrder, Dictionary<Card, int> known)
{
    var first = Inspect(firstOrder, known);
    var second = Inspect(secondOrder, known);
    if (first == second) return true;
    Console.Error.WriteLine($"{name}: hidden permutation changed public contents/references: {first} != {second}");
    return false;
}
var strike = new Card("Strike", 1);
var defend = new Card("Defend", 2);
var bash = new Card("Bash", 3);
var copy = new Card("Strike", 4);
if (!Check("Unregistered contents", [strike, defend, bash], [bash, strike, defend], [])) return 1;
if (!Check("Known identical copies", [strike, copy], [copy, strike], new() { [strike] = 12, [copy] = 47 })) return 1;
if (!Check("Mixed known/new copies", [strike, copy, defend], [defend, copy, strike], new() { [strike] = 12 })) return 1;
Console.WriteLine("Public unordered-card order/reference independence: 3 cases passed");
return 0;
record Card(string Name, int Copy);
