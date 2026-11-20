using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

sealed partial class TraderBots
{
    sealed record Advertisement(string Name, string Text, Listing? Offer, DateTimeOffset RepeatAt,
        DateTimeOffset Expires, int RepeatsLeft);

    sealed class TradeFeed(ChatRoom room, DateTimeOffset now)
    {
        public ChatRoom Room { get; } = room;
        public DateTimeOffset Next = now.AddSeconds(Random.Shared.Next(4, 13));
        public readonly List<Advertisement> Ads = [];
    }

    static readonly string[] Realms = ["SG", "EU", "JP", "KR"];
    static readonly string[] Bosses = ["Uber Elder", "Maven", "Exarch", "Eater"];

    // Rounded sampling bins from the November Trade log, with zero-gap duplicates excluded.
    internal static int TradeGapSeconds(Random rng) => rng.Next(203) switch
    {
        < 43 => rng.Next(2, 6),
        < 96 => rng.Next(6, 15),
        < 143 => rng.Next(15, 30),
        < 178 => rng.Next(30, 60),
        _ => rng.Next(60, 121),
    };

    static int RepeatDelaySeconds(Random rng) => rng.Next(100) switch
    {
        < 57 => rng.Next(18, 60),
        < 91 => rng.Next(60, 181),
        _ => rng.Next(181, 301),
    };

    async Task TickTradeFeed(ClientState conn, Player player, TradeFeed feed, DateTimeOffset now)
    {
        if (now < feed.Next || !ChatRoom.IsListener(conn) || conn.Ssf) return;
        var roll = player.GearRoll;
        if (roll is { Completed: false }) return;
        var rng = Random.Shared;
        feed.Ads.RemoveAll(ad =>
        {
            if (ad.Expires > now) return false;
            if (ad.Offer is { } expired) Remember(player, expired, now, "Sorry, that offer expired.");
            return true;
        });
        var due = roll is null
            ? feed.Ads.Where(ad => ad.RepeatsLeft > 0 && ad.RepeatAt <= now).MinBy(ad => ad.RepeatAt)
            : null;
        if (due is null && roll is null && rng.Next(100) >= 78 && QueueListing(conn, player, feed.Room)) return;
        feed.Next = now.AddSeconds(TradeGapSeconds(rng));
        Advertisement advert;
        if (due is not null)
        {
            advert = due with { RepeatsLeft = due.RepeatsLeft - 1, RepeatAt = now.AddSeconds(RepeatDelaySeconds(rng)) };
            feed.Ads[feed.Ads.IndexOf(due)] = advert;
        }
        else
        {
            var offer = roll is not null ? FinishListing(player, roll, now) : null;
            // Average of 1.5 reposts per new ad before expiry, corresponding to a 60% repeat baseline.
            int repeats = rng.Next(10) switch { < 2 => 0, < 5 => 1, < 8 => 2, _ => 3 };
            var repeatAt = now.AddSeconds(RepeatDelaySeconds(rng));
            if (offer is not null)
            {
                offer = offer with { Expires = now.AddMinutes(7) };
                string text = rng.Next(4) switch
                {
                    0 => $"WTS {ChatWire.ItemMark} {offer.Price}",
                    1 => $"{ChatWire.ItemMark} {offer.Price} wts",
                    2 => $"wts {offer.Price} {ChatWire.ItemMark}",
                    _ => $"WTS {ChatWire.ItemMark} - {offer.Price}, pm",
                };
                advert = new(offer.Name, text, offer, repeatAt, offer.Expires, repeats);
            }
            else
            {
                if (NextTraderName(player, now) is not { } name) return;
                advert = new(name, ServiceText(rng), null, repeatAt, now.AddMinutes(12), repeats);
            }
            feed.Ads.Add(advert);
        }

        await Send(conn, InstanceChat.ChatLine(advert.Text, InstanceChat.Sender(feed.Room.TypedChannel, advert.Name),
            items: advert.Offer is { } listing
                ? [new ChatLineItem((uint)advert.Text.IndexOf(ChatWire.ItemMark), listing.Item.Wire())] : null));
    }

    internal static string ServiceText(Random rng)
    {
        string boss = Bosses[rng.Next(Bosses.Length)];
        string realm = Realms[rng.Next(Realms.Length)];
        int price = rng.Next(4, 11) * 5;
        int spots = rng.Next(1, 4);
        return rng.Next(28) switch
        {
            < 8 => $"WTS {boss} voidstone - {price}c, pay after",
            < 13 => $"wts {boss} completion {price}c {spots}/4",
            < 17 => $"WTS {boss} carry {price}c | {(spots == 1 ? "last spot" : $"{spots} spots open")}",
            < 20 => $"HOSTING 5way AFK | {realm} | {rng.Next(1, 4)} div/run | {spots} spots left",
            < 23 => $"Hosting 5-way: {realm}, {rng.Next(7, 11)}k kills, {rng.Next(4, 9)}d / 5 runs",
            < 25 => $"WTS T17 boss service {price}c | your map, your loot",
            < 27 => $"WTS challenge carry {price}c each, pm",
            _ => $"WTB {boss} voidstone, can join now",
        };
    }
}
