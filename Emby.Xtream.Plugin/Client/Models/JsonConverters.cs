using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emby.Xtream.Plugin.Client.Models
{
    /// <summary>
    /// Reads a JSON 0/1 integer (or "0"/"1" string) as a bool.
    /// Xtream API fields such as <c>tv_archive</c>, <c>is_adult</c>, and <c>has_archive</c>
    /// are documented as booleans but are transmitted as integers.
    /// </summary>
    internal sealed class IntAsBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetInt32() != 0;
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                return s != "0" && !string.IsNullOrEmpty(s);
            }
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;
            if (reader.TokenType == JsonTokenType.Null) return false;
            throw new JsonException($"Unexpected token {reader.TokenType} for bool field.");
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }

    /// <summary>
    /// Reads a JSON value that may be a quoted string ("1609459200") or a bare number (1609459200)
    /// and always surfaces it as a <see cref="long"/>.
    /// Xtream API fields such as <c>last_modified</c> are Unix timestamps that some providers
    /// transmit as strings and others transmit as bare integers.
    /// </summary>
    internal sealed class StringOrNumberAsLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.GetInt64();
            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                if (string.IsNullOrEmpty(s)) return 0L;
                return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                    ? result
                    : 0L;
            }
            if (reader.TokenType == JsonTokenType.Null) return 0L;
            throw new JsonException($"Unexpected token {reader.TokenType} for long field.");
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    /// <summary>
    /// Reads a JSON value that may be a string ("5.1", "stereo") or a bare number (2, 6)
    /// and always surfaces it as a string. Dispatcharr versions differ in which they emit.
    /// </summary>
    internal sealed class StringOrNumberAsStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
                return reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString();
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();
            if (reader.TokenType == JsonTokenType.Null)
                return string.Empty;
            throw new JsonException($"Unexpected token {reader.TokenType} for string field.");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }

    /// <summary>
    /// Reads a JSON value that may be a number, numeric string, or a non-numeric token such as
    /// "all" and surfaces it as <see cref="int?"/>. Non-numeric values map to null.
    /// </summary>
    internal sealed class StringOrNumberAsNullableIntConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.GetInt32();
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    return null;
                }

                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return null;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            throw new JsonException($"Unexpected token {reader.TokenType} for nullable int field.");
        }

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
            {
                writer.WriteNumberValue(value.Value);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
