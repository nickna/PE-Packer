using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace PEPacker;

public partial class AssemblyReferenceRewriter
{
    // ECMA-335 II.25.4 — Method body format
    private const byte TinyFormatFlag = 0x02;
    private const ushort FatFormatFlags = 0x3003;   // Fat format + header size 3 dwords
    private const ushort FatInitLocalsFlag = 0x0010;
    private const ushort FatMoreSectionsFlag = 0x0008;

    // ECMA-335 II.25.4.6 — Exception handler section
    private const byte SmallExceptionSectionFlag = 0x01;
    private const byte FatExceptionSectionFlag = 0x41;

    // Metadata token layout
    private const int MetadataTableMask = 0xFF;
    private const int MetadataRowMask = 0x00FFFFFF;
    private const int MetadataTableShift = 24;

    // ECMA-335 II.22 — Metadata table indices
    private const int TableTypeRef = 0x01;
    private const int TableTypeDef = 0x02;
    private const int TableFieldDef = 0x04;
    private const int TableMethodDef = 0x06;
    private const int TableMemberRef = 0x0A;
    private const int TableStandAloneSig = 0x11;
    private const int TableTypeSpec = 0x1B;
    private const int TableMethodSpec = 0x2B;
    private const int TableUserString = 0x70;

    private int CopyMethodBody(MethodDefinition method)
    {
        var body = _peReader.GetMethodBody(method.RelativeVirtualAddress);
        var ilBytes = body.GetILBytes();

        if (ilBytes == null || ilBytes.Length == 0)
        {
            return -1;
        }

        // Patch metadata tokens in IL
        var patchedIL = PatchILTokens(ilBytes);

        // Get local variables signature. Every StandAloneSig row is copied up front
        // (see CopyStandaloneSignatures), so a miss here means the phase order broke.
        StandaloneSignatureHandle localSig = default;
        if (!body.LocalSignature.IsNil &&
            !_standAloneSigMap.TryGetValue(body.LocalSignature, out localSig))
        {
            throw new PEPackerException(
                $"Local variable signature 0x{MetadataTokens.GetToken(body.LocalSignature):X8} " +
                "has no mapping; standalone signatures must be copied before method bodies.");
        }

        // Build method body by writing directly to the IL stream
        var exceptionRegions = body.ExceptionRegions;
        bool initLocals = body.LocalVariablesInitialized;

        // Check if we can use tiny format (no exceptions, no locals, code < 64 bytes, max stack <= 8)
        bool canUseTinyFormat = exceptionRegions.Length == 0 &&
                                localSig.IsNil &&
                                patchedIL.Length < 64 &&
                                body.MaxStack <= 8;

        int methodBodyOffset;

        if (canUseTinyFormat)
        {
            // Tiny format: 1-byte header (format bits + code size)
            // Format: (CodeSize << 2) | 0x02
            methodBodyOffset = _ilStream.Count;
            byte header = (byte)((patchedIL.Length << 2) | TinyFormatFlag);
            _ilStream.WriteByte(header);
            _ilStream.WriteBytes(patchedIL);
        }
        else
        {
            // Fat format: 12-byte header
            // Align to 4-byte boundary BEFORE recording offset
            int alignment = 4 - (_ilStream.Count % 4);
            if (alignment < 4)
            {
                for (int i = 0; i < alignment; i++)
                    _ilStream.WriteByte(0);
            }

            // Record offset AFTER alignment
            methodBodyOffset = _ilStream.Count;

            // Fat header (12 bytes)
            // Flags (2 bytes): 0x3 = fat format, 0x10 = init locals, 0x8 = more sections
            ushort flags = FatFormatFlags;
            if (initLocals)
                flags |= FatInitLocalsFlag;
            if (exceptionRegions.Length > 0)
                flags |= FatMoreSectionsFlag;

            _ilStream.WriteUInt16(flags);
            _ilStream.WriteUInt16((ushort)body.MaxStack);
            _ilStream.WriteInt32(patchedIL.Length);
            _ilStream.WriteInt32(localSig.IsNil ? 0 : MetadataTokens.GetToken(localSig));

            // IL code
            _ilStream.WriteBytes(patchedIL);

            // Exception handlers (if any)
            if (exceptionRegions.Length > 0)
            {
                // Align to 4-byte boundary
                alignment = 4 - (_ilStream.Count % 4);
                if (alignment < 4)
                {
                    for (int i = 0; i < alignment; i++)
                        _ilStream.WriteByte(0);
                }

                // Determine if we need fat exception handlers
                bool needsFatHandlers = false;
                foreach (var region in exceptionRegions)
                {
                    if (region.TryOffset > 0xFFFF || region.TryLength > 0xFF ||
                        region.HandlerOffset > 0xFFFF || region.HandlerLength > 0xFF)
                    {
                        needsFatHandlers = true;
                        break;
                    }
                }

                if (needsFatHandlers)
                {
                    // Fat exception header
                    int dataSize = 4 + (exceptionRegions.Length * 24);
                    _ilStream.WriteByte(FatExceptionSectionFlag);
                    _ilStream.WriteByte((byte)(dataSize & 0xFF));
                    _ilStream.WriteByte((byte)((dataSize >> 8) & 0xFF));
                    _ilStream.WriteByte((byte)((dataSize >> 16) & 0xFF));

                    foreach (var region in exceptionRegions)
                    {
                        int flags2 = region.Kind switch
                        {
                            ExceptionRegionKind.Catch => 0,
                            ExceptionRegionKind.Filter => 1,
                            ExceptionRegionKind.Finally => 2,
                            ExceptionRegionKind.Fault => 4,
                            _ => 0
                        };
                        _ilStream.WriteInt32(flags2);
                        _ilStream.WriteInt32(region.TryOffset);
                        _ilStream.WriteInt32(region.TryLength);
                        _ilStream.WriteInt32(region.HandlerOffset);
                        _ilStream.WriteInt32(region.HandlerLength);

                        if (region.Kind == ExceptionRegionKind.Catch)
                        {
                            var catchType = MapEntityHandle(region.CatchType);
                            _ilStream.WriteInt32(MetadataTokens.GetToken(catchType));
                        }
                        else if (region.Kind == ExceptionRegionKind.Filter)
                        {
                            _ilStream.WriteInt32(region.FilterOffset);
                        }
                        else
                        {
                            _ilStream.WriteInt32(0);
                        }
                    }
                }
                else
                {
                    // Small exception header
                    int dataSize = 4 + (exceptionRegions.Length * 12);
                    _ilStream.WriteByte(SmallExceptionSectionFlag);
                    _ilStream.WriteByte((byte)dataSize);
                    _ilStream.WriteUInt16(0); // Reserved

                    foreach (var region in exceptionRegions)
                    {
                        ushort flags2 = region.Kind switch
                        {
                            ExceptionRegionKind.Catch => 0,
                            ExceptionRegionKind.Filter => 1,
                            ExceptionRegionKind.Finally => 2,
                            ExceptionRegionKind.Fault => 4,
                            _ => 0
                        };
                        _ilStream.WriteUInt16(flags2);
                        _ilStream.WriteUInt16((ushort)region.TryOffset);
                        _ilStream.WriteByte((byte)region.TryLength);
                        _ilStream.WriteUInt16((ushort)region.HandlerOffset);
                        _ilStream.WriteByte((byte)region.HandlerLength);

                        if (region.Kind == ExceptionRegionKind.Catch)
                        {
                            var catchType = MapEntityHandle(region.CatchType);
                            _ilStream.WriteInt32(MetadataTokens.GetToken(catchType));
                        }
                        else if (region.Kind == ExceptionRegionKind.Filter)
                        {
                            _ilStream.WriteInt32(region.FilterOffset);
                        }
                        else
                        {
                            _ilStream.WriteInt32(0);
                        }
                    }
                }
            }
        }

        return methodBodyOffset;
    }

