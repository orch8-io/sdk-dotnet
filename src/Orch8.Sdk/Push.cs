using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orch8.Sdk;

/// <summary>
/// Verifies push-dispatch signatures (WORKER_PROTOCOL.md §8.2):
/// <c>X-Orch8-Signature: sha256=hex(HMAC-SHA256(secret, "&lt;X-Orch8-Timestamp&gt;." + rawBody))</c>.
/// </summary>
public static class Orch8PushVerifier
{
    /// <summary>Timestamp header name.</summary>
    public const string TimestampHeader = "X-Orch8-Timestamp";

    /// <summary>Signature header name.</summary>
    public const string SignatureHeader = "X-Orch8-Signature";

    /// <summary>Default replay window (300 s, both directions).</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

    private const string Prefix = "sha256=";

    /// <summary>
    /// Returns true only when <paramref name="signatureHeader"/> is <c>sha256=</c> + 64 hex digits matching
    /// the HMAC of <paramref name="timestampHeader"/> + "." + <paramref name="rawBody"/> (constant-time
    /// compare) and the integer timestamp is within <paramref name="tolerance"/> of <paramref name="now"/>.
    /// Always verify the exact raw request bytes, never re-serialized JSON.
    /// </summary>
    /// <exception cref="ArgumentException">The secret is null or empty (a configuration error).</exception>
    public static bool Verify(string secret, string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> rawBody,
        DateTimeOffset? now = null, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("push secret must not be empty", nameof(secret));
        if (!TryParseTimestamp(timestampHeader, out var ts)) return false;
        if (signatureHeader is null || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var hex = signatureHeader.AsSpan(Prefix.Length);
        if (hex.Length != 64) return false;
        Span<byte> provided = stackalloc byte[32];
        for (var i = 0; i < 32; i++)
        {
            var hi = HexValue(hex[2 * i]);
            var lo = HexValue(hex[2 * i + 1]);
            if (hi < 0 || lo < 0) return false;
            provided[i] = (byte)((hi << 4) | lo);
        }

        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var window = (long)Math.Floor((tolerance ?? DefaultTolerance).TotalSeconds);
        // |current - ts| > window, without overflow for extreme timestamps
        var diff = (decimal)current - ts;
        if (Math.Abs(diff) > window) return false;

        Span<byte> expected = stackalloc byte[32];
        Compute(secret, timestampHeader!, rawBody, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>Produces the <c>sha256=&lt;hex&gt;</c> header value for a timestamp and body (for tests and tooling).</summary>
    public static string Sign(string secret, string timestamp, ReadOnlySpan<byte> rawBody)
    {
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("push secret must not be empty", nameof(secret));
        Span<byte> mac = stackalloc byte[32];
        Compute(secret, timestamp, rawBody, mac);
        return Prefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static void Compute(string secret, string timestamp, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret));
        hmac.AppendData(Encoding.UTF8.GetBytes(timestamp + "."));
        hmac.AppendData(body);
        hmac.GetHashAndReset(destination);
    }

    private static bool TryParseTimestamp(string? value, out long ts)
    {
        ts = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var start = value[0] == '-' ? 1 : 0;
        if (start == value.Length) return false;
        for (var i = start; i < value.Length; i++)
            if (value[i] is < '0' or > '9') return false;
        return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ts);
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>
/// Framework-agnostic push receiver: verifies a push request, then (asynchronously) claims via
/// <c>POST /workers/tasks/poll/queue</c> and runs the claimed task on an <see cref="Orch8Worker"/>
/// (a push is only a wake-up, §8.3 D2). Wire it into ASP.NET Core, HttpListener, a Lambda, etc.
/// </summary>
public sealed class Orch8PushReceiver
{
    private readonly Orch8Worker _worker;
    private readonly string _secret;
    private readonly TimeSpan _tolerance;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;

    /// <summary>Creates a receiver.</summary>
    /// <param name="worker">Worker with the handlers registered (it need not be polling).</param>
    /// <param name="secret">The queue's push secret (non-empty).</param>
    /// <param name="tolerance">Timestamp tolerance (default 300 s).</param>
    /// <param name="clock">Clock override for tests.</param>
    public Orch8PushReceiver(Orch8Worker worker, string secret, TimeSpan? tolerance = null, Func<DateTimeOffset>? clock = null)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("push secret must not be empty", nameof(secret));
        _secret = secret;
        _tolerance = tolerance ?? Orch8PushVerifier.DefaultTolerance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = worker.Options.Logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Handles one push request and returns the HTTP status to answer with: <c>401</c> for a bad
    /// signature (no work is triggered), <c>400</c> for a malformed envelope, otherwise <c>202</c>
    /// with the claim started in the background.
    /// </summary>
    public int Handle(string? timestampHeader, string? signatureHeader, ReadOnlySpan<byte> rawBody)
    {
        if (!Orch8PushVerifier.Verify(_secret, timestampHeader, signatureHeader, rawBody, _clock(), _tolerance)) return 401;
        PushEnvelope envelope;
        try
        {
            envelope = PushEnvelope.Parse(rawBody);
        }
        catch (JsonException)
        {
            return 400;
        }
        if (string.IsNullOrEmpty(envelope.HandlerName)) return 400;
        _ = Task.Run(() => ClaimAsync(envelope));
        return 202;
    }

    /// <summary>Claims (limit 1) for the envelope's handler/queue and runs the claimed task.</summary>
    public async Task<int> ClaimAsync(PushEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_worker.HasHandler(envelope.HandlerName))
        {
            _log.LogWarning("orch8 push for unregistered handler {Handler}; not claiming", envelope.HandlerName);
            return 0;
        }
        try
        {
            return await _worker.ClaimAsync(envelope.HandlerName, envelope.QueueName, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "orch8 push claim for {Handler} on {Queue} failed; task stays pending for pollers", envelope.HandlerName, envelope.QueueName);
            return 0;
        }
    }
}
