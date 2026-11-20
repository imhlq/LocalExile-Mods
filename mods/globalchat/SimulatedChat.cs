using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Chat;
using GameServer.Core.Protocol.Instance;

namespace GlobalChat;

/// <summary>Fake chat simulation for configured rooms with listeners.</summary>
sealed class SimulatedChat(ChatChannelRouting routing, Action<string> log, bool simulateTrade = true) : IDisposable
{
    sealed class FakeChatRoom(DateTime now)
    {
        internal readonly List<(DateTime DueUtc, string Speaker, string Group, string Text)> Pending = [];
        internal DateTime NextChat = now.AddSeconds(3);
        internal long LastSeenTick;
    }

    readonly Dictionary<ChatRoom, FakeChatRoom> _fakeRooms = [];
    long _chatTick;
    readonly Queue<string> _recentChatMessages = new();
    readonly Queue<string> _recentChatSpeakers = new();
    Dictionary<string, GlobalChatGroup> _chatGroupById = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<(string Group, string Text)>> _chatKeywordIndex = new(StringComparer.OrdinalIgnoreCase);
    AmbientChatModel? _ambientChat;
    readonly Lock gate = new();
    readonly Random _random = new();
    readonly CancellationTokenSource stop = new();
    Task? running;
    bool _chatLoadAttempted;

    static readonly HashSet<string> ChatStopWords = new(
    [
        "what", "where", "when", "which", "that", "this", "with", "from", "have", "does", "your", "they",
        "them", "then", "than", "there", "their", "would", "could", "should", "about", "into", "just",
        "like", "some", "more", "only", "very", "much", "many", "also", "even", "still", "here", "good",
        "best", "make", "made", "need", "want", "know", "think", "league", "anyone", "someone", "how"
    ], StringComparer.OrdinalIgnoreCase);

    internal void Start() => running = Task.Run(Run);

