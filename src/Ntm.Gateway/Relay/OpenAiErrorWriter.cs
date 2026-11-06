using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ntm.Gateway.Relay;

/// <summary>
/// 网关自己产生的错误，按 OpenAI 的错误形状写给客户端，客户端 SDK 能直接认出来。
/// </summary>
internal static class OpenAiErrorWriter
{
    // 响应是 application/json，不会嵌进 HTML，所以不需要把中文转义成 \uXXXX。
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Body(string message, string type, string? code)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("message", message);
            writer.WriteString("type", type);
            if (code is null)
            {
                writer.WriteNull("code");
            }
            else
            {
                writer.WriteString("code", code);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>流内错误帧。响应头已经发了、状态码改不了时只能这样告诉客户端（ADR-0008）。</summary>
    public static byte[] StreamFrame(string message, string type, string? code)
    {
        byte[] body = Body(message, type, code);
        byte[] frame = new byte[6 + body.Length + 2];
        Encoding.ASCII.GetBytes("data: ").CopyTo(frame, 0);
        body.CopyTo(frame, 6);
        frame[^2] = (byte)'\n';
        frame[^1] = (byte)'\n';
        return frame;
    }
}
