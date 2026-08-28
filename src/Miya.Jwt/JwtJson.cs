using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Miya.Json;

namespace Miya.Jwt;

internal static class JwtJson
{
    internal static byte[] WriteHeader(JwtAlgorithm algorithm) => algorithm switch
    {
        JwtAlgorithm.HS256 => "{\"alg\":\"HS256\",\"typ\":\"JWT\"}"u8.ToArray(),
        JwtAlgorithm.RS256 => "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8.ToArray(),
        _ => "{\"alg\":\"ES256\",\"typ\":\"JWT\"}"u8.ToArray(),
    };

    internal static byte[] WritePayload(JwtPayload payload)
    {
        var destination = new ArrayBufferWriter<byte>();
        var writer = new JsonWriter(destination, JsonOptions.Default);
        var memberCount = CountMembers(payload);
        writer.EnterContainer(memberCount);
        writer.WriteRaw("{"u8);

        var wroteMember = false;
        if (payload.Subject is not null)
        {
            WriteProperty(ref writer, "sub", payload.Subject, ref wroteMember);
        }

        if (payload.Issuer is not null)
        {
            WriteProperty(ref writer, "iss", payload.Issuer, ref wroteMember);
        }

        if (payload.Audience is not null)
        {
            WriteProperty(ref writer, "aud", payload.Audience, ref wroteMember);
        }
        else if (payload.AudienceValues is not null)
        {
            WritePropertyName(ref writer, "aud", ref wroteMember);
            WriteStringArray(ref writer, payload.AudienceValues);
        }

        if (payload.ExpiresAt is { } expiresAt)
        {
            WriteProperty(ref writer, "exp", expiresAt.ToUnixTimeSeconds(), ref wroteMember);
        }

        if (payload.NotBefore is { } notBefore)
        {
            WriteProperty(ref writer, "nbf", notBefore.ToUnixTimeSeconds(), ref wroteMember);
        }

        if (payload.IssuedAt is { } issuedAt)
        {
            WriteProperty(ref writer, "iat", issuedAt.ToUnixTimeSeconds(), ref wroteMember);
        }

        if (payload.TokenId is not null)
        {
            WriteProperty(ref writer, "jti", payload.TokenId, ref wroteMember);
        }

        var claims = payload.Claims;
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            WritePropertyName(ref writer, claim.Name, ref wroteMember);
            switch (claim.Kind)
            {
                case JwtClaimKind.String:
                    writer.WriteString(claim.StringValue);
                    break;
                case JwtClaimKind.Int64:
                    writer.WriteNumber(claim.Int64Value);
                    break;
                default:
                    writer.WriteBool(claim.BoolValue);
                    break;
            }
        }

