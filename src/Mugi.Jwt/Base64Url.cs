namespace Mugi.Jwt;

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return string.Empty;
        }

        var base64 = Convert.ToBase64String(value);
        var length = base64.Length;
        while (length > 0 && base64[length - 1] == '=')
        {
            length--;
        }

        return string.Create(length, base64, static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = source[i] switch
                {
                    '+' => '-',
                    '/' => '_',
                    var character => character,
                };
            }
        });
    }

    internal static bool TryDecode(ReadOnlySpan<char> value, out byte[] bytes)
    {
        bytes = [];
        var remainder = value.Length & 3;
        if (remainder == 1)
        {
            return false;
        }

        var decodedLength = (value.Length >> 2) * 3 + (remainder == 0 ? 0 : remainder - 1);
        var result = GC.AllocateUninitializedArray<byte>(decodedLength);
        var sourceIndex = 0;
        var destinationIndex = 0;

        while (value.Length - sourceIndex >= 4)
        {
            var a = Decode(value[sourceIndex]);
            var b = Decode(value[sourceIndex + 1]);
            var c = Decode(value[sourceIndex + 2]);
            var d = Decode(value[sourceIndex + 3]);
            if ((a | b | c | d) < 0)
            {
                return false;
            }

            result[destinationIndex] = (byte)((a << 2) | (b >> 4));
            result[destinationIndex + 1] = (byte)((b << 4) | (c >> 2));
            result[destinationIndex + 2] = (byte)((c << 6) | d);
            sourceIndex += 4;
            destinationIndex += 3;
        }

        if (remainder == 2)
        {
            var a = Decode(value[sourceIndex]);
            var b = Decode(value[sourceIndex + 1]);
            if ((a | b) < 0 || (b & 0x0F) != 0)
            {
                return false;
            }

            result[destinationIndex] = (byte)((a << 2) | (b >> 4));
        }
        else if (remainder == 3)
        {
            var a = Decode(value[sourceIndex]);
            var b = Decode(value[sourceIndex + 1]);
            var c = Decode(value[sourceIndex + 2]);
            if ((a | b | c) < 0 || (c & 0x03) != 0)
            {
                return false;
            }

            result[destinationIndex] = (byte)((a << 2) | (b >> 4));
            result[destinationIndex + 1] = (byte)((b << 4) | (c >> 2));
        }

        bytes = result;
        return true;
    }

    private static int Decode(char value)
    {
        if (value is >= 'A' and <= 'Z')
        {
            return value - 'A';
        }

        if (value is >= 'a' and <= 'z')
        {
            return value - 'a' + 26;
        }

        if (value is >= '0' and <= '9')
        {
            return value - '0' + 52;
        }

        return value switch
        {
            '-' => 62,
            '_' => 63,
            _ => -1,
        };
    }
}
