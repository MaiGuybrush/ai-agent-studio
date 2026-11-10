using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAgentStudio.Api
{
    public class TrimConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value != null)
            {
                return value.Trim();
            }
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value.Trim());
            }
        }
    }
}
