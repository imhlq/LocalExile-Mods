namespace GlobalChat;

sealed partial class TraderBots
{
    static readonly string[] TraderFirstNames =
    [
        "Aven", "Briar", "Cael", "Dagna", "Edda", "Fen", "Garran", "Hale", "Iona", "Joren", "Kestrel", "Liora",
        "Maren", "Neri", "Orin", "Perrin", "Quill", "Runa", "Soren", "Tallis", "Una", "Vessa", "Wren", "Yarrow",
    ];

    static readonly string[] TraderSurnames =
    [
        "Aldermere", "Ashford", "Blackfen", "Bramble", "Caskwell", "Dunlow", "Emberton", "Fallow", "Flint", "Greylock",
        "Harth", "Kettle", "Larkspur", "Lowmarsh", "Moor", "Northwick", "Reed", "Rook", "Saltmead", "Slate",
        "Stonevein", "Thorne", "Vale", "Whitlock",
    ];

    static string? NextTraderName(Player player, DateTimeOffset now) => PickTraderName(Random.Shared, name =>
        string.Equals(player.Listing?.Name, name, StringComparison.OrdinalIgnoreCase)
        || player.Recent.TryGetValue(name, out var previous) && previous.Until > now
        || player.Trade?.Ads.Any(ad => ad.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true
        || Program.InGame.Keys.Any(conn => Program.CharacterName(conn).Equals(name, StringComparison.OrdinalIgnoreCase)));

    internal static string? PickTraderName(Random random, Func<string, bool> unavailable)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string first = TraderFirstNames[random.Next(TraderFirstNames.Length)];
            string last = TraderSurnames[random.Next(TraderSurnames.Length)];
            string name = random.Next(3) == 0 ? first + last : first + "_" + last;
            if (!unavailable(name)) return name;
        }
        return null;
    }
}