    /// <summary>
    /// Walks a method body and rewrites every metadata token operand in place.
    /// Instruction lengths never change (tokens stay four bytes), so branch targets
    /// and exception-handler offsets carry over untouched.
    /// </summary>
    private byte[] PatchILTokens(byte[] ilBytes)
    {
        var result = new byte[ilBytes.Length];
        Buffer.BlockCopy(ilBytes, 0, result, 0, ilBytes.Length);

        int offset = 0;
        while (offset < ilBytes.Length)
        {
            int instructionStart = offset;
            byte opByte = ilBytes[offset++];

            ILOperandKind kind;
            if (opByte == ILOperandTable.ExtendedPrefix)
            {
                if (offset >= ilBytes.Length)
                {
                    throw new PEPackerException(
                        $"Method body ends mid-opcode: 0xFE prefix at IL offset {instructionStart}.");
                }
                kind = ILOperandTable.GetExtended(ilBytes[offset++]);
            }
            else
            {
                kind = ILOperandTable.Get(opByte);
            }

            switch (kind)
            {
                case ILOperandKind.None:
                    break;

                case ILOperandKind.Byte:
                    offset += 1;
                    break;

                case ILOperandKind.Short:
                    offset += 2;
                    break;

                case ILOperandKind.Int:
                    offset += 4;
                    break;

                case ILOperandKind.Long:
                    offset += 8;
                    break;

                case ILOperandKind.Token:
                    RequireBytes(ilBytes, offset, 4, instructionStart);
                    var token = BitConverter.ToInt32(ilBytes, offset);
                    BitConverter.TryWriteBytes(result.AsSpan(offset), MapMetadataToken(token));
                    offset += 4;
                    break;

                case ILOperandKind.Switch:
                    // ECMA-335 III.3.66: a 4-byte case count, then that many 4-byte targets.
                    RequireBytes(ilBytes, offset, 4, instructionStart);
                    uint caseCount = BitConverter.ToUInt32(ilBytes, offset);
                    offset += 4;
                    long tableEnd = offset + ((long)caseCount * 4);
                    if (tableEnd > ilBytes.Length)
                    {
                        throw new PEPackerException(
                            $"switch at IL offset {instructionStart} declares {caseCount} cases, " +
                            $"which overruns the {ilBytes.Length}-byte method body.");
                    }
                    offset = (int)tableEnd;
                    break;

                default:
                    throw new PEPackerException(
                        $"Undefined IL opcode 0x{(opByte == ILOperandTable.ExtendedPrefix ? 0xFE00 | ilBytes[offset - 1] : opByte):X2} " +
                        $"at IL offset {instructionStart}; refusing to rewrite a method body the decoder cannot walk.");
            }
        }

        return result;
    }

