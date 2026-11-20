using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

/// <summary>Personal, rotating listings in simulated chat, with a native invitation and a fixed-price trade window.</summary>
sealed partial class TraderBots(ChatChannelRouting routing, TimeProvider? clock = null, Action<string>? log = null,
    bool enabled = true) : IDisposable
{
    sealed record Listing(uint Id, string Name, Item Item, string Price, DateTimeOffset Expires, bool RequiresAgreement,
        string? Quote = null, InventoryItem? FromPlayer = null);
    sealed record Request(ChatRoom Room, DateTimeOffset ReplyAt, string Target, bool Selling, string? LinkedItem);

    const byte Main = PlayerInventories.MainInventory;
    static readonly TimeSpan InviteDuration = TimeSpan.FromSeconds(25);
    static readonly TimeSpan InviteDelay = TimeSpan.FromMilliseconds(800);
    const byte TradeWindowFlags = InstanceNpcs.TradeActive | InstanceSocial.TradeRequestFlag;
    const byte TradeInventoryFits = 0x10;
    const ushort PlaceItemsPrompt = 840; // ClientStrings.TradeWindowPlaceItemsHere
    static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(45);
    static readonly string[] Thanks = ["ty", "tyty!", "thank you!"];
    const RegexOptions IgnoreCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string PostedPrice = @"\d+(?:[.,]\d+)?\s*(?:chaos(?:\s+orbs?)?|c|div(?:ine)?(?:\s+orbs?|s)?|d|ex(?:alted)?(?:\s+orbs?|alts?|s)?|mir(?:rors?(?:\s+of\s+kalandra)?)?)\b";
    static readonly Regex ClassifiedStart = new(@"^\s*(?<direction>wtb|wts|buying|selling)\s+(?<target>.+)$", IgnoreCase);
    static readonly Regex ClassifiedEnd = new(@"^\s*(?<target>.+?)\s+(?<direction>wtb|wts)$", IgnoreCase);
    static readonly Regex PricePrefix = new("^" + PostedPrice + @"[\s:-]+", IgnoreCase);
    static readonly Regex PriceSuffix = new(@"(?:\s|[-,])+(?:for\s+)?" + PostedPrice + "$", IgnoreCase);
    static readonly Regex AdSuffix = new(@"(?:[\s,-]+(?:fast|pm(?:\s+me)?|dm(?:\s+me)?|each|firm|obo))+$", IgnoreCase);
    static readonly Regex Buying = new(@"\b(?:wtb|buy|need|want|looking\s+for|give\s+me|sell\s+me|(?:anyone|some\s*one)\s+(?:have|selling))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Selling = new(@"^\s*(?:wts|selling|i\s+(?:have|am\s+selling))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Agreement = new(@"^\s*(?:ok(?:ay)?|yes|ye[ap]|sure|deal|inv(?:ite)?(?:\s+me)?)(?:[\s,!.]+(?:please|pls|ty|thanks|invite|inv))*[\s!.]*$", IgnoreCase);
    static readonly Regex Decline = new(@"^\s*(?:no(?:\s+thanks)?|pass|cancel|nvm|never\s*mind)[\s!.]*$", IgnoreCase);
    static readonly Regex ListingPurchase = new(@"(?:^\s*(?:buy|buying|wtb)(?:\s|[!.]|$)|\b(?:want|would like)\s+to\s+buy\b|\b(?:i'?ll|ill)\s+take\s+it\b)", IgnoreCase);
    static readonly Regex ListingQuestion = new(@"\b(?:price|how\s+much|available|still\s+(?:have|got))\b", IgnoreCase);

    sealed class Player(DateTimeOffset firstListing)
    {
        public readonly SemaphoreSlim Serial = new(1);
        public DateTimeOffset NextListing = firstListing;
        public DateTimeOffset InviteUntil;
        public DateTimeOffset InviteAt;
        public Listing? Listing;
        public ListingRoll? GearRoll;
        public TradeFeed? Trade;
        public Request? Pending;
        public uint InviteId;
        public uint OfferRevision;
        public bool WindowOpen;
        public uint MineRevision;
        public readonly Dictionary<string, (DateTimeOffset Until, string Message)> Recent = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly ConcurrentDictionary<ClientState, Player> players = new();
    readonly ConcurrentDictionary<string, byte> optedOut = new(StringComparer.OrdinalIgnoreCase);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly ListingWorker listingWorker = new(enabled, log);
    static int nextTrader;
    volatile bool stopped;

    internal const string CommandUsage = "/tradebots [off|on|status] (trader bots for your character)";

    bool EnabledFor(ClientState conn) => !stopped && enabled && !optedOut.ContainsKey(Program.CharacterName(conn));

    internal async Task Command(ClientState conn, string[] args)
    {
        if (conn.Commands is not { } commands) return;
        string? action = args.Length switch { 0 => "status", 1 => args[0].ToLowerInvariant(), _ => null };
        if (action is not ("off" or "on" or "status"))
        {
            await commands.Reply(CommandUsage);
            return;
        }
        if (!enabled)
        {
            await commands.Reply("Trader bots are disabled for this server in GlobalChat settings.");
            return;
        }
        if (conn.Ssf)
        {
            await commands.Reply("Trader bots are unavailable in Solo Self-Found.");
            return;
        }

        string character = Program.CharacterName(conn);
        if (action == "off")
        {
            optedOut.TryAdd(character, 0);
            await Leave(conn, "trader bots disabled");
        }
        else if (action == "on") optedOut.TryRemove(character, out _);

        await commands.Reply(EnabledFor(conn) ? "Trader bots are on for your character." : "Trader bots are off for your character.");
    }

    static bool Available(ClientState conn) => ChatRoom.IsListener(conn) && !conn.Ssf
        && conn.PlayerLife > 0 && !conn.HardcoreDeathRecorded && conn.Trade is null
        && conn.TradeRequestTo == 0 && conn.Sale is null && conn.Shop is null && conn.NpcRewardOffer is null;

    static bool OwnsTrade(ClientState conn, Player player) => player.InviteId != 0
        && conn.VirtualTradePartner == player.InviteId && Available(conn);

    static bool ReserveTrade(ClientState conn, Player player)
    {
        lock (conn.Monsters)
        {
            if (!Available(conn) || conn.VirtualTradePartner != 0 || Program.InGame.Keys.Any(other =>
                other.TradeRequestTo == conn.PeerId && !other.Closed && !other.LeavingArea
                && ReferenceEquals(other.Instance.Presence, conn.Instance.Presence))) return false;
            (player.InviteId, player.OfferRevision) = conn.NextVirtualTrade();
            conn.VirtualTradePartner = player.InviteId;
            return true;
        }
    }

    static void Remember(Player player, Listing offer, DateTimeOffset now, string message)
    {
        if (player.Recent.Count >= 24) player.Recent.Remove(player.Recent.MinBy(pair => pair.Value.Until).Key);
        player.Recent[offer.Name] = (now.AddMinutes(2), message);
    }

    ChatRoom? Room(ClientState conn, ChatChannelKind kind)
    {
        return ChatRoom.Of(conn, kind) is { } room && routing.Find(kind, room.Channel)?.Type == ChatChannelRouteType.Fake
            ? room : null;
    }

    ChatRoom? Room(ClientState conn) => Room(conn, ChatChannelKind.Trade) ?? Room(conn, ChatChannelKind.Global);

    Player PlayerFor(ClientState conn) =>
        players.GetOrAdd(conn, _ => new(time.GetUtcNow().AddSeconds(Random.Shared.Next(15, 31))));

    internal void OnPlayerChat(ClientState conn, ChatFrame line)
    {
        if (!EnabledFor(conn) || !Available(conn)
            || line is not { Type: ChatFrame.Line, Kind: { } kind, Channel: { } channel, Language: { } language, Text: { } text }
            || kind is not (ChatChannelKind.Global or ChatChannelKind.Trade)
            || ChatRoom.Of(conn, kind) is not { } room || room.Channel != channel || room.Language != language
            || kind == ChatChannelKind.Trade && routing.Find(kind, channel)?.Type != ChatChannelRouteType.Fake)
            return;
        var classified = ClassifiedStart.Match(text);
        if (!classified.Success) classified = ClassifiedEnd.Match(text.Trim().TrimEnd('.', '!', '?'));
        bool selling = false;
        string target;
        if (classified.Success)
        {
            selling = classified.Groups["direction"].Value.ToLowerInvariant() is "wts" or "selling";
            target = AdSuffix.Replace(classified.Groups["target"].Value.Trim().TrimEnd('.', '!', '?'), "").Trim();
            target = PricePrefix.Replace(PriceSuffix.Replace(target, ""), "").Trim();
        }
        else if (Buying.IsMatch(text) && !Selling.IsMatch(text) && MentionedStockUnique(text) is { } mentioned)
            target = mentioned;
        else return;
        string? linked = selling && target == "_" && line.Items is [{ Item: { } item }] ? item : null;
        if (target.Length == 0 || target == "_" && linked is null) return;
        var player = PlayerFor(conn);
        Interlocked.CompareExchange(ref player.Pending,
            new Request(room, time.GetUtcNow().AddSeconds(Random.Shared.Next(3, 7)), target, selling, linked), null);
    }

    internal async Task Tick(ClientState conn, double seconds)
    {
        if (!EnabledFor(conn)) return;
        if (!players.TryGetValue(conn, out var player))
        {
            if (!Available(conn) || Room(conn) is null) return;
            player = PlayerFor(conn);
        }
        if (!player.Serial.Wait(0)) return;
        try
        {
            if (!EnabledFor(conn)) return;
            var now = time.GetUtcNow();
            if (player.Listing is { } listing)
            {
                if (player.InviteUntil != default && (player.InviteUntil <= now || !OwnsTrade(conn, player)))
                {
                    bool timedOut = player.InviteUntil <= now;
                    bool wasOpen = player.WindowOpen;
                    await Close(conn, player, false);
                    if (timedOut) await Reply(conn, listing.Name, wasOpen ? "Trade timed out." : "Invite timed out.");
                }
                if (player.InviteAt != default && player.InviteAt <= now && OwnsTrade(conn, player))
                    await SendInvitation(conn, player, listing, now);
                if (listing.Expires <= now)
                {
                    player.Listing = null;
                    Remember(player, listing, now, "Sorry, that offer expired.");
                }
            }
            if (Volatile.Read(ref player.Pending) is { } request && request.ReplyAt <= now)
            {
                Interlocked.Exchange(ref player.Pending, null);
                if (player.InviteUntil == default && player.Listing is not { RequiresAgreement: true }
                    && Available(conn) && ChatRoom.Of(conn, request.Room.Kind) == request.Room
                    && Quote(conn, player, request, now) is { } quote)
                {
                    if (player.Listing is { } previous) Remember(player, previous, now, "Sorry, that offer is no longer available.");
                    player.Listing = quote;
                    await Send(conn, InstanceChat.ChatLine(quote.Quote!, "@" + quote.Name, items: quote.FromPlayer is { } offered
                        ? [new ChatLineItem((uint)quote.Quote!.IndexOf(ChatWire.ItemMark), offered.Item.Wire())] : null));
                }
            }
            var activeRoom = Room(conn, ChatChannelKind.Trade);
            if (activeRoom is null && (player.GearRoll is not null || now >= player.NextListing))
                activeRoom = Room(conn, ChatChannelKind.Global);
            if (player.GearRoll is { } pending && (pending.Room != activeRoom || pending.Level != ListingLevel(conn)
                || !ChatRoom.IsListener(conn) || conn.Ssf))
                CancelListing(player);
            if (activeRoom is { Kind: ChatChannelKind.Trade } trade)
            {
                var feed = player.Trade;
                if (feed is null || feed.Room != trade) player.Trade = feed = new(trade, now);
                await TickTradeFeed(conn, player, feed, now);
                return;
            }
            player.Trade = null;
            if (now < player.NextListing) return;
            if (player.InviteUntil != default || player.Listing is { RequiresAgreement: true }
                || !Available(conn) || activeRoom is not { } room)
            {
                CancelListing(player);
                return;
            }
            if (player.GearRoll is not { } roll)
            {
                if (!QueueListing(conn, player, room)) player.NextListing = now.AddSeconds(1);
                return;
            }
            if (!roll.Completed) return;
            player.NextListing = now.AddSeconds(Random.Shared.Next(120, 241));
            if (FinishListing(player, roll, now) is not { } offer) return;
            if (player.Listing is { } previousListing) Remember(player, previousListing, now, "Sorry, that offer is no longer available.");
            player.Listing = offer;
            string text = $"WTS {ChatWire.ItemMark} for {offer.Price}. Whisper me for a trade.";
            await Send(conn, InstanceChat.ChatLine(text, InstanceChat.Sender(room.TypedChannel, offer.Name),
                items: [new ChatLineItem((uint)text.IndexOf(ChatWire.ItemMark), offer.Item.Wire())]));
        }
        finally { player.Serial.Release(); }
    }

    static bool SupportedGear(ItemCatalog.BaseType type) => type.Supported && type.Quest is null && type.DropLevel > 0 && type.ModDomain == 1
        && type.Width > 0 && type.Height > 0 && type.Class is "Ring" or "Amulet" or "Belt" or "Boots" or "Gloves" or "Helmet";

    static Item? GearPayment(ItemCatalog catalog, Item item, ItemCatalog.BaseType gear, bool buyback)
    {
        // Budget gear trades in Chaos or Exalted Orbs. Reserve premium quotes for rolled endgame gear.
        string currency = CurrencyEffects.ChaosOrb;
        ushort amount = (ushort)Math.Max(1, (buyback ? 1 : 2) + gear.DropLevel / (buyback ? 24 : 12));
        if (item.Rarity == ItemRarity.Rare && gear.DropLevel >= 40)
        {
            var affixes = item.Mods.Where(mod => mod.Section == ModSection.Explicit).ToArray();
            int highRolls = affixes.Count(mod => catalog.Mods.ByHash(mod.Hash) is { Level: >= 60 });
            if (!item.Corrupted && !item.Mirrored && item.ItemLevel >= 80 && highRolls >= 4)
            {
                bool perfect = !buyback && affixes.Length == 6 && affixes.All(affix =>
                    catalog.Mods.ByHash(affix.Hash) is { Level: >= 75 } mod && mod.Stats.Count == affix.Values.Count
                    && mod.Stats.Select(stat => stat.Max).SequenceEqual(affix.Values));
                currency = perfect ? CurrencyEffects.MirrorOfKalandra : CurrencyEffects.DivineOrb;
                amount = (ushort)(buyback || perfect ? 1 : highRolls - 2);
            }
            else if (highRolls >= 2)
            {
                currency = CurrencyEffects.ExaltedOrb;
                amount = (ushort)(buyback ? 1 : 2);
            }
        }
        return Currency(catalog, currency, amount);
    }

    static Item? Currency(ItemCatalog catalog, string id, ushort amount) => catalog.Find(id) is { Supported: true } currency
        ? ItemCatalog.Create(currency) with { StackSize = amount } : null;

    static string PriceText(ItemCatalog catalog, Item payment) =>
        $"{payment.StackSize} {catalog.ByHash(payment.TypeHash)!.Name}{(payment.StackSize == 1 ? "" : "s")}";

    static Listing? Quote(ClientState conn, Player player, Request request, DateTimeOffset now)
    {
        var catalog = Game.ItemsFor(conn);
        var uniques = Game.UniquesFor(conn);
        var stock = Stock(catalog, uniques);
        var named = stock.ByName(request.Target);

        if (request.Selling)
        {
            var owned = Game.ItemsOf(conn).Items(Main)
                .Where(entry => entry.Item.StackSize == 1 && entry.Item.Sockets.All(s => s.Gem is null))
                .Select(entry => (Entry: entry, Type: catalog.ByHash(entry.Item.TypeHash),
                    Stock: request.LinkedItem is null ? named : stock.ByItem(entry.Item)))
                .Where(candidate => candidate.Entry.Item.Rarity == ItemRarity.Unique
                    ? candidate.Stock is { Buyback: not null } unique && unique.Matches(candidate.Entry.Item)
                    : candidate.Type is { } type && SupportedGear(type))
                .Where(candidate => request.LinkedItem is { } link
                    ? ChatItems.Write(candidate.Entry.Item, catalog) == link
                    : named is not null ? named.Matches(candidate.Entry.Item)
                    : request.Target.Equals(candidate.Type?.Name, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (owned is not [var candidate]) return null;
            var offered = candidate.Entry;
            var payment = candidate.Stock?.Buyback ?? GearPayment(catalog, offered.Item, candidate.Type!, buyback: true);
            if (payment is null) return null;
            string price = PriceText(catalog, payment);
            return CreateListing(player, payment, price, now, requiresAgreement: true,
                Random.Shared.Next(3) switch
                {
                    0 => $"I'll buy {ChatWire.ItemMark} for {price}, interested?",
                    1 => $"hey, {price} for {ChatWire.ItemMark}?",
                    _ => $"can do {price} for {ChatWire.ItemMark}",
                }, offered);
        }

        int level = ListingLevel(conn);
        var rolls = Game.RollsFor(conn);
        if (named is not null)
        {
            if (named.MinimumLevel > level || named.Create(catalog, rolls, uniques, level) is not { } goods) return null;
            string quote = catalog.ByHash(named.Price.TypeHash)?.Id == CurrencyEffects.ChaosOrb
                ? $"{named.Price.StackSize}c" : goods.Price;
            return CreateListing(player, goods.Item, goods.Price, now, requiresAgreement: true, $"I have, {quote}.");
        }
        if (catalog.All.FirstOrDefault(t => SupportedGear(t)
            && t.DropLevel <= level && t.Name.Equals(request.Target, StringComparison.OrdinalIgnoreCase)) is not { } gear)
            return null;
        var equipment = RollBase(rolls, gear, level);
        if (GearPayment(catalog, equipment, gear, buyback: false) is not { } cost) return null;
        string terms = PriceText(catalog, cost);
        return CreateListing(player, WithPrice(equipment, cost), terms, now, requiresAgreement: true,
            Random.Shared.Next(3) switch
            {
                0 => $"hey, got one. {terms}",
                1 => $"yep, {gear.Name} for {terms}",
                _ => $"{terms} if you still need it",
            });
    }

    static Listing? CreateListing(Player player, Item item, string price, DateTimeOffset now, bool requiresAgreement,
        string? quote = null, InventoryItem? fromPlayer = null)
    {
        if (NextTraderName(player, now) is not { } name) return null;
        uint serial = unchecked((uint)Interlocked.Increment(ref nextTrader));
        return new(serial, name, item, price, now.AddSeconds(100), requiresAgreement, quote, fromPlayer);
    }

    internal async Task<bool> Whisper(ClientState conn, string recipient, string text)
    {
        if (!EnabledFor(conn) || !players.TryGetValue(conn, out var player)) return false;
        await player.Serial.WaitAsync();
        try
        {
            if (!EnabledFor(conn)) return false;
            var now = time.GetUtcNow();
            var offer = player.Listing;
            if (offer is null || !recipient.Equals(offer.Name, StringComparison.OrdinalIgnoreCase))
            {
                var advert = player.Trade is { } feed && Room(conn, ChatChannelKind.Trade) == feed.Room
                    ? feed.Ads.FirstOrDefault(ad => ad.Name.Equals(recipient, StringComparison.OrdinalIgnoreCase)) : null;
                if (advert is { Offer: null }) return true;
                offer = advert?.Offer;
                if (offer is null)
                {
                    if (!player.Recent.TryGetValue(recipient, out var recent) || recent.Until <= now) return false;
                    await Reply(conn, recipient, recent.Message);
                    return true;
                }
            }
            await Send(conn, InstanceChat.ChatLine(text, "@" + offer.Name, InstanceChat.WhisperSentFlag));
            if (player.InviteUntil != default && player.Listing is { } active)
            {
                if (player.InviteUntil <= now || !OwnsTrade(conn, player)) await Close(conn, player, false);
                else
                {
                    if (active.Id == offer.Id && Decline.IsMatch(text)) await Close(conn, player, false);
                    else if (Agreement.IsMatch(text) || ListingPurchase.IsMatch(text))
                        await Reply(conn, offer.Name, active.Id != offer.Id ? "Finish your current trade first."
                            : player.WindowOpen ? "The trade window is open."
                            : player.InviteAt != default ? "One sec, sending an invite." : "Invite already sent.");
                    return true;
                }
            }
            if (offer.Expires <= now)
            {
                await Reply(conn, offer.Name, "Sorry, that offer expired.");
                return true;
            }
            if (!Agreement.IsMatch(text) && (offer.RequiresAgreement || !ListingPurchase.IsMatch(text)))
            {
                if (ListingQuestion.IsMatch(text)) await Reply(conn, offer.Name, $"Still available, {offer.Price}.");
                return true;
            }
            if (offer.FromPlayer is { } offered && !StillOwns(Game.ItemsOf(conn), offered))
            {
                RetireChangedOffer(player, offer, now);
                await Reply(conn, offer.Name, ChangedOffer);
                return true;
            }
            if (!ReserveTrade(conn, player))
            {
                await Reply(conn, offer.Name, "Finish or decline your other trade first, then whisper me again.");
                return true;
            }

            if (player.Listing is { } replaced && replaced.Id != offer.Id)
                Remember(player, replaced, now, "Sorry, that offer is no longer available.");
            player.InviteAt = now + InviteDelay;
            player.InviteUntil = player.InviteAt + InviteDuration;
            if (offer.Expires < player.InviteUntil) offer = offer with { Expires = player.InviteUntil };
            player.Listing = offer;
            player.Trade?.Ads.RemoveAll(ad => ad.Offer?.Id == offer.Id);
            return true;
        }
        finally { player.Serial.Release(); }
    }

    static async Task SendInvitation(ClientState conn, Player player, Listing offer, DateTimeOffset now)
    {
        player.InviteAt = default;
        player.InviteUntil = now + InviteDuration;
        string terms = offer.FromPlayer is null ? $"Place {offer.Price} in your offer." : "Place the quoted item in your offer.";
        try
        {
            await Reply(conn, offer.Name, $"Invite sent. {terms}");
            await Send(conn, InstanceSocial.KnownPlayer(offer.Name, offer.Name, conn.Layout.Hash, 0));
            foreach (uint inventory in new[] { Game.PlayerTradeMine, Game.PlayerTradeTheirs })
                await Send(conn, InstanceInventories.AddInventory(inventory, NpcShops.Offer, NpcShops.Manager));
            await Send(conn, InstanceNpcs.TradeOpen(player.InviteId, Game.PlayerTradeMine, Game.PlayerTradeTheirs,
                InstanceSocial.TradeRequestFlag, offer.Name, -1));
        }
        catch
        {
            await Close(conn, player, false);
            throw;
        }
    }

    internal async Task<bool> TradeAction(ClientState conn, ushort opcode, byte[] bytes)
    {
        if (!EnabledFor(conn) || !players.TryGetValue(conn, out var player)) return false;
        await player.Serial.WaitAsync();
        try
        {
            if (!EnabledFor(conn) || player.InviteUntil == default || player.Listing is not { } offer) return false;
            if (opcode == InstanceSocial.TradeRequestAcceptId)
            {
                if (InstanceSocial.ReadTradeRequestAccept(bytes) != player.InviteId) return false;
                if (player.InviteAt != default) return true;
                var now = time.GetUtcNow();
                if (!OwnsTrade(conn, player) || player.InviteUntil <= now)
                {
                    await Close(conn, player, false);
                    await Reply(conn, offer.Name, "That invite is no longer valid. Whisper me again if you still want to trade.");
                    return true;
                }
                if (player.WindowOpen) return true;
                player.InviteUntil = now + WindowDuration;
                if (offer.Expires < player.InviteUntil) player.Listing = offer = offer with { Expires = player.InviteUntil };
                player.WindowOpen = true;
                await Send(conn, TradeWindowState(player, offer, matches: false, fits: false));
                await Send(conn, InstanceItems.InventoryChange(Game.PlayerTradeTheirs, player.OfferRevision, [],
                    [new InventoryItem(Game.ItemsOf(conn).NewItemId(), 0, 0, offer.Item with { Price = [] })], NpcShops.Manager));
                return true;
            }
            if (opcode == InstanceNpcs.TradeAnswerId)
            {
                if (InstanceNpcs.ReadTradeAnswer(bytes).Other != player.InviteId) return false;
                await Close(conn, player, false);
                return true;
            }
            if (!player.WindowOpen) return false;
            if (!OwnsTrade(conn, player) || player.InviteUntil <= time.GetUtcNow())
            {
                await Close(conn, player, false);
                await Reply(conn, offer.Name, "That trade is no longer open.");
                return true;
            }
            if (opcode != InstanceNpcs.TradeAcceptId)
            {
                await EditOffer(conn, player, offer, opcode, bytes);
                return true;
            }
            if (InstanceNpcs.ReadTradeAccept(bytes) != player.OfferRevision) return true;

            var items = Game.ItemsOf(conn);
            var offered = items.OfferedInPlace();
            PlayerInventories.Stacking? settlement;
            try
            {
                lock (conn.Monsters)
                    settlement = EnabledFor(conn) && OwnsTrade(conn, player) && MatchesOffer(offer, items.Offered())
                        ? Saves.Atomically(() => items.Sell(offer.Item with { Price = [] }, Game.ShapeOf)) : null;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                log?.Invoke($"Trader settlement failed: {error.GetType().Name}: {error.Message}");
                await Close(conn, player, false, (items, offered));
                await Reply(conn, offer.Name, "The trade couldn't be saved. No exchange was completed. You can try again.");
                return true;
            }
            if (settlement is null)
            {
                string reason = MatchesOffer(offer, items.Offered()) ? TradeSpaceHint(offer.Item)
                    : offer.FromPlayer is null ? $"Offer exactly {offer.Price} and remove any extra items."
                    : "Offer the exact item I quoted and remove any extra items.";
                await Reply(conn, offer.Name, reason);
                return true;
            }
            player.Listing = null;
            Remember(player, offer, time.GetUtcNow(), "That trade is already complete.");
            try
            {
                await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(Main), items.NextRevision(Main),
                    settlement.RemovedIds, settlement.AddedItems));
            }
            finally { await Close(conn, player, true); }
            await Reply(conn, offer.Name, Thanks[Random.Shared.Next(Thanks.Length)]);
            return true;
        }
        finally { player.Serial.Release(); }
    }

    static bool MatchesOffer(Listing listing, IReadOnlyList<InventoryItem> offered)
    {
        if (listing.FromPlayer is { } expected)
            return offered is [var entry] && SameItem(entry, expected);
        return listing.Item.Price is [var price] && offered.Count > 0
            && offered.All(entry => entry.Item.TypeHash == price.TypeHash && entry.Item.StackSize > 0)
            && offered.Sum(entry => (long)entry.Item.StackSize) == price.Amount;
    }

    static string TradeSpaceHint(Item reward) => Game.ShapeOf(reward) is { } size && (size.Width > 1 || size.Height > 1)
        ? $"The item needs a clear {size.Width}×{size.Height} space in your main inventory after payment."
        : "Make room in your main inventory for the item you will receive.";

    static async Task EditOffer(ClientState conn, Player player, Listing listing, ushort opcode, byte[] bytes)
    {
        if (opcode is not (InstanceNpcs.TradeOfferId or InstanceNpcs.TradeTakeBackId or InstanceNpcs.TradeMoveAllId)) return;

        uint inventory, id;
        (byte X, byte Y)? requested = null;
        if (opcode == InstanceNpcs.TradeOfferId)
        {
            var move = InstanceNpcs.ReadTradeOffer(bytes);
            (inventory, id) = (move.InventoryId, move.ItemId);
            if (move.X != 0xff) requested = (move.X, move.Y);
        }
        else (id, inventory) = InstanceNpcs.ReadTradeMove(bytes);

        var items = Game.ItemsOf(conn);
        byte? source = PlayerInventories.Row(inventory);
        byte row;
        InventoryItem item;
        bool takingBack;
        (InventoryItem Stack, InventoryItem Held)? split = null;
        string? hint = null;
        lock (conn.Monsters)
        {
            if (!OwnsTrade(conn, player)) return;
            if (opcode != InstanceNpcs.TradeOfferId && inventory is 0 or Game.PlayerTradeMine)
            {
                if (items.TakeBack(id) is not { } back) return;
                (row, item) = back;
                takingBack = true;
            }
            else
            {
                if (source is not byte from || items.Items(from).FirstOrDefault(entry => entry.Id == id) is not { } original
                    || Game.ShapeOf(original.Item) is not { } shape) return;
                var offered = items.Offered();
                long remaining = listing.FromPlayer is null && listing.Item.Price is [var price]
                    && original.Item.TypeHash == price.TypeHash
                    ? price.Amount - offered.Where(entry => entry.Item.TypeHash == price.TypeHash).Sum(entry => (long)entry.Item.StackSize) : 0;
                row = from;
                item = original;
                if (requested is null && from == Main && remaining > 0 && remaining < original.Item.StackSize)
                {
                    // Quick-add packets have no target cell. Split the unpaid balance onto the cursor.
                    split = items.Split(from, id, (int)remaining, out _);
                    if (split is null) hint = "Put down the item on your cursor before splitting the payment.";
                }
                else if (OfferCell(offered, shape, requested) is not { } cell)
                    hint = "That item does not fit in the selected trade space.";
                else if (items.Offer(from, id, cell.X, cell.Y, Game.ShapeOf) is { } placed)
                    (row, item) = (placed.Row, placed.Item with { X = cell.X, Y = cell.Y });
                else if (from == PlayerInventories.Cursor)
                    hint = "Leave an empty space in your main inventory to hold this item while the trade is open.";
                else return;
                takingBack = false;
            }
            // A confirmation of the previous contents must not apply to a changed offer.
            if (split is null && hint is null) player.OfferRevision = conn.NextVirtualTrade().Revision;
        }

        if (hint is not null)
        {
            await Reply(conn, listing.Name, hint);
            return;
        }
        if (split is { } change)
        {
            await Send(conn, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [id], [change.Stack]));
            const byte cursor = PlayerInventories.Cursor;
            await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(cursor), items.NextRevision(cursor), [], [change.Held]));
            return;
        }

        uint main = PlayerInventories.InventoryId(row);
        if (takingBack)
            await Send(conn, InstanceItems.InventoryChange(main, items.NextRevision(row), [], [item]));
        else
        {
            byte from = source!.Value;
            await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from), [id], []));
        }
        await Send(conn, InstanceItems.InventoryChange(Game.PlayerTradeMine, ++player.MineRevision,
            takingBack ? [id] : [], takingBack ? [] : [item], NpcShops.Manager));
        await Send(conn, InstanceItems.InventoryChange(Game.PlayerTradeTheirs, player.OfferRevision, [], [], NpcShops.Manager));
        bool matches = MatchesOffer(listing, items.Offered());
        bool ready = matches && items.CanExchange([listing.Item with { Price = [] }], Game.ShapeOf);
        await Send(conn, TradeWindowState(player, listing, matches, ready));
        if (matches && !ready) await Reply(conn, listing.Name, TradeSpaceHint(listing.Item));
    }

    static byte[] TradeWindowState(Player player, Listing listing, bool matches, bool fits)
    {
        byte flags = fits ? (byte)(TradeWindowFlags | TradeInventoryFits | Game.TradeTheirsAccepted) : TradeWindowFlags;
        var packet = InstanceNpcs.TradeOpen(player.InviteId, Game.PlayerTradeMine, Game.PlayerTradeTheirs, flags, listing.Name, -1);
        // An explicit prompt suppresses the client's default inventory warning while the offer is being assembled.
        if (!matches) System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(packet.Length - 4), PlaceItemsPrompt);
        return packet;
    }

    static (byte X, byte Y)? OfferCell(IReadOnlyList<InventoryItem> offered, PlayerInventories.Shape shape,
        (byte X, byte Y)? requested)
    {
        var occupied = offered.Select(item => (Item: item, Shape: Game.ShapeOf(item.Item))).ToArray();
        bool Fits(byte x, byte y) => x + shape.Width <= NpcShops.Offer.Width && y + shape.Height <= NpcShops.Offer.Height
            && !occupied.Any(other => other.Shape is { } size && x < other.Item.X + size.Width && other.Item.X < x + shape.Width
                && y < other.Item.Y + size.Height && other.Item.Y < y + shape.Height);
        if (requested is { } cell) return Fits(cell.X, cell.Y) ? cell : null;
        for (byte x = 0; x + shape.Width <= NpcShops.Offer.Width; x++)
            for (byte y = 0; y + shape.Height <= NpcShops.Offer.Height; y++)
                if (Fits(x, y)) return (x, y);
        return null;
    }

    const string ChangedOffer = "The quoted item is missing or changed. Post a new WTS with the item linked to get another quote.";

    static bool StillOwns(PlayerInventories items, InventoryItem offered) =>
        items.Items(Main).Any(i => SameItem(i, offered)) || items.Items(PlayerInventories.Cursor).Any(i => SameItem(i, offered));

    static bool SameItem(InventoryItem entry, InventoryItem expected) => entry.Id == expected.Id
        && (entry.Item == expected.Item || entry.Item.Wire().AsSpan().SequenceEqual(expected.Item.Wire()));

    static void RetireChangedOffer(Player player, Listing offer, DateTimeOffset now)
    {
        if (player.Listing?.Id == offer.Id) player.Listing = null;
        Remember(player, offer, now, ChangedOffer);
    }

    static async Task Close(ClientState conn, Player player, bool bought,
        (PlayerInventories Inventory, IReadOnlyList<(byte Row, InventoryItem Item)> Offered)? recovery = null)
    {
        uint invite = player.InviteId;
        player.InviteId = 0;
        player.InviteAt = default;
        player.InviteUntil = default;
        bool wasOpen = player.WindowOpen;
        player.WindowOpen = false;
        player.MineRevision = 0;
        player.OfferRevision = 0;
        try
        {
            if (wasOpen && !bought)
            {
                var items = Game.ItemsOf(conn);
                if (recovery is { } restored) items.InheritRevisions(restored.Inventory);
                var returned = items.CancelOffers();
                foreach (var row in (returned.Count > 0 ? returned : recovery?.Offered ?? []).GroupBy(item => item.Row))
                    await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                        [], [.. row.Select(item => item.Item)]));
            }
            await Send(conn, InstanceNpcs.TradeClose(invite, bought));
        }
        finally
        {
            lock (conn.Monsters)
                if (conn.VirtualTradePartner == invite) conn.VirtualTradePartner = 0;
        }
    }

    static Task Reply(ClientState conn, string name, string text) => Send(conn, InstanceChat.ChatLine(text, "@" + name));

    static async Task Send(ClientState conn, byte[] packet)
    {
        if (conn is { Closed: false, LeavingArea: false, Outbox: { } send })
            try { await send(packet, "trader bot"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
    }

    internal Task Leave(ClientState conn, string reason)
    {
        return players.TryRemove(conn, out var player) ? CancelOnUnload(conn, player) : Task.CompletedTask;
    }

    public void Dispose()
    {
        stopped = true;
        listingWorker.Dispose();
        foreach (var (conn, player) in players)
            _ = CancelOnUnload(conn, player);
        players.Clear();
    }

    static async Task CancelOnUnload(ClientState conn, Player player)
    {
        await player.Serial.WaitAsync();
        try
        {
            CancelListing(player);
            if (player.InviteId != 0) await Close(conn, player, false);
        }
        finally { player.Serial.Release(); }
    }
}
