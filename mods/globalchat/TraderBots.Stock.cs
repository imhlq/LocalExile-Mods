using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

sealed record TradeStockEntry(string Currency, ushort Amount, string? BaseName = null, string? UniqueName = null,
    bool AutoAdvertise = true, ushort? BuybackAmount = null, int? MinimumLevel = null, string[]? Aliases = null);

[JsonSerializable(typeof(TradeStockEntry[]))]
sealed partial class TradeListingJsonContext : JsonSerializerContext;

sealed partial class TraderBots
{
    sealed record StockItem(TradeStockEntry Entry, string Name, ItemCatalog.BaseType Base, UniqueCatalog.Definition? Unique,
        uint? NameWord, Item Price, Item? Buyback)
    {
        internal int MinimumLevel => Entry.MinimumLevel ?? (Unique is null ? Base.DropLevel : 1);

        internal bool Named(string text) => string.Equals(Name, text, StringComparison.OrdinalIgnoreCase)
            || Entry.Aliases?.Contains(text, StringComparer.OrdinalIgnoreCase) == true;

        internal bool Matches(Item item) => item.TypeHash == Base.Hash && (NameWord is uint word
            ? item.Rarity == ItemRarity.Unique && item.NameWords.Contains(word)
            : item.Rarity != ItemRarity.Unique);

        internal GeneratedGear? Create(ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques, int level)
        {
            Item? item = Unique is { } unique ? uniques.Create(unique, Random.Shared) : RollBase(rolls, Base, level);
            return item is null ? null : new(WithPrice(item, Price), PriceText(catalog, Price));
        }
    }

    sealed class TradeStock
    {
        readonly StockItem[] items;
        readonly StockItem[] advertised;
        readonly int[] eligible = new int[MaxListingLevel + 1];

        public TradeStock(StockItem[] items)
        {
            this.items = items;
            advertised = [.. items.Where(item => item.Entry.AutoAdvertise).OrderBy(item => item.MinimumLevel)];
            int count = 0;
            for (int level = 1; level < eligible.Length; level++)
            {
                while (count < advertised.Length && advertised[count].MinimumLevel <= level) count++;
                eligible[level] = count;
            }
        }

        internal StockItem? PickAdvert(int level) => eligible[level] is > 0 and var count
            ? advertised[Random.Shared.Next(count)] : null;

        internal StockItem? ByName(string name) => Array.Find(items, item => item.Named(name));
        internal StockItem? ByItem(Item item) => Array.Find(items, entry => entry.Matches(item));
    }

    static readonly Lazy<TradeStockEntry[]> StockEntries = new(LoadStock);
    static readonly ConcurrentDictionary<(ItemCatalog Catalog, UniqueCatalog Uniques), Lazy<TradeStock>> StockByCatalog = new();

    static TradeStock Stock(ItemCatalog catalog, UniqueCatalog uniques) =>
        StockByCatalog.GetOrAdd((catalog, uniques), static key => new(() => ResolveStock(key.Catalog, key.Uniques))).Value;

    static TradeStockEntry[] LoadStock()
    {
        using var source = typeof(TraderBots).Assembly.GetManifestResourceStream("GlobalChat.TradeListings")
            ?? throw new InvalidDataException("Trader stock is missing.");
        return JsonSerializer.Deserialize(source, TradeListingJsonContext.Default.TradeStockEntryArray)
            ?? throw new InvalidDataException("Trader stock is empty.");
    }

    static TradeStock ResolveStock(ItemCatalog catalog, UniqueCatalog uniques)
    {
        var items = new List<StockItem>();
        foreach (var entry in StockEntries.Value)
        {
            if (entry.Amount == 0 || Currency(catalog, entry.Currency, entry.Amount) is not { } payment) continue;
            Item? buyback = entry.BuybackAmount is > 0 and var amount ? Currency(catalog, entry.Currency, amount) : null;
            if (entry.UniqueName is { Length: > 0 } name && entry.BaseName is null && uniques.Find(name) is { } unique)
            {
                // Explicit creation variants can have their own base type.
                string baseId = unique.Variants?.FirstOrDefault(v => v.Name == unique.CreationVariant)?.BaseTypeId ?? unique.BaseTypeId;
                var identity = unique with { BaseTypeId = baseId };
                if (catalog.Find(baseId) is { Supported: true } type && uniques.NameWord(identity) is uint word)
                    items.Add(new(entry, name, type, unique, word, payment, buyback));
            }
            else if (entry.UniqueName is null && entry.BaseName is { Length: > 0 } baseName
                && catalog.Find(baseName) is { } type && SupportedGear(type))
                items.Add(new(entry, baseName, type, null, null, payment, buyback));
        }
        return new([.. items]);
    }

    static string? MentionedStockUnique(string text)
    {
        foreach (var entry in StockEntries.Value)
        {
            if (entry.UniqueName is not { } name) continue;
            if (Mentions(text, name) || entry.Aliases?.Any(alias => Mentions(text, alias)) == true) return name;
        }
        return null;
    }

    static bool Mentions(string text, string name) => Regex.IsMatch(text, $@"(?<!\w){Regex.Escape(name)}(?!\w)", IgnoreCase);

    static Item RollBase(ItemRolls rolls, ItemCatalog.BaseType type, int level) =>
        rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = (byte)level },
            level < 12 ? ItemRarity.Magic : ItemRarity.Rare, Random.Shared);

    static Item WithPrice(Item item, Item payment) => item with { Price = [(payment.TypeHash, payment.StackSize)] };
}
