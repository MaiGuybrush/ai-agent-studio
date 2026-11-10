using System.Text.Encodings.Web;
using System.Text.Json;

namespace AiAgentStudio.Api;

/// <summary>共用的 JSON 序列化選項：中文不轉成 \uXXXX（存進 DB 的 JSON 保持可讀）。</summary>
public static class JsonOpts
{
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
