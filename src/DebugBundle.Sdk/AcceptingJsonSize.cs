using System.Globalization;
using System.Text.Json;

namespace DebugBundle;

// JSON.stringify form used by the accepting contract. Actual native sender bytes
// are checked separately because its escaping and numeric form can be larger.
internal static class AcceptingJsonSize
{
    internal static int Size(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => 4,
        JsonValueKind.True => 4,
        JsonValueKind.False => 5,
        JsonValueKind.Number => Number(value.GetDouble()).Length,
        JsonValueKind.String => String(value.GetString()!),
        JsonValueKind.Array => 2 + value.EnumerateArray().Select(Size).Sum() + Math.Max(0, value.GetArrayLength() - 1),
        JsonValueKind.Object => Object(value),
        _ => throw new ArgumentException("invalid JSON")
    };
    internal static int Size(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, OwnedJson.Options));
        return Size(document.RootElement);
    }
    private static int Object(JsonElement value)
    {
        var count = 0; var size = 2;
        foreach (var property in value.EnumerateObject()) { size += String(property.Name) + 1 + Size(property.Value); count++; }
        return size + Math.Max(0, count - 1);
    }
    private static int String(string value)
    {
        var size = 2;
        for (var index = 0; index < value.Length; index++)
        {
            var code = value[index];
            if (code is '"' or '\\' or '\b' or '\t' or '\n' or '\f' or '\r') size += 2;
            else if (code < 32) size += 6;
            else if (char.IsHighSurrogate(code) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) { size += 4; index++; }
            else if (char.IsSurrogate(code)) size += 6;
            else size += code <= 127 ? 1 : code <= 2047 ? 2 : 3;
        }
        return size;
    }
    private static string Number(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("nonfinite number");
        if (number == 0) return "0";
        var sign = number < 0 ? "-" : "";
        var native = Math.Abs(number).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var pieces = native.Split('e');
        var exponent = pieces.Length == 2 ? int.Parse(pieces[1], CultureInfo.InvariantCulture) : 0;
        var dot = pieces[0].IndexOf('.');
        var point = (dot < 0 ? pieces[0].Length : dot) + exponent;
        var digits = pieces[0].Replace(".", "");
        while (digits.Length > 1 && digits[0] == '0') { digits = digits.Substring(1); point--; }
        digits = digits.TrimEnd('0');
        if (point > -6 && point <= 21)
        {
            if (point <= 0) return sign + "0." + new string('0', -point) + digits;
            if (point >= digits.Length) return sign + digits + new string('0', point - digits.Length);
            return sign + digits.Substring(0, point) + "." + digits.Substring(point);
        }
        var mantissa = digits.Length == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
        var power = point - 1;
        return sign + mantissa + "e" + (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
    }
}