    async Task Run()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                lock (gate) TickGlobalChat();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        running?.GetAwaiter().GetResult();
        lock (gate) UnloadGlobalChat();
        stop.Dispose();
    }

    bool EnsureGlobalChatLoaded()
    {
        if (_chatLoadAttempted) return _ambientChat is not null;
        _chatLoadAttempted = true;
        GlobalChatLibrary library;
        try { (library, _ambientChat) = AmbientChatModel.Load(); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or FormatException)
        {
            log($"failed to load simulated chat: {error.Message}");
            return false;
        }
        var groups = library.Groups;
        _chatGroupById = groups.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            if (g.Id is "questions_help" or "item_showcase" or "lfg_sharing") continue;
            foreach (string msg in g.Messages)
            {
                if (msg.EndsWith('?')) continue;
                string lower = msg.ToLowerInvariant();
                foreach (var match in Regex.EnumerateMatches(lower, "[a-z0-9]{4,}"))
                {
                    string w = lower.Substring(match.Index, match.Length);
                    if (ChatStopWords.Contains(w)) continue;
                    if (!_chatKeywordIndex.TryGetValue(w, out var list)) _chatKeywordIndex[w] = list = [];
                    list.Add((g.Id, msg));
                }
            }
        }

        log($"loaded simulated chat ({groups.Length} groups, {groups.Sum(g => g.Messages.Length)} messages, {library.Bursts.Length} conversation templates)");
        return true;
    }

    void UnloadGlobalChat()
    {
        _fakeRooms.Clear();
        if (!_chatLoadAttempted) return;
        _chatLoadAttempted = false;
        _recentChatMessages.Clear();
        _recentChatSpeakers.Clear();
        _ambientChat = null;
        _chatGroupById = new(StringComparer.OrdinalIgnoreCase);
        _chatKeywordIndex = new(StringComparer.OrdinalIgnoreCase);
    }

    internal void OnPlayerChat(ClientState conn, ChatFrame line)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !IsChatListener(conn)
                || line is not { Kind: { } kind, Channel: { } channel, Language: { } language, Text: { Length: > 0 } text }
                || FakeChatRoomOf(conn, kind) != new ChatRoom(kind, channel, language)
                || !EnsureGlobalChatLoaded()) return;
            var now = DateTime.UtcNow;
            EnqueueRepliesToPlayerLocked(Room(new(kind, channel, language), now), now, Program.CharacterName(conn), text);
        }
    }

    void EnqueueRepliesToPlayerLocked(FakeChatRoom room, DateTime now, string playerName, string userMessage)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { playerName };
        string lower = userMessage.ToLowerInvariant();

        if (Regex.IsMatch(lower, @"^(hi|hey|hello|yo|sup|morning|gm|o/|glhf)\b"))
        {
            if (_chatGroupById.TryGetValue("greetings_parting", out var fg) && PickGroupMessage(fg) is { } msg)
                room.Pending.Add((now.AddSeconds(1.5 + _random.NextDouble() * 2.5), PickChatSpeaker(used), fg.Id, msg));
            return;
        }
        if (Regex.IsMatch(lower, @"\b(dropped|got a|first|found a)\b.*\b(mirror|mageblood|mb|headhunter|hh|divine|div|apothecary)\b") && !lower.EndsWith('?'))
        {
            int count = 2 + _random.Next(2);
            double delay = 1.5 + _random.NextDouble() * 2;
            for (int i = 0; i < count; i++)
            {
                string grpId = i == count - 1 && _random.NextDouble() < 0.4 ? "vaal_gambling" : "reactions_gratz";
                if (_chatGroupById.TryGetValue(grpId, out var fg) && PickGroupMessage(fg) is { } msg)
                {
                    room.Pending.Add((now.AddSeconds(delay), PickChatSpeaker(used), fg.Id, msg));
                    delay += 1.5 + _random.NextDouble() * 2.5;
                }
            }
            return;
        }
        if (Regex.IsMatch(lower, @"\b(bricked|poofed|ripped|died|lost my)\b"))
        {
            string grpId = _random.NextDouble() < 0.65 ? "reactions_sympathy" : "reactions_humor";
            if (_chatGroupById.TryGetValue(grpId, out var fg) && PickGroupMessage(fg) is { } msg)
                room.Pending.Add((now.AddSeconds(1.5 + _random.NextDouble() * 2.5), PickChatSpeaker(used), fg.Id, msg));
            return;
        }

        var words = Regex.Matches(lower, "[a-z0-9]{4,}")
            .Select(m => m.Value)
            .Where(w => !ChatStopWords.Contains(w) && _chatKeywordIndex.ContainsKey(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => _chatKeywordIndex[w].Count)
            .ToArray();

        if (words.Length > 0)
        {
            var matches = new List<(string Group, string Text)>();
            foreach (string w in words)
                matches.AddRange(_chatKeywordIndex[w].Where(m => !string.Equals(m.Text, userMessage, StringComparison.OrdinalIgnoreCase)));
            if (matches.Count > 0)
            {
                var pick = matches[_random.Next(Math.Min(matches.Count, 15))];
                room.Pending.Add((now.AddSeconds(2 + _random.NextDouble() * 3.5), PickChatSpeaker(used), pick.Group, pick.Text));
                return;
            }
        }

        if (_random.NextDouble() < 0.6)
        {
            string fbId = lower.EndsWith('?') ? "answers_advice" : "reactions_humor";
            if (_chatGroupById.TryGetValue(fbId, out var fb) && PickGroupMessage(fb) is { } msg)
                room.Pending.Add((now.AddSeconds(2 + _random.NextDouble() * 3), PickChatSpeaker(used), fb.Id, msg));
        }
    }

    ChatRoom? FakeChatRoomOf(ClientState conn, ChatChannelKind kind) =>
        (kind != ChatChannelKind.Trade || simulateTrade) && ChatRoom.Of(conn, kind) is { } room
        && routing.Find(kind, room.Channel)?.Type == ChatChannelRouteType.Fake ? room : null;

    static bool IsChatListener(ClientState conn) => ChatRoom.IsListener(conn);

    FakeChatRoom Room(ChatRoom key, DateTime now)
    {
        if (!_fakeRooms.TryGetValue(key, out var room)) _fakeRooms.Add(key, room = new(now));
        return room;
    }

    double NextGlobalChatGapSeconds() => 12 + _random.NextDouble() * 26;

    void TickGlobalChat()
    {
        if (!routing.HasFakeChat)
        {
            UnloadGlobalChat();
            return;
        }
        var now = DateTime.UtcNow;
        long tick = ++_chatTick;
        foreach (var (conn, _) in Program.InGame)
        {
            if (!IsChatListener(conn)) continue;
            if (FakeChatRoomOf(conn, ChatChannelKind.Global) is { } global) Room(global, now).LastSeenTick = tick;
            if (FakeChatRoomOf(conn, ChatChannelKind.Trade) is { } trade) Room(trade, now).LastSeenTick = tick;
        }
        foreach (var (key, room) in _fakeRooms)
        {
            if (room.LastSeenTick != tick) { _fakeRooms.Remove(key); continue; }
            if (EnsureGlobalChatLoaded()) TickChatRoom(key, room, now);
        }
        if (_fakeRooms.Count == 0) UnloadGlobalChat();
    }

    void TickChatRoom(ChatRoom key, FakeChatRoom room, DateTime now)
    {
        if (room.Pending.Count == 0 && now >= room.NextChat)
        {
            EnqueueChatEvent(room, now);
            room.NextChat = now.AddSeconds(NextGlobalChatGapSeconds());
        }
        List<(DateTime DueUtc, string Speaker, string Group, string Text)>? due = null;
        for (int i = room.Pending.Count - 1; i >= 0; i--)
        {
            if (room.Pending[i].DueUtc > now) continue;
            (due ??= []).Add(room.Pending[i]);
            room.Pending.RemoveAt(i);
        }
        if (due is null) return;
        var listeners = new List<ClientState>();
        foreach (var player in Program.InGame)
            if (IsChatListener(player.Key) && FakeChatRoomOf(player.Key, key.Kind) == key) listeners.Add(player.Key);
        due.Sort((a, b) => a.DueUtc.CompareTo(b.DueUtc));
        foreach (var item in due)
            _ = BroadcastChatAsync(key, listeners, item.Speaker, item.Text);
    }

    async Task BroadcastChatAsync(ChatRoom room, IReadOnlyList<ClientState> listeners, string speaker, string text)
    {
        string senderTag = InstanceChat.Sender(room.TypedChannel, speaker);
        foreach (var conn in listeners)
        {
            if (stop.IsCancellationRequested || !IsChatListener(conn) || FakeChatRoomOf(conn, room.Kind) != room || conn.Outbox is not { } outbox) continue;
            // Allocate per recipient because Outbox encrypts the buffer in place.
            byte[] packet = InstanceChat.ChatLine(text, senderTag);
            try { await outbox(packet, $"0x0017 {room} fake chat from {speaker}"); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    static readonly string[] GivenNames = ["Nyx", "Brynn", "Sigrid", "Corvin", "Dorian", "Maelis",
        "Riven", "Thalia", "Vesper", "Ivara", "Kaelen", "Neris", "Sylas", "Veyra"];
    static readonly string[] Epithets = ["Ash", "Grimm", "Ironwood", "Hollow", "Rook", "Moonveil",
        "Grey", "Blackthorn", "Storm", "Wren", "Ember", "Barrow"];
    static readonly string[] ChineseNames = ["星河旅人", "山间拾梦", "听雨归人", "云上青舟", "月下寻风",
        "雾海微光", "长夜有灯", "暮色归舟", "青山旧客", "清风问月", "独行山海", "流萤照野"];

    string PickChatSpeaker(HashSet<string>? usedInBurst = null)
    {
        string name;
        int attempt = 0;
        do
        {
            name = _random.NextDouble() < 0.72
                ? $"{GivenNames[_random.Next(GivenNames.Length)]}_{Epithets[_random.Next(Epithets.Length)]}"
                : ChineseNames[_random.Next(ChineseNames.Length)];
            if (++attempt > 20) name += "_" + new string(_random.GetItems("abcdefghijklmnopqrstuvwxyz".AsSpan(), 3));
        }
        while (usedInBurst?.Contains(name) == true || _recentChatSpeakers.Contains(name)
            || Program.InGame.Keys.Any(conn => string.Equals(Program.CharacterName(conn), name, StringComparison.OrdinalIgnoreCase)));
        RememberSpeaker(name);
        usedInBurst?.Add(name);
        return name;
    }

    void RememberSpeaker(string name)
    {
        _recentChatSpeakers.Enqueue(name);
        while (_recentChatSpeakers.Count > 8) _recentChatSpeakers.Dequeue();
    }

    string? PickGroupMessage(GlobalChatGroup group)
    {
        if (group.Messages.Length == 0) return null;
        string pick = group.Messages[_random.Next(group.Messages.Length)];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string cand = group.Messages[_random.Next(group.Messages.Length)];
            if (!_recentChatMessages.Contains(cand)) { pick = cand; break; }
        }
        _recentChatMessages.Enqueue(pick);
        while (_recentChatMessages.Count > 64) _recentChatMessages.Dequeue();
        return pick;
    }

    void EnqueueChatEvent(FakeChatRoom room, DateTime now)
    {
        var lines = _ambientChat!.NextEvent(_random, group => PickGroupMessage(group)!);
        var speakerMap = new Dictionary<int, string>();
        var usedSpeakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!speakerMap.TryGetValue(line.Speaker, out string? speaker))
                speakerMap[line.Speaker] = speaker = PickChatSpeaker(usedSpeakers);
            room.Pending.Add((now.AddSeconds(Math.Clamp(line.DelaySeconds, 0, 45)), speaker, line.Group, line.Text));
        }
    }
}
