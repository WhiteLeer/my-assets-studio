using AnimeStudio;
using MessagePack;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ZZZMapProbe <map-directory-or-file> <regex> [limit]");
    return 2;
}

var mapPath = args[0];
var pattern = new System.Text.RegularExpressions.Regex(args[1], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var limit = args.Length > 2 && int.TryParse(args[2], out var parsedLimit) ? parsedLimit : 100;
var files = Directory.Exists(mapPath)
    ? Directory.GetFiles(mapPath, "*.map").OrderBy(x => x)
    : new[] { mapPath }.OrderBy(x => x);
var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
var count = 0;
var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var file in files)
{
    using var stream = File.OpenRead(file);
    var map = MessagePackSerializer.Deserialize<AssetMap>(stream, options);
    foreach (var entry in map.AssetEntries)
    {
        if (!pattern.IsMatch(entry.Name ?? string.Empty))
            continue;

        var key = $"{entry.Type}|{entry.Name}|{entry.Source}|{entry.PathID}|{entry.Offset}";
        if (!seen.Add(key))
            continue;

        Console.WriteLine($"{entry.Type}\t{entry.Name}\t{entry.Source}\t{entry.PathID}\t{entry.Offset}");
        count++;
        if (count >= limit)
            return 0;
    }
}

Console.Error.WriteLine($"Matches: {count}");
return 0;
