using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Normative HYJR binary segment framing (design §10.1).
/// Header 16 bytes; records length-prefixed with SHA-256; footer ENDJ with segment digest.
/// </summary>
public static class HyjrSegmentFormat
{
    public const ushort FormatVersion = 1;
    public const int HeaderSize = 16;
    public const int FooterSize = 4 + 8 + 4 + 32; // ENDJ + last_seq + record_count + sha256
    public const int DigestSize = 32;

    /// <summary>Read size for the closed-file verifier.</summary>
    internal const int ClosedFileVerifyBufferBytes = 1024 * 1024;
    /// <summary>Fixed fields after record_length, before payload: seq+class+rel+res+payload_len.</summary>
    public const int RecordFixedBodySize = 8 + 1 + 1 + 2 + 4; // 16
    public static readonly byte[] HeaderMagic = "HYJR"u8.ToArray();
    public static readonly byte[] FooterMagic = "ENDJ"u8.ToArray();

    public static void WriteHeader(Stream stream, long firstSeq, ushort flags = 0)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        HeaderMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)firstSeq);
        stream.Write(header);
    }

    public static long ReadHeader(Stream stream, out ushort version, out ushort flags)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var read = stream.Read(header);
        if (read < HeaderSize)
            throw new InvalidDataException("HYJR header truncated");
        if (!header[..4].SequenceEqual(HeaderMagic))
            throw new InvalidDataException("HYJR magic mismatch");
        version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported HYJR version {version}");
        return (long)BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
    }

    /// <summary>
    /// Write one record. Returns the total bytes written (4 + record_length).
    /// </summary>
    public static int WriteRecord(Stream stream, RuntimeEventRecord record)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(record.PayloadJson);
        return WriteRecord(stream, record.Seq, record.Class, record.Reliability, payloadBytes);
    }

    /// <summary>
    /// Write one record from a stored-payload UTF-8 span. Same frame as the
    /// string overload: record_length, 16-byte fixed body, payload, 32-byte digest.
    /// </summary>
    public static int WriteRecord(
        Stream stream,
        long seq,
        EventClass cls,
        EventReliability reliability,
        ReadOnlySpan<byte> storedPayloadUtf8)
    {
        var payloadLen = storedPayloadUtf8.Length;
        var bodyLen = RecordFixedBodySize + payloadLen;
        var recordLength = bodyLen + DigestSize;
        var total = 4 + recordLength;
        // Build the frame before touching the stream so a hash or allocation
        // failure cannot leave a length prefix behind.
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var frame = rented.AsSpan(0, total);
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)recordLength);
            var body = frame.Slice(4, bodyLen);
            BinaryPrimitives.WriteUInt64LittleEndian(body, (ulong)seq);
            body[8] = (byte)cls;
            body[9] = (byte)reliability;
            BinaryPrimitives.WriteUInt16LittleEndian(body[10..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(body[12..], (uint)payloadLen);
            storedPayloadUtf8.CopyTo(body[RecordFixedBodySize..]);
            var digest = frame.Slice(4 + bodyLen, DigestSize);
            if (!SHA256.TryHashData(body, digest, out var hashed) || hashed != DigestSize)
                throw new CryptographicException("SHA-256 digest failed");
            stream.Write(frame);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Attempt to read the next complete record at the current position.
    /// Returns false if EOF or incomplete tail (caller should truncate).
    /// </summary>
    public static bool TryReadRecord(Stream stream, out RuntimeEventRecord? record, out string? error)
    {
        record = null;
        error = null;
        var startPos = stream.Position;
        Span<byte> lengthBuf = stackalloc byte[4];
        var n = stream.Read(lengthBuf);
        if (n == 0)
            return false; // clean EOF (before footer or end of records)
        if (n < 4)
        {
            error = "incomplete record_length";
            stream.Position = startPos;
            return false;
        }

        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuf);
        if (recordLength < DigestSize + RecordFixedBodySize)
        {
            error = "record_length too small";
            stream.Position = startPos;
            return false;
        }

        // Check remaining bytes available (may be incomplete tail).
        var remaining = stream.Length - stream.Position;
        if (remaining < recordLength)
        {
            error = "incomplete record body";
            stream.Position = startPos;
            return false;
        }

        var bodyAndDigest = new byte[recordLength];
        var read = stream.Read(bodyAndDigest);
        if (read < recordLength)
        {
            error = "incomplete record read";
            stream.Position = startPos;
            return false;
        }

        var bodyLen = (int)recordLength - DigestSize;
        var body = bodyAndDigest.AsSpan(0, bodyLen);
        var storedDigest = bodyAndDigest.AsSpan(bodyLen, DigestSize);
        var computed = SHA256.HashData(body);
        if (!storedDigest.SequenceEqual(computed))
        {
            error = "record digest mismatch";
            stream.Position = startPos;
            return false;
        }

        var seq = (long)BinaryPrimitives.ReadUInt64LittleEndian(body);
        var cls = (EventClass)body[8];
        var rel = (EventReliability)body[9];
        var payloadLen = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        if (payloadLen > bodyLen - RecordFixedBodySize)
        {
            error = "payload_length exceeds body";
            stream.Position = startPos;
            return false;
        }

        var payloadJson = Encoding.UTF8.GetString(body.Slice(RecordFixedBodySize, (int)payloadLen));
        // Type/occurred_at wrap is source-generated STJ (AOT-safe).
        if (!HyjrPayloadJson.TryUnwrapStoredPayload(
                payloadJson, out var type, out var occurredAt, out var innerPayload))
        {
            type = "unknown";
            occurredAt = DateTimeOffset.UnixEpoch;
            innerPayload = "{}";
        }

        record = new RuntimeEventRecord
        {
            Seq = seq,
            Class = cls,
            Reliability = rel,
            Type = type,
            OccurredAt = occurredAt,
            PayloadJson = innerPayload,
        };
        return true;
    }

    public static void WriteFooter(Stream stream, long lastSeq, int recordCount, ReadOnlySpan<byte> headerAndRecords)
    {
        var digest = SHA256.HashData(headerAndRecords);
        Span<byte> footer = stackalloc byte[FooterSize];
        FooterMagic.CopyTo(footer);
        BinaryPrimitives.WriteUInt64LittleEndian(footer[4..], (ulong)lastSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[12..], (uint)recordCount);
        digest.CopyTo(footer[16..]);
        stream.Write(footer);
    }

    /// <summary>
    /// Validate footer at the given position. Returns false on missing/mismatch.
    /// </summary>
    public static bool TryReadFooter(
        Stream stream,
        long footerPosition,
        ReadOnlySpan<byte> headerAndRecords,
        out long lastSeq,
        out int recordCount,
        out string? error)
    {
        lastSeq = 0;
        recordCount = 0;
        error = null;
        if (footerPosition + FooterSize > stream.Length)
        {
            error = "footer truncated or missing";
            return false;
        }

        stream.Position = footerPosition;
        Span<byte> footer = stackalloc byte[FooterSize];
        if (stream.Read(footer) < FooterSize)
        {
            error = "footer read incomplete";
            return false;
        }

        if (!footer[..4].SequenceEqual(FooterMagic))
        {
            error = "footer magic mismatch";
            return false;
        }

        lastSeq = (long)BinaryPrimitives.ReadUInt64LittleEndian(footer[4..]);
        recordCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
        var stored = footer[16..];
        var computed = SHA256.HashData(headerAndRecords);
        if (!stored.SequenceEqual(computed))
        {
            error = "footer digest mismatch";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Read-only scan of a segment: verify digests, return records up to the last complete
    /// record (and optional valid footer). Never truncates or opens for write.
    /// Use for subscribe replay / <c>ReadRangeAsync</c>.
    /// </summary>
    public static HyjrRecoveryResult ScanFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Journal segment not found", path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ScanStream(fs, mutate: false);
    }

    /// <summary>
    /// Recover a segment file: verify records, truncate incomplete tail, validate footer.
    /// Returns recovery outcome without inventing events. Startup / <c>RecoverAsync</c> only.
    /// </summary>
    public static HyjrRecoveryResult RecoverFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Journal segment not found", path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return ScanStream(fs, mutate: true);
    }

    /// <summary>
    /// Shared scan logic. When <paramref name="mutate"/> is true, truncates incomplete tails
    /// and trailing garbage (recovery). When false, never changes file length or contents.
    /// </summary>
    private static HyjrRecoveryResult ScanStream(FileStream fs, bool mutate)
    {
        if (fs.Length == 0)
        {
            return new HyjrRecoveryResult
            {
                FirstSeq = 0,
                LastValidSeq = null,
                RecordCount = 0,
                Closed = false,
                Truncated = false,
                ReplayComplete = false,
                ReplayError = "empty segment",
                Bytes = 0,
            };
        }

        long firstSeq;
        try
        {
            firstSeq = ReadHeader(fs, out _, out _);
        }
        catch (Exception ex)
        {
            return new HyjrRecoveryResult
            {
                FirstSeq = 0,
                LastValidSeq = null,
                RecordCount = 0,
                Closed = false,
                Truncated = false,
                ReplayComplete = false,
                ReplayError = "header invalid: " + ex.Message,
                Bytes = fs.Length,
            };
        }

        var records = new List<RuntimeEventRecord>();
        long? lastValidSeq = null;
        long lastCompleteEnd = HeaderSize;
        string? tailError = null;
        Span<byte> magicPeek = stackalloc byte[4];

        while (fs.Position < fs.Length)
        {
            // Peek for footer magic.
            if (fs.Length - fs.Position >= 4)
            {
                var peekPos = fs.Position;
                fs.ReadExactly(magicPeek);
                fs.Position = peekPos;
                if (magicPeek.SequenceEqual(FooterMagic))
                    break;
            }

            if (!TryReadRecord(fs, out var rec, out var err))
            {
                if (err is not null)
                    tailError = err;
                break;
            }

            records.Add(rec!);
            lastValidSeq = rec!.Seq;
            lastCompleteEnd = fs.Position;
        }

        var truncated = false;
        if (lastCompleteEnd < fs.Length)
        {
            // Check if remaining is a valid footer.
            fs.Position = 0;
            var headerAndRecords = new byte[lastCompleteEnd];
            fs.ReadExactly(headerAndRecords);

            if (TryReadFooter(fs, lastCompleteEnd, headerAndRecords, out var footLast, out var footCount, out var footErr)
                && footCount == records.Count
                && (records.Count == 0 || footLast == lastValidSeq))
            {
                // Clean closed segment.
                var expectedEnd = lastCompleteEnd + FooterSize;
                if (mutate && fs.Length > expectedEnd)
                {
                    fs.SetLength(expectedEnd);
                    truncated = true;
                }

                return new HyjrRecoveryResult
                {
                    FirstSeq = firstSeq,
                    LastValidSeq = lastValidSeq,
                    RecordCount = records.Count,
                    Closed = true,
                    Truncated = truncated,
                    ReplayComplete = true,
                    ReplayError = null,
                    Bytes = expectedEnd,
                    Records = records,
                };
            }

            // Incomplete/invalid tail or missing footer.
            if (mutate && fs.Length != lastCompleteEnd)
            {
                fs.SetLength(lastCompleteEnd);
                truncated = true;
            }

            return new HyjrRecoveryResult
            {
                FirstSeq = firstSeq,
                LastValidSeq = lastValidSeq,
                RecordCount = records.Count,
                Closed = false,
                Truncated = truncated,
                ReplayComplete = false,
                ReplayError = tailError ?? footErr ?? "missing footer",
                Bytes = lastCompleteEnd,
                Records = records,
            };
        }

        // Position at end with no footer.
        return new HyjrRecoveryResult
        {
            FirstSeq = firstSeq,
            LastValidSeq = lastValidSeq,
            RecordCount = records.Count,
            Closed = false,
            Truncated = false,
            ReplayComplete = false,
            ReplayError = "missing footer",
            Bytes = fs.Length,
            Records = records,
        };
    }

    /// <summary>SHA-256 hex (lowercase) of the entire file.</summary>
    public static string ComputeFileChecksum(string path)
    {
        using var fs = File.OpenRead(path);
        var hash = SHA256.HashData(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Read-only verify of a closed segment. One pass hashes the file and checks
    /// that record framing agrees with the footer counts (design §10.1).
    /// Does not verify per-record digests or decode JSON. Never writes the file.
    /// Returns false on any mismatch or error.
    /// </summary>
    public static bool TryVerifyClosedFile(
        string path,
        string expectedChecksumSha256,
        long expectedByteCount,
        out HyjrRecoveryResult result)
    {
        result = new HyjrRecoveryResult
        {
            FirstSeq = 0,
            LastValidSeq = null,
            RecordCount = 0,
            Closed = false,
            Truncated = false,
            ReplayComplete = false,
            ReplayError = null,
            Bytes = 0,
        };

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = fs.Length;
            // A closed manifest row with no positive byte count is not fast-path eligible.
            if (expectedByteCount <= 0 || length != expectedByteCount)
                return false;
            if (length < HeaderSize + FooterSize)
                return false;

            if (!TryHashAndMatchFooter(fs, length, expectedChecksumSha256, out var firstSeq, out var lastSeq, out var recordCount))
                return false;

            result = new HyjrRecoveryResult
            {
                FirstSeq = firstSeq,
                LastValidSeq = recordCount == 0 ? null : lastSeq,
                RecordCount = recordCount,
                Closed = true,
                Truncated = false,
                ReplayComplete = true,
                ReplayError = null,
                Bytes = length,
                Records = [],
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Hash the file once, hash the header and records in that same pass, and
    /// walk record length/seq fields up to the footer.
    /// </summary>
    private static bool TryHashAndMatchFooter(
        FileStream fs,
        long length,
        string expectedChecksumSha256,
        out long firstSeq,
        out long lastSeq,
        out int recordCount)
    {
        firstSeq = 0;
        lastSeq = 0;
        recordCount = 0;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var prefixHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var rented = ArrayPool<byte>.Shared.Rent(ClosedFileVerifyBufferBytes);
        try
        {
            var footerAt = length - FooterSize;
            var phase = VerifyPhase.Header;
            var need = HeaderSize;
            var have = 0;
            Span<byte> staging = stackalloc byte[FooterSize];
            Span<byte> prefixDigest = stackalloc byte[DigestSize];
            long skipRemaining = 0;
            long logical = 0;
            uint recordLength = 0;
            long? previousSeq = null;
            var footerAccepted = false;
            var pending = 0;

            while (true)
            {
                var n = fs.Read(rented, pending, rented.Length - pending);
                if (n > 0)
                {
                    var chunk = rented.AsSpan(pending, n);
                    hasher.AppendData(chunk);
                    var start = fs.Position - n;
                    if (start < footerAt)
                    {
                        var prefixCount = (int)Math.Min(n, footerAt - start);
                        prefixHasher.AppendData(chunk[..prefixCount]);
                    }
                }
                var available = pending + n;
                if (available == 0)
                    break;

                var consumed = 0;
                var failed = false;
                while (consumed < available && !failed)
                {
                    if (phase == VerifyPhase.Skip)
                    {
                        var take = (int)Math.Min(skipRemaining, available - consumed);
                        consumed += take;
                        skipRemaining -= take;
                        logical += take;
                        if (skipRemaining == 0)
                            phase = logical == footerAt ? VerifyPhase.Footer : VerifyPhase.RecordLength;
                        if (phase == VerifyPhase.Footer)
                            need = FooterSize;
                        else if (phase == VerifyPhase.RecordLength)
                            need = 4;
                        have = 0;
                        continue;
                    }

                    if (footerAccepted)
                    {
                        failed = true;
                        break;
                    }

                    var takeField = Math.Min(need - have, available - consumed);
                    rented.AsSpan(consumed, takeField).CopyTo(staging[have..]);
                    consumed += takeField;
                    have += takeField;
                    logical += takeField;
                    if (have < need)
                        break;

                    have = 0;
                    switch (phase)
                    {
                        case VerifyPhase.Header:
                            if (!staging[..4].SequenceEqual(HeaderMagic))
                            {
                                failed = true;
                                break;
                            }

                            var version = BinaryPrimitives.ReadUInt16LittleEndian(staging[4..]);
                            if (version != FormatVersion)
                            {
                                failed = true;
                                break;
                            }

                            firstSeq = (long)BinaryPrimitives.ReadUInt64LittleEndian(staging[8..]);
                            // A high-bit sequence is not valid. Leave it to the full scan.
                            if (firstSeq < 0)
                            {
                                failed = true;
                                break;
                            }

                            phase = logical == footerAt ? VerifyPhase.Footer : VerifyPhase.RecordLength;
                            need = phase == VerifyPhase.Footer ? FooterSize : 4;
                            break;

                        case VerifyPhase.RecordLength:
                            recordLength = BinaryPrimitives.ReadUInt32LittleEndian(staging);
                            if (recordLength < DigestSize + RecordFixedBodySize
                                || logical + recordLength > footerAt)
                            {
                                failed = true;
                                break;
                            }

                            phase = VerifyPhase.RecordSeq;
                            need = 8;
                            break;

                        case VerifyPhase.RecordSeq:
                            var seq = (long)BinaryPrimitives.ReadUInt64LittleEndian(staging);
                            if (seq < 0)
                            {
                                failed = true;
                                break;
                            }

                            if (recordCount == 0)
                            {
                                if (seq < firstSeq)
                                {
                                    failed = true;
                                    break;
                                }
                            }
                            else if (previousSeq is { } prior && seq <= prior)
                            {
                                failed = true;
                                break;
                            }

                            previousSeq = seq;
                            lastSeq = seq;
                            recordCount++;
                            skipRemaining = recordLength - 8;
                            phase = skipRemaining == 0
                                ? logical == footerAt ? VerifyPhase.Footer : VerifyPhase.RecordLength
                                : VerifyPhase.Skip;
                            if (phase == VerifyPhase.Footer)
                                need = FooterSize;
                            else if (phase == VerifyPhase.RecordLength)
                                need = 4;
                            break;

                        case VerifyPhase.Footer:
                            if (!staging[..4].SequenceEqual(FooterMagic))
                            {
                                failed = true;
                                break;
                            }

                            var footLast = (long)BinaryPrimitives.ReadUInt64LittleEndian(staging[4..]);
                            var footCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(staging[12..]);
                            if (footCount != recordCount)
                            {
                                failed = true;
                                break;
                            }

                            if (recordCount > 0 && footLast != lastSeq)
                            {
                                failed = true;
                                break;
                            }

                            if (!prefixHasher.TryGetHashAndReset(prefixDigest, out var hashed)
                                || hashed != DigestSize
                                || !staging.Slice(16, DigestSize).SequenceEqual(prefixDigest))
                            {
                                failed = true;
                                break;
                            }

                            footerAccepted = true;
                            break;
                    }
                }

                if (failed)
                    return false;

                pending = available - consumed;
                if (pending > 0)
                    Buffer.BlockCopy(rented, consumed, rented, 0, pending);
                if (n == 0)
                    break;
            }

            if (!footerAccepted || logical != length)
                return false;

            var checksum = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(checksum, expectedChecksumSha256, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private enum VerifyPhase
    {
        Header,
        RecordLength,
        RecordSeq,
        Skip,
        Footer,
    }

    /// <summary>Wrap type/occurred_at/payload for storage in the HYJR payload field.</summary>
    public static string WrapStoredPayload(string type, DateTimeOffset occurredAt, string payloadJson) =>
        HyjrPayloadJson.WrapStoredPayload(type, occurredAt, payloadJson);
}

/// <summary>Outcome of recovering a single HYJR segment file.</summary>
public sealed record HyjrRecoveryResult
{
    public required long FirstSeq { get; init; }
    public long? LastValidSeq { get; init; }
    public required int RecordCount { get; init; }
    public required bool Closed { get; init; }
    public required bool Truncated { get; init; }
    public required bool ReplayComplete { get; init; }
    public string? ReplayError { get; init; }
    public required long Bytes { get; init; }
    public IReadOnlyList<RuntimeEventRecord> Records { get; init; } = [];
}
