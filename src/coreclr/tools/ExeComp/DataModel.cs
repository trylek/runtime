// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Xml;

namespace ExeComp
{
    public struct InstructionSequence : IEquatable<InstructionSequence>

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

        public InstructionSequence Minus(InstructionSequence sequence)
        {
            return new InstructionSequence()
            {
                Count = Count - sequence.Count,
                Bytes = Bytes - sequence.Bytes,
            };
        }

        public InstructionSequence Max(InstructionSequence sequence)
        {
            return new InstructionSequence()
            {
                Count = Math.Max(Count, sequence.Count),
                Bytes = Math.Max(Bytes, sequence.Bytes),
            };
        }

        public InstructionSequence Min(InstructionSequence sequence)
        {
            if (Count == 0)
            {
                return sequence;
            }
            if (sequence.Count == 0)
            {
                return this;
            }
            return new InstructionSequence()
            {
                Count = Math.Min(Count, sequence.Count),
                Bytes = Math.Min(Bytes, sequence.Bytes),
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

        public override bool Equals(object? other)
        {
            return other is InstructionSequence seq && Equals(seq);
        }

        public override int GetHashCode()
        {
            return Count.GetHashCode() ^ Bytes.GetHashCode();
        }

        public bool Equals(InstructionSequence other) => other.Count == this.Count && other.Bytes == this.Bytes;
    }

    public struct ModuleSymbol : IEquatable<ModuleSymbol>
    {
        public string Module;
        public string Symbol;

        public ModuleSymbol(string module, string symbol)
        {
            Module = module;
            Symbol = symbol;
        }

        public override string ToString()
        {
            return $"{Module}!{Symbol}";
        }

        public override int GetHashCode()
        {
            return (Module != null ? unchecked(31 * Module.GetHashCode()) : 0) ^ (Symbol != null ? Symbol.GetHashCode() : 0);
        }

        public override bool Equals(object? obj)
        {
            return obj is ModuleSymbol sym && Equals(sym);
        }

        public bool Equals(ModuleSymbol other) => Module == other.Module && Symbol == other.Symbol;
    }

    public class CodeStream
    {
        public ulong BeginPC;
        public ulong EndPC;
        public ulong MinPC;
        public ulong MaxPC;
        public ulong SP;
        public InstructionSequence Instructions;
        public ModuleSymbol ModuleSymbol;
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

                    case "min_pc":
                        MinPC = (ulong)reader.ReadElementContentAsLong();
                        break;

                    case "max_pc":
                        MaxPC = (ulong)reader.ReadElementContentAsLong();
                        break;

                    case "sp":
                        SP = (ulong)reader.ReadElementContentAsLong();
                        break;

                    case "module":
                        ModuleSymbol.Module = reader.ReadElementContentAsString();
                        break;

                    case "symbol":
                        ModuleSymbol.Symbol = reader.ReadElementContentAsString();
                        break;

                    case "offset":
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

    public class ThreadExecution
    {
        public readonly List<CodeStream> CodeStreams = new List<CodeStream>();

        public ThreadExecution()
        {
        }

        public static ThreadExecution Parse(XmlReader reader, ref int progress)
        {
            ThreadExecution threadExec = new ThreadExecution();
            threadExec.ParseInner(reader, ref progress);
            return threadExec;
        }

        private void ParseInner(XmlReader reader, ref int progress)
        {
            reader.ReadStartElement("streams");
            while (reader.IsStartElement())
            {
                switch (reader.Name)
                {
                    case "stream":
                        reader.Read();
                        CodeStreams.Add(CodeStream.Parse(reader));
                        if ((++progress % 1000000) == 0)
                        {
                            Console.WriteLine("{0} code streams parsed", progress);
                        }
                        break;

                    default:
                        throw new XmlException($"expected code stream, found: {reader.Name}");
                }
                reader.ReadEndElement();
            }
            reader.ReadEndElement();
        }
    }

    public class ExecutionTrace
    {
        public readonly string FileName;
        public readonly Dictionary<long, ThreadExecution> Threads = new Dictionary<long, ThreadExecution>();

        public ExecutionTrace(string fileName)
        {
            FileName = fileName;
        }

        public static ExecutionTrace LoadFile(string fileName)
        {
            int progress = 0;
            ExecutionTrace trace = new ExecutionTrace(fileName);
            using (XmlReader reader = XmlReader.Create(fileName))
            {
                trace.ParseInner(reader, ref progress);
            }
            Console.WriteLine("{0} code streams read from {1}", progress, fileName);
            return trace;
        }

        private void ParseInner(XmlReader reader, ref int progress)
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
                            ThreadExecution threadExec = ThreadExecution.Parse(reader, ref progress);
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
}
