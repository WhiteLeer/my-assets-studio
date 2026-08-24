using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnimeStudio;

public static class ExternalTypeTreeDatabase
{
    private static readonly Regex ClassHeader = new(
        @"^// classID\{(?<id>-?\d+)\}:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NodeLine = new(
        @"^(?<prefix>.+?) // ByteSize\{(?<size>[0-9a-fA-F]+)\}, Index\{(?<index>[0-9a-fA-F]+)\}, Version\{(?<version>[0-9a-fA-F]+)\}, IsArray\{(?<array>[0-9a-fA-F]+)\}, MetaFlag\{(?<meta>[0-9a-fA-F]+)\}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyDictionary<int, TypeTree> trees = new Dictionary<int, TypeTree>();
    private static IReadOnlySet<int> allowedClassIDs;

    public static string SourcePath { get; private set; } = string.Empty;

    public static void Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            trees = new Dictionary<int, TypeTree>();
            allowedClassIDs = null;
            SourcePath = string.Empty;
            return;
        }

        allowedClassIDs = ParseClassFilter(Environment.GetEnvironmentVariable("SR_EXTERNAL_TYPETREE_CLASSES"));

        var parsedTrees = new Dictionary<int, TypeTree>();
        int? currentClassID = null;
        List<TypeTreeNode> currentNodes = null;

        void CommitCurrent()
        {
            if (!currentClassID.HasValue || currentNodes is not { Count: > 0 })
                return;

            if (!IsPlausibleTree(currentNodes))
            {
                Logger.Warning($"Ignored malformed external TypeTree for classID {currentClassID.Value}; the dump contains invalid node data.");
                return;
            }

            parsedTrees[currentClassID.Value] = new TypeTree { m_Nodes = currentNodes };
        }

        foreach (var line in File.ReadLines(path))
        {
            var classMatch = ClassHeader.Match(line);
            if (classMatch.Success)
            {
                CommitCurrent();
                currentClassID = int.Parse(classMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
                currentNodes = new List<TypeTreeNode>();
                continue;
            }

            if (!currentClassID.HasValue || string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.TrimStart(' ', '\t');
            var nodeMatch = NodeLine.Match(trimmed);
            if (!nodeMatch.Success)
                continue;

            var prefix = nodeMatch.Groups["prefix"].Value;
            var separator = prefix.LastIndexOf(' ');
            if (separator <= 0 || separator == prefix.Length - 1)
                continue;

            var leading = line.Length - trimmed.Length;
            var tabs = line.Take(leading).Count(character => character == '\t');
            var spaces = leading - tabs;
            currentNodes.Add(new TypeTreeNode
            {
                m_Type = prefix[..separator],
                m_Name = prefix[(separator + 1)..],
                m_Level = tabs + spaces / 4,
                m_ByteSize = unchecked((int)uint.Parse(nodeMatch.Groups["size"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                m_Index = unchecked((int)uint.Parse(nodeMatch.Groups["index"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                m_Version = unchecked((int)uint.Parse(nodeMatch.Groups["version"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                m_TypeFlags = unchecked((int)uint.Parse(nodeMatch.Groups["array"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                m_MetaFlag = unchecked((int)uint.Parse(nodeMatch.Groups["meta"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            });
        }

        CommitCurrent();
        trees = parsedTrees;
        SourcePath = Path.GetFullPath(path);
        Logger.Info($"Loaded {trees.Count} external type tree(s) from {SourcePath}.");
    }

    private static IReadOnlySet<int> ParseClassFilter(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var classIDs = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var classID)
                ? (int?)classID
                : null)
            .Where(classID => classID.HasValue)
            .Select(classID => classID.Value)
            .ToHashSet();

        return classIDs.Count > 0 ? classIDs : null;
    }

    private static bool IsPlausibleTree(IReadOnlyList<TypeTreeNode> nodes)
    {
        if (nodes.Count < 2 || nodes[0].m_Level != 0)
            return false;

        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.m_Type) || string.IsNullOrWhiteSpace(node.m_Name) ||
                node.m_Type.StartsWith("<invalid-", StringComparison.Ordinal) ||
                node.m_Name.StartsWith("<invalid-", StringComparison.Ordinal) ||
                node.m_Level < 0 || node.m_Level > 64)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryGet(int classID, out TypeTree typeTree)
    {
        if (allowedClassIDs != null && !allowedClassIDs.Contains(classID))
        {
            typeTree = null;
            return false;
        }

        return trees.TryGetValue(classID, out typeTree);
    }
}