        writer.WriteRaw("}"u8);
        writer.ExitContainer();
        writer.Flush();
        return destination.WrittenSpan.ToArray();
    }

    internal static JwtHeader ReadHeader(ReadOnlySpan<byte> json)
    {
        var reader = new JsonReader(json, JsonOptions.Default);
        try
        {
            reader.ReadBeginObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? algorithm = null;
            var algorithmPresent = false;
            var unsupported = false;

            while (!reader.TryReadEndObject())
            {
                var name = Encoding.UTF8.GetString(reader.ReadPropertyName());
                if (!names.Add(name))
                {
                    throw InputError("The JOSE header contains a duplicate member.");
                }

                switch (name)
                {
                    case "alg":
                        algorithmPresent = true;
                        if (reader.PeekValueKind() == JsonTokenKind.String)
                        {
                            algorithm = reader.ReadString();
                        }
                        else
                        {
                            reader.SkipValue();
                        }

                        break;
                    case "typ":
                        if (reader.PeekValueKind() == JsonTokenKind.String)
                        {
                            var type = reader.ReadString();
                            if (!string.Equals(type, "JWT", StringComparison.OrdinalIgnoreCase))
                            {
                                unsupported = true;
                            }
                        }
                        else
                        {
                            reader.SkipValue();
                            unsupported = true;
                        }

                        break;
                    case "crit":
                        reader.SkipValue();
                        unsupported = true;
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }

            reader.ExpectEnd();
            return new JwtHeader(algorithmPresent ? algorithm : null, unsupported);
        }
        finally
        {
            reader.Dispose();
        }
    }

    internal static JwtPayload ReadPayload(ReadOnlySpan<byte> json)
    {
        var reader = new JsonReader(json, JsonOptions.Default);
        try
        {
            reader.ReadBeginObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var claims = new List<JwtClaim>();
            string? subject = null;
            string? issuer = null;
            string? audience = null;
            string[]? audienceValues = null;
            string? tokenId = null;
            DateTimeOffset? expiresAt = null;
            DateTimeOffset? notBefore = null;
            DateTimeOffset? issuedAt = null;
            double? expiresAtNumber = null;
            double? notBeforeNumber = null;
            double? issuedAtNumber = null;

            while (!reader.TryReadEndObject())
            {
                var name = Encoding.UTF8.GetString(reader.ReadPropertyName());
                if (!names.Add(name))
                {
                    throw InputError("The JWT payload contains a duplicate claim.");
                }

                switch (name)
                {
                    case "sub":
                        subject = ReadRequiredString(ref reader, name);
                        break;
                    case "iss":
                        issuer = ReadRequiredString(ref reader, name);
                        break;
                    case "aud":
                        ReadAudience(ref reader, out audience, out audienceValues);
                        break;
                    case "exp":
                        expiresAtNumber = ReadNumericDate(ref reader, name, out var parsedExpiresAt);
                        expiresAt = parsedExpiresAt;
                        break;
                    case "nbf":
                        notBeforeNumber = ReadNumericDate(ref reader, name, out var parsedNotBefore);
                        notBefore = parsedNotBefore;
                        break;
                    case "iat":
                        issuedAtNumber = ReadNumericDate(ref reader, name, out var parsedIssuedAt);
                        issuedAt = parsedIssuedAt;
                        break;
                    case "jti":
                        tokenId = ReadRequiredString(ref reader, name);
                        break;
                    default:
                        ReadCustomClaim(ref reader, name, claims);
                        break;
                }
            }

            reader.ExpectEnd();
            var payload = new JwtPayload
            {
                Subject = subject,
                Issuer = issuer,
                Audience = audience,
                AudienceValues = audienceValues,
                ExpiresAt = expiresAt,
                ExpiresAtNumber = expiresAtNumber,
                NotBefore = notBefore,
                NotBeforeNumber = notBeforeNumber,
                IssuedAt = issuedAt,
                IssuedAtNumber = issuedAtNumber,
                TokenId = tokenId,
            };

            for (var i = 0; i < claims.Count; i++)
            {
                payload.AddParsedClaim(claims[i]);
            }

            return payload;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static int CountMembers(JwtPayload payload)
    {
        var count = payload.Claims.Count;
        count += payload.Subject is null ? 0 : 1;
        count += payload.Issuer is null ? 0 : 1;
        count += payload.Audience is null && payload.AudienceValues is null ? 0 : 1;
        count += payload.ExpiresAt is null ? 0 : 1;
        count += payload.NotBefore is null ? 0 : 1;
        count += payload.IssuedAt is null ? 0 : 1;
        count += payload.TokenId is null ? 0 : 1;
        return count;
    }

    private static void WriteProperty(
        ref JsonWriter writer,
        string name,
        string value,
        ref bool wroteMember)
    {
        WritePropertyName(ref writer, name, ref wroteMember);
        writer.WriteString(value);
    }

    private static void WriteProperty(
        ref JsonWriter writer,
        string name,
        long value,
        ref bool wroteMember)
    {
        WritePropertyName(ref writer, name, ref wroteMember);
        writer.WriteNumber(value);
    }

    private static void WritePropertyName(ref JsonWriter writer, string name, ref bool wroteMember)
    {
        if (wroteMember)
        {
            writer.WriteRaw(","u8);
        }

        writer.WriteString(name);
        writer.WriteRaw(":"u8);
        wroteMember = true;
    }

    private static void WriteStringArray(ref JsonWriter writer, string[] values)
    {
        writer.EnterContainer(values.Length);
        writer.WriteRaw("["u8);
        for (var i = 0; i < values.Length; i++)
        {
            if (i != 0)
            {
                writer.WriteRaw(","u8);
            }

            writer.WriteString(values[i]);
        }

        writer.WriteRaw("]"u8);
        writer.ExitContainer();
    }

    private static string ReadRequiredString(ref JsonReader reader, string name)
    {
        if (reader.PeekValueKind() != JsonTokenKind.String)
        {
            reader.SkipValue();
            throw InputError($"The '{name}' claim must be a string.");
        }

        return reader.ReadString()!;
    }

    private static void ReadAudience(
        ref JsonReader reader,
        out string? audience,
        out string[]? audienceValues)
    {
        audience = null;
        audienceValues = null;
        if (reader.PeekValueKind() == JsonTokenKind.String)
        {
            audience = reader.ReadString();
            return;
        }

        if (reader.PeekValueKind() != JsonTokenKind.Array)
        {
            reader.SkipValue();
            throw InputError("The 'aud' claim must be a string or an array of strings.");
        }

        var values = new List<string>();
        reader.ReadBeginArray();
        while (!reader.TryReadEndArray())
        {
            if (reader.PeekValueKind() != JsonTokenKind.String)
            {
                reader.SkipValue();
                throw InputError("The 'aud' claim must contain only strings.");
            }

            values.Add(reader.ReadString()!);
        }

        audienceValues = [.. values];
    }

    private static double ReadNumericDate(
        ref JsonReader reader,
        string name,
        out DateTimeOffset date)
    {
        if (reader.PeekValueKind() != JsonTokenKind.Number)
        {
            reader.SkipValue();
            throw InputError($"The '{name}' claim must be a JSON number.");
        }

        var token = reader.ReadNumberBytes();
        if (!Utf8Parser.TryParse(token, out double seconds, out var consumed)
            || consumed != token.Length
            || !double.IsFinite(seconds))
        {
            throw InputError($"The '{name}' claim is outside the supported NumericDate range.");
        }

        try
        {
            date = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
            return seconds;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException(
                $"The '{name}' claim is outside the supported NumericDate range.",
                exception,
                isInputError: true);
        }
    }

    private static void ReadCustomClaim(
        ref JsonReader reader,
        string name,
        List<JwtClaim> claims)
    {
        switch (reader.PeekValueKind())
        {
            case JsonTokenKind.String:
                claims.Add(JwtClaim.String(name, reader.ReadString()!));
                break;
            case JsonTokenKind.Number:
                var token = reader.ReadNumberBytes();
                if (Utf8Parser.TryParse(token, out long integer, out var integerConsumed)
                    && integerConsumed == token.Length)
                {
                    claims.Add(JwtClaim.Int64(name, integer));
                }
                else if (!Utf8Parser.TryParse(token, out double number, out var numberConsumed)
                    || numberConsumed != token.Length
                    || !double.IsFinite(number))
                {
                    throw InputError($"The '{name}' claim contains an invalid number.");
                }

                break;
            case JsonTokenKind.True:
            case JsonTokenKind.False:
                claims.Add(JwtClaim.Bool(name, reader.ReadBool()));
                break;
            default:
                reader.SkipValue();
                break;
        }
    }

    private static JsonException InputError(string message) => new(message, isInputError: true);
}

internal readonly record struct JwtHeader(string? Algorithm, bool Unsupported);
