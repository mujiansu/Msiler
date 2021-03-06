﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using System.IO;

namespace Msiler.AssemblyParser
{
    internal struct PDBCheckSumAlgorithms
    {
        public static Guid MD5Guid  = new Guid(0x406ea660, 0x64cf, 0x4c82, 0xb6, 0xf0, 0x42, 0xd4, 0x81, 0x72, 0xa7, 0x99);
        public static Guid SHA1Guid = new Guid(0xff1816ec, 0xaa5e, 0x4d10, 0x87, 0xf7, 0x6f, 0x49, 0x63, 0x83, 0x34, 0x60);
    }

    public class ListingGenerator : IDisposable
    {
        private HashSet<string> _warnings = new HashSet<string>();

        private readonly ListingGeneratorOptions _options;
        private MethodBytesReader _bytesReader;

        private readonly Dictionary<string, List<string>> _pdbCache =
            new Dictionary<string, List<string>>();

        private int _longestOpCode = -1;
        private int _longestByteSeq = -1;

        public ListingGenerator(ListingGeneratorOptions options) {
            this._options = options;
        }

        private string GetHeader(AssemblyMethod m) => $"Method: {m.Signature.MethodName}";

        private string GetOffset(Instruction i) {
            var f = (this._options.DecimalOffsets) ? "IL_{0:D4}" : "IL_{0:X4}";
            return String.Format(f, i.Offset);
        }

        private string GetOperand(Instruction i) {
            if (i.OpCode.Code == Code.Ldstr) {
                return @"""" + i.Operand.ToString().ReplaceNewLineCharacters() + @"""";
            }

            if (i.Operand == null) {
                return String.Empty;
            }

            if (i.Operand is Instruction) {
                return GetOffset((Instruction)i.Operand);
            }

            if (i.Operand is Instruction[]) {
                var operands = (Instruction[])i.Operand;
                var joined = String.Join(" | ", operands.Select(GetOffset));
                return $"[ {joined} ]";
            }

            // TODO: rewrite this part of code
            if (i.Operand is IMemberRef) {
                var op = (IMemberRef)i.Operand;
                var simpleName = (op is ITypeDefOrRef)
                    ? op.FullName
                    : $"{op.DeclaringType.FullName}.{op.Name}";
                return (this._options.SimplifyFunctionNames) ? simpleName : op.FullName;
            }

            if (this._options.NumbersAsHex) {
                Int64 number;
                bool isNumeric = Int64.TryParse(i.Operand.ToString(), out number);
                if (isNumeric) {
                    return $"0x{number.ToString("X")}";
                }
            }
            return i.Operand.ToString();
        }

        public string GetOpCode(Instruction i) {
            var name = i.OpCode.Name;
            return (this._options.UpcaseOpCodes) ? name.ToUpper() : name;
        }

        private string InstructionToString(Instruction i, int longestOpcode) {
            var result = $"{GetOffset(i)} ";
            if (this._options.ReadInstructionBytes && this._bytesReader != null) {
                var bytesSeq = $"{this._bytesReader.ReadInstrution(i)} ";
                if (this._options.AlignListing) {
                    bytesSeq = bytesSeq.PadRight(_longestByteSeq + 1);
                }
                result += $"| {bytesSeq}| ";
            }
            var opcodePart = this._options.AlignListing
                ? GetOpCode(i).PadRight(longestOpcode + 1)
                : GetOpCode(i);
            result += $"{opcodePart} ";
            return result + $"{GetOperand(i)}";
        }

        public void ClearSourceCache() {
            this._pdbCache.Clear();
        }

        private string ParsePdbInformation(SequencePoint sp) {
            if (sp == null) {
                return String.Empty;
            }
            // If hidden line
            if (sp.StartLine == 0xfeefee) {
                return String.Empty;
            }
            // If invalid Url
            if (sp.Document?.Url == null) {
                return String.Empty;
            }
            var docUrl = sp.Document.Url;
            if (!_pdbCache.ContainsKey(sp.Document.Url)) {
                // if file not exists
                if (!File.Exists(docUrl)) {
                    return String.Empty;
                }

                byte[] currentDocumentHash = new byte[0];
                if (sp.Document.CheckSumAlgorithmId == PDBCheckSumAlgorithms.MD5Guid) {
                    currentDocumentHash = Helpers.ComputeMD5FileHash(docUrl);
                } else if (sp.Document.CheckSumAlgorithmId == PDBCheckSumAlgorithms.SHA1Guid) {
                    currentDocumentHash = Helpers.ComputeSHA1FileHash(docUrl);
                }

                // display warning if source file weas changed
                if (!Helpers.IsByteArraysEqual(currentDocumentHash, sp.Document.CheckSum)) {
                    _warnings.Add($"WARNING: Document {Path.GetFileName(docUrl)} was changed, PDB information can be incorrect.");
                }

                _pdbCache[docUrl] = File.ReadAllLines(docUrl).Select(s => s.Trim()).ToList();
            }
            var sb = new StringBuilder();
            for (int i = sp.StartLine - 1; i <= sp.EndLine - 1; i++) {
                var sourceLine = _pdbCache[docUrl][i];
                if (i < _pdbCache[docUrl].Count
                  && !String.IsNullOrWhiteSpace(sourceLine)
                  && !sourceLine.StartsWith("//", StringComparison.Ordinal)) {
                    sb.AppendLine($"// {sourceLine}");
                }
            }
            return sb.ToString();
        }

        public string GenerateListing(AssemblyMethod method) {
            var sb = new StringBuilder();

            if (this._options.IncludeMethodName) {
                sb.AppendLine($"// Selected method: {method.Signature.MethodName}");
                sb.AppendLine();
            }

            if (this._options.ReadInstructionBytes) {
                this._bytesReader = new MethodBytesReader(method.MethodDefinition);
            }

            if (this._options.AlignListing) {
                this._longestOpCode = method.Instructions.Max(i => i.OpCode.Name.Length);
                if (this._options.ReadInstructionBytes) {
                    this._longestByteSeq = method.Instructions.Max(i => i.GetSize()) * 2;
                }
            }

            foreach (var instruction in method.Instructions) {
                if (this._options.IgnoreNops && instruction.OpCode.Code == Code.Nop) {
                    continue;
                }
                if (this._options.ProcessPDBFiles) {
                    sb.Append(this.ParsePdbInformation(instruction.SequencePoint));
                }
                sb.AppendLine(InstructionToString(instruction, _longestOpCode));
            }

            string resultListing = sb.ToString();
            if (_warnings.Count > 0) {
                var warnStr = String.Join(Environment.NewLine, this._warnings);
                resultListing = warnStr + Environment.NewLine + sb.ToString();
            }
            return resultListing;
        }

        public void Dispose() {
            if (this._bytesReader != null) {
                this._bytesReader.Dispose();
            }
        }
    }
}
