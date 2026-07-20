using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeStudio;
using MessagePack;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AnimationIndex <asset.map> [--query <regex>] [--output <index.json>]");
    return 1;
}

var mapPath = Path.GetFullPath(args[0]);
string? query = null;
string? outputPath = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--query" when i + 1 < args.Length:
            query = args[++i];
            break;
        case "--output" when i + 1 < args.Length:
            outputPath = Path.GetFullPath(args[++i]);
            break;
    }
}

await using var stream = File.OpenRead(mapPath);
var assetMap = MessagePackSerializer.Deserialize<AssetMap>(
    stream,
    MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
var entries = assetMap.AssetEntries ?? [];

Console.WriteLine($"Game: {assetMap.GameType}");
Console.WriteLine($"Entries: {entries.Count}");
foreach (var group in entries.GroupBy(x => x.Type).OrderByDescending(x => x.Count()))
{
    Console.WriteLine($"Type {group.Key}: {group.Count()}");
}

if (!string.IsNullOrWhiteSpace(query))
{
    var regex = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    var matches = entries.Where(x => regex.IsMatch(x.Name ?? string.Empty)).ToArray();
    Console.WriteLine($"Matches: {matches.Length}");
    foreach (var entry in matches.Take(500))
    {
        Console.WriteLine($"{entry.Name}\t{entry.Source}\t{entry.PathID}\t{entry.Offset}");
    }
}

if (!string.IsNullOrWhiteSpace(outputPath))
{
    var animationEntries = entries
        .Where(x => x.Type == ClassIDType.AnimationClip && !string.IsNullOrWhiteSpace(x.Name))
        .Select(x => new AnimationEntry(x.Name, GetActionKey(x.Name), x.Source, x.PathID, x.Offset))
        .ToArray();
    var indexedEntries = animationEntries.Select((entry, index) => new { entry, index }).ToArray();
    var index = new AnimationIndexDocument(
        1,
        assetMap.GameType.ToString(),
        animationEntries.Length,
        animationEntries,
        indexedEntries.GroupBy(x => x.entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.index).ToArray(), StringComparer.OrdinalIgnoreCase),
        indexedEntries.GroupBy(x => x.entry.Action, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.index).ToArray(), StringComparer.OrdinalIgnoreCase),
        indexedEntries.GroupBy(x => x.entry.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.index).ToArray(), StringComparer.OrdinalIgnoreCase));

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await using var output = File.Create(outputPath);
    await JsonSerializer.SerializeAsync(output, index, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine($"Index: {outputPath}");
}

return 0;

static string GetActionKey(string name)
{
    var marker = name.LastIndexOf("_Ani_", StringComparison.OrdinalIgnoreCase);
    return marker >= 0 ? name[(marker + 5)..] : name;
}

internal sealed record AnimationEntry(string Name, string Action, string Source, long PathID, long Offset);
internal sealed record AnimationIndexDocument(
    int Version,
    string Game,
    int AssetCount,
    AnimationEntry[] Entries,
    Dictionary<string, int[]> ByName,
    Dictionary<string, int[]> ByAction,
    Dictionary<string, int[]> BySource);
