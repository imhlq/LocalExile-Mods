using System.Threading.Channels;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

sealed partial class TraderBots
{
    sealed record GeneratedGear(Item Item, string Price);

    // The worker takes catalog data and a level. The player tick owns each request and the resulting listing.
    sealed class ListingRoll(ChatRoom room, ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques, int level)
    {
        public readonly ChatRoom Room = room;
        public readonly ItemCatalog Catalog = catalog;
        public readonly ItemRolls Rolls = rolls;
        public readonly UniqueCatalog Uniques = uniques;
        public readonly int Level = level;
        public GeneratedGear? Result;
        public volatile bool Completed;
        public volatile bool Cancelled;
    }

    sealed class ListingWorker : IDisposable
    {
        readonly Channel<ListingRoll> queue = Channel.CreateBounded<ListingRoll>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        readonly Task? worker;
        volatile bool stopping;

        public ListingWorker(bool enabled, Action<string>? log)
        {
            if (enabled) worker = Task.Run(() => Run(log));
            else queue.Writer.Complete();
        }

        public bool TryQueue(ListingRoll roll) => queue.Writer.TryWrite(roll);

        async Task Run(Action<string>? log)
        {
            await foreach (var request in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (stopping || request.Cancelled) continue;
                    request.Result = Stock(request.Catalog, request.Uniques).PickAdvert(request.Level)
                        ?.Create(request.Catalog, request.Rolls, request.Uniques, request.Level);
                }
                catch (Exception ex) { log?.Invoke($"trader listing generation failed: {ex.Message}"); }
                finally { request.Completed = true; }
            }
        }

        public void Dispose()
        {
            stopping = true;
            queue.Writer.TryComplete();
            worker?.GetAwaiter().GetResult();
        }
    }

    const int MaxListingLevel = 86;

    static int ListingLevel(ClientState conn) => Math.Clamp(conn.Pools.Level, 1, MaxListingLevel);

    bool QueueListing(ClientState conn, Player player, ChatRoom room)
    {
        var roll = new ListingRoll(room, Game.ItemsFor(conn), Game.RollsFor(conn), Game.UniquesFor(conn), ListingLevel(conn));
        if (!listingWorker.TryQueue(roll)) return false;
        player.GearRoll = roll;
        return true;
    }

    static void CancelListing(Player player)
    {
        if (player.GearRoll is { } pending) pending.Cancelled = true;
        player.GearRoll = null;
    }

    static Listing? FinishListing(Player player, ListingRoll roll, DateTimeOffset now)
    {
        player.GearRoll = null;
        return roll.Result is { } gear ? CreateListing(player, gear.Item, gear.Price, now, requiresAgreement: false) : null;
    }
}