    private static void RequireBytes(byte[] ilBytes, int offset, int count, int instructionStart)
    {
        if (offset + count > ilBytes.Length)
        {
            throw new PEPackerException(
                $"Method body ends mid-operand: instruction at IL offset {instructionStart} " +
                $"needs {count} operand bytes but only {ilBytes.Length - offset} remain.");
        }
    }

    private int MapMetadataToken(int token)
    {
        var tableIndex = (token >> MetadataTableShift) & MetadataTableMask;
        var rowNumber = token & MetadataRowMask;

        if (rowNumber == 0)
            return token;

        return tableIndex switch
        {
            TableTypeRef =>
                MetadataTokens.GetToken(_typeRefMap.GetValueOrDefault(
                    MetadataTokens.TypeReferenceHandle(rowNumber),
                    MetadataTokens.TypeReferenceHandle(rowNumber))),

            TableTypeDef =>
                MetadataTokens.GetToken(_typeDefMap.GetValueOrDefault(
                    MetadataTokens.TypeDefinitionHandle(rowNumber),
                    MetadataTokens.TypeDefinitionHandle(rowNumber))),

            TableFieldDef =>
                MetadataTokens.GetToken(_fieldDefMap.GetValueOrDefault(
                    MetadataTokens.FieldDefinitionHandle(rowNumber),
                    MetadataTokens.FieldDefinitionHandle(rowNumber))),

            TableMethodDef =>
                MetadataTokens.GetToken(_methodDefMap.GetValueOrDefault(
                    MetadataTokens.MethodDefinitionHandle(rowNumber),
                    MetadataTokens.MethodDefinitionHandle(rowNumber))),

            TableMemberRef =>
                MetadataTokens.GetToken(_memberRefMap.GetValueOrDefault(
                    MetadataTokens.MemberReferenceHandle(rowNumber),
                    MetadataTokens.MemberReferenceHandle(rowNumber))),

            TableStandAloneSig =>
                MetadataTokens.GetToken(_standAloneSigMap.GetValueOrDefault(
                    MetadataTokens.StandaloneSignatureHandle(rowNumber),
                    MetadataTokens.StandaloneSignatureHandle(rowNumber))),

            TableTypeSpec =>
                MetadataTokens.GetToken(_typeSpecMap.GetValueOrDefault(
                    MetadataTokens.TypeSpecificationHandle(rowNumber),
                    MetadataTokens.TypeSpecificationHandle(rowNumber))),

            TableMethodSpec =>
                MetadataTokens.GetToken(_methodSpecMap.GetValueOrDefault(
                    MetadataTokens.MethodSpecificationHandle(rowNumber),
                    MetadataTokens.MethodSpecificationHandle(rowNumber))),

            TableUserString =>
                MetadataTokens.GetToken(_userStringMap.GetValueOrDefault(
                    MetadataTokens.UserStringHandle(rowNumber),
                    AddUserString(MetadataTokens.UserStringHandle(rowNumber)))),

            _ => token
        };
    }

    private UserStringHandle AddUserString(UserStringHandle sourceHandle)
    {
        var str = _reader.GetUserString(sourceHandle);
        var newHandle = _metadata.GetOrAddUserString(str);
        _userStringMap[sourceHandle] = newHandle;
        return newHandle;
    }
}
