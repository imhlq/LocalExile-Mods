using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlobalChat;

sealed record GlobalChatGroup(string Id, string[] Messages);
sealed record GlobalChatLibrary(GlobalChatGroup[] Groups, GlobalChatBurst[] Bursts);

sealed record GlobalChatBurstLine(
    [property: JsonPropertyName("speaker")] int Speaker,
    [property: JsonPropertyName("delay_s")] int DelaySeconds,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("text")] string Text);

sealed record GlobalChatBurst([property: JsonPropertyName("lines")] GlobalChatBurstLine[] Lines);
sealed record ChatEventCount(int Length, int Repeats, int Count);
sealed record ChatBaseline(Dictionary<string, int> GroupCounts, ChatEventCount[] EventCounts);

[JsonSerializable(typeof(GlobalChatLibrary))]
[JsonSerializable(typeof(ChatBaseline))]
sealed partial class SimulatedChatJsonContext : JsonSerializerContext;

/// <summary>Samples ambient events while matching the capture's line-level category mix.</summary>
sealed class AmbientChatModel
{
    readonly (int Count, GlobalChatBurstLine[][] Templates)[] events;
    readonly (GlobalChatGroup Group, double Weight)[] singles;
    readonly int eventTotal;
    readonly double singleTotal;

    internal AmbientChatModel(GlobalChatLibrary library, ChatBaseline baseline)
    {
        var singleWeights = library.Groups.ToDictionary(g => g.Id, g => (double)baseline.GroupCounts[g.Id]);
        var buckets = new List<(int Count, GlobalChatBurstLine[][] Templates)>();
        foreach (var shape in baseline.EventCounts)
        {
            if (shape.Length == 1) continue;
            var templates = library.Bursts.Where(b => b.Lines.Length >= shape.Length)
                .Select(b => b.Lines[..shape.Length])
                .Where(lines => CountRepeats(lines) == shape.Repeats)
                .ToArray();
            if (templates.Length == 0)
                throw new InvalidDataException($"No chat templates for {shape.Length} lines with {shape.Repeats} speaker repeats.");
            buckets.Add((shape.Count, templates));

            // Remove the expected contribution of whole bursts before drawing standalone lines.
            foreach (var category in templates.SelectMany(lines => lines).GroupBy(line => line.Group))
                singleWeights[category.Key] -= (double)shape.Count * category.Count() / templates.Length;
        }
        if (singleWeights.Values.Any(weight => weight < 0))
            throw new InvalidDataException("Chat templates exceed a category's observed line count.");

        events = buckets.ToArray();
        eventTotal = baseline.EventCounts.Sum(shape => shape.Count);
        singles = library.Groups.Where(g => singleWeights[g.Id] > 0)
            .Select(g => (g, singleWeights[g.Id])).ToArray();
        singleTotal = singles.Sum(item => item.Weight);
    }

    static int CountRepeats(GlobalChatBurstLine[] lines)
    {
        int repeats = 0;
        for (int i = 1; i < lines.Length; i++)
            if (lines[i].Speaker == lines[i - 1].Speaker) repeats++;
        return repeats;
    }

    internal GlobalChatBurstLine[] NextEvent(Random random, Func<GlobalChatGroup, string> pickMessage)
    {
        int eventTicket = random.Next(eventTotal);
        foreach (var (count, templates) in events)
        {
            if (eventTicket < count) return templates[random.Next(templates.Length)];
            eventTicket -= count;
        }

        double lineTicket = random.NextDouble() * singleTotal;
        foreach (var (group, weight) in singles)
        {
            if (lineTicket < weight) return [new(0, 0, group.Id, pickMessage(group))];
            lineTicket -= weight;
        }
        var last = singles[^1].Group;
        return [new(0, 0, last.Id, pickMessage(last))];
    }

    internal static (GlobalChatLibrary Library, AmbientChatModel Model) Load()
    {
        var assembly = typeof(AmbientChatModel).Assembly;
        using var resource = assembly.GetManifestResourceStream("GlobalChat.SimulatedChat")
            ?? throw new InvalidDataException("The simulated chat library is missing.");
        using var text = new StreamReader(resource);
        using var compressed = new MemoryStream(Convert.FromBase64String(text.ReadToEnd()));
        using var stream = new BrotliStream(compressed, CompressionMode.Decompress);
        var library = JsonSerializer.Deserialize(stream, SimulatedChatJsonContext.Default.GlobalChatLibrary)
            ?? throw new InvalidDataException("The simulated chat library is empty.");
        using var counts = assembly.GetManifestResourceStream("GlobalChat.ChatBaseline")
            ?? throw new InvalidDataException("The simulated chat baseline is missing.");
        var baseline = JsonSerializer.Deserialize(counts, SimulatedChatJsonContext.Default.ChatBaseline)
            ?? throw new InvalidDataException("The simulated chat baseline is empty.");
        return (library, new(library, baseline));
    }
}
