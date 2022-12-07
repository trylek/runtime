// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace MapCsvDiff
{
    public class Node
    {
        public readonly int Rva;
        public int Length;
        public readonly int Relocs;
        public readonly string Section;
        public readonly string Symbol;
        public readonly string NodeType;

        public Node(int rva, int length, int relocs, string section, string symbol, string nodeType)
        {
            Rva = rva;
            Length = length;
            Relocs = relocs;
            Section = section;
            Symbol = symbol;
            NodeType = nodeType;
        }
    }

    public class Map
    {
        public readonly List<Node> _nodes;

        public IReadOnlyList<Node> Nodes => _nodes;

        private Map(List<Node> nodes)
        {
            _nodes = nodes;
        }

        public static Map Load(string csvFile)
        {
            using (StreamReader reader = new StreamReader(csvFile))
            {
                // Skip header name line
                reader.ReadLine();
                List<Node> nodes = new List<Node>();
                for (; ;)
                {
                    string? rawLine = reader.ReadLine();
                    if (rawLine == null)
                    {
                        break;
                    }
                    string[] line = rawLine.Split(',');
                    if (line.Length == 6)
                    {
                        int rva = Convert.ToInt32(line[0], 16);
                        int length = line[1] == "" ? 0 : int.Parse(line[1]);
                        int relocs = line[2] == "" ? 0 : int.Parse(line[2]);
                        string section = line[3];
                        string symbol = line[4];
                        string nodeType = line[5];
                        if (symbol == "")
                        {
                            symbol = nodeType;
                            int prefixLength = 0;
                            while (prefixLength < nodeType.Length && char.IsAsciiLetterOrDigit(nodeType[prefixLength]))
                            {
                                prefixLength++;
                            }
                            nodeType = nodeType.Substring(0, prefixLength);
                        }
                        nodes.Add(new Node(rva, length, relocs, section, symbol, nodeType));
                    }
                }
                FixZeroSizes(nodes);
                return new Map(nodes);
            }
        }

        private static void FixZeroSizes(List<Node> nodes)
        {
            for (int row = 1; row < nodes.Count - 1; row++)
            {
                Node node = nodes[row];
                if (node.Length == 0 && node.Rva > nodes[row - 1].Rva)
                {
                    node.Length = nodes[row + 1].Rva - node.Rva;
                }
            }
        }
    }

    public class Diff
    {
        private List<Map> _maps;

        public Diff(List<Map> maps)
        {
            _maps = maps;
        }

        public void DumpDiff(TextWriter writer)
        {
            DumpNodeTypeDiff(writer);
        }

        public void DumpNodeTypeDiff(TextWriter writer)
        {
            writer.WriteLine("NODE TYPE SIZE AGGREGATION");
            writer.WriteLine("--------------------------");
            Dictionary<string, int[]> nodeTypeLengthMap = new Dictionary<string, int[]>();
            for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
            {
                Map map = _maps[mapIndex];
                foreach (Node node in map.Nodes)
                {
                    if (node.Length == 0)
                    {
                        continue;
                    }
                    if (!nodeTypeLengthMap.TryGetValue(node.NodeType, out int[]? sizesPerMap))
                    {
                        sizesPerMap = new int[_maps.Count];
                        nodeTypeLengthMap.Add(node.NodeType, sizesPerMap);
                    }
                    sizesPerMap[mapIndex] += node.Length;
                }
            }

            if (_maps.Count >= 2)
            {
                DumpSizeAggregation(writer, "NODE TYPE BY DIFF", nodeTypeLengthMap.OrderByDescending(kvp => kvp.Value[1] - kvp.Value[0]));
            }
            DumpSizeAggregation(writer, "NODE TYPE BY SIZE", nodeTypeLengthMap.OrderByDescending(kvp => kvp.Value[0]));
            DumpSizeAggregation(writer, "NODE TYPE BY NAME", nodeTypeLengthMap.OrderBy(kvp => kvp.Key));

            if (_maps.Count >= 2)
            {
                foreach (string nodeType in nodeTypeLengthMap.Where(kvp => kvp.Value[0] != kvp.Value[1]).Select(kvp => kvp.Key).OrderBy(k => k))
                {
                    HashSet<string> allNames = new HashSet<string>();
                    Dictionary<string, int>[] nameLengthMaps = new Dictionary<string, int>[_maps.Count];
                    for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
                    {
                        Dictionary<string, int> lengthMap = new Dictionary<string, int>();
                        nameLengthMaps[mapIndex] = lengthMap;
                        foreach (Node node in _maps[mapIndex].Nodes.Where(n => n.NodeType == nodeType))
                        {
                            lengthMap.TryGetValue(node.Symbol, out int size);
                            lengthMap[node.Symbol] = size + node.Length;
                            allNames.Add(node.Symbol);
                        }
                    }
                    foreach (string name in allNames.OrderBy(n => n))
                    {
                        bool haveDiff = false;
                        nameLengthMaps[0].TryGetValue(name, out int length0);
                        StringBuilder sizes = new StringBuilder();
                        for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
                        {
                            nameLengthMaps[mapIndex].TryGetValue(name, out int length);
                            sizes.AppendFormat(mapIndex == 0 ? "[{0}" : " {0}", length);
                            if (length != length0)
                            {
                                haveDiff = true;
                            }
                        }
                        if (haveDiff)
                        {
                            sizes.AppendFormat("] {0}", name);
                            writer.WriteLine(sizes.ToString());
                        }
                    }
                }
            }
        }

        public void DumpSizeAggregation(TextWriter writer, string title, IEnumerable<KeyValuePair<string, int[]>> rows)
        {
            StringBuilder titleLine = new StringBuilder();
            for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
            {
                titleLine.AppendFormat("MAP #{0,-2}  | ", mapIndex);
                if (mapIndex > 0)
                {
                    titleLine.AppendFormat("DIFF #{0,-2} | ", mapIndex);
                }
            }
            titleLine.Append(title);
            writer.WriteLine(titleLine.ToString());
            writer.WriteLine(new string('-', titleLine.Length));
            int total0 = rows.Select(kvp => kvp.Value[0]).Sum();
            for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
            {
                int total = rows.Select(kvp => kvp.Value[mapIndex]).Sum();
                writer.Write("{0,8} | ", total);
                if (mapIndex > 0)
                {
                    writer.Write("{0,8} | ", total - total0);
                }
            }
            writer.WriteLine("(total)");

            foreach (KeyValuePair<string, int[]> row in rows)
            {
                for (int mapIndex = 0; mapIndex < _maps.Count; mapIndex++)
                {
                    writer.Write("{0,8} | ", row.Value[mapIndex]);
                    if (mapIndex > 0)
                    {
                        writer.Write("{0,8} | ", row.Value[mapIndex] - row.Value[0]);
                    }
                }
                writer.WriteLine(row.Key);
            }

            writer.WriteLine();
        }
    }
}
