// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Xml;

struct InstructionSequence
{
    public long Count;
    public long Bytes;

    public InstructionSequence Plus(InstructionSequence sequence)
    {
        return new InstructionSequence()
        {
            Count = Count + sequence.Count,
            Bytes = Bytes + sequence.Bytes,
        };
    }

    public void Parse(XmlReader reader)
    {
        while (reader.IsStartElement())
        {
            switch (reader.Name)
            {
                case "count":
                    Count = reader.ReadElementContentAsLong();
                    break;

                case "bytes":
                    Bytes = reader.ReadElementContentAsLong();
                    break;

                default:
                    throw new XmlException($"expected instruction sequence element, found: {reader.Name}");
            }
            reader.Read();
        }
    }
}

class CodeStream
{
    public ulong BeginPC;
    public ulong EndPC;
    public ulong SP;
    public InstructionSequence Instructions;
    public string Module = "";
    public string Symbol = "";
    public uint SymbolOffset;

    public static CodeStream Parse(XmlReader reader)
    {
        CodeStream stream = new CodeStream();
        stream.ParseInner(reader);
        return stream;
    }

    private void ParseInner(XmlReader reader)
    {
        while (reader.IsStartElement())
        {
            switch (reader.Name)
            {
                case "begin_pc":
                    BeginPC = (ulong)reader.ReadElementContentAsLong();
                    break;

                case "end_pc":
                    EndPC = (ulong)reader.ReadElementContentAsLong();
                    break;

                case "sp":
                    SP = (ulong)reader.ReadElementContentAsLong();
                    break;

                case "module":
                    Module = reader.ReadElementContentAsString();
                    break;

                case "symbol":
                    Symbol = reader.ReadElementContentAsString();
                    break;

                case "symbol_offset":
                    SymbolOffset = (uint)reader.ReadElementContentAsInt();
                    break;

                case "instructions":
                    reader.Read();
                    Instructions.Parse(reader);
                    break;

                default:
                    throw new XmlException($"expected code stream element, found: {reader.Name}");
            }
            reader.Read();
        }
    }
}

class ThreadExecution
{
    public readonly List<CodeStream> CodeStreams = new List<CodeStream>();

    public ThreadExecution()
    {
    }

    public static ThreadExecution Parse(XmlReader reader)
    {
        ThreadExecution threadExec = new ThreadExecution();
        threadExec.ParseInner(reader);
        return threadExec;
    }

    private void ParseInner(XmlReader reader)
    {
        reader.ReadStartElement("streams");
        while (reader.IsStartElement())
        {
            switch (reader.Name)
            {
                case "stream":
                    reader.Read();
                    CodeStreams.Add(CodeStream.Parse(reader));
                    break;

                default:
                    throw new XmlException($"expected code stream, found: {reader.Name}");
            }
            reader.ReadEndElement();
        }
        reader.ReadEndElement();
    }
}

class ExecutionTrace
{
    public readonly string FileName;
    public readonly Dictionary<long, ThreadExecution> Threads = new Dictionary<long, ThreadExecution>();

    public ExecutionTrace(string fileName)
    {
        FileName = fileName;
    }

    public static ExecutionTrace LoadFile(string fileName)
    {
        ExecutionTrace trace = new ExecutionTrace(fileName);
        using (XmlReader reader = XmlReader.Create(fileName))
        {
            trace.ParseInner(reader);
        }
        return trace;
    }

    private void ParseInner(XmlReader reader)
    {
        reader.ReadStartElement("threads");
        while (reader.IsStartElement())
        {
            switch (reader.Name)
            {
                case "thread":
                    {
                        long.TryParse(reader.GetAttribute("id") ?? "", out long id);
                        reader.Read();
                        ThreadExecution threadExec = ThreadExecution.Parse(reader);
                        Threads.Add(id, threadExec);
                        break;
                    }

                default:
                    throw new XmlException($"expected thread, found: {reader.Name}");
            }
            reader.ReadEndElement();
        }
        reader.ReadEndElement();
    }
}
