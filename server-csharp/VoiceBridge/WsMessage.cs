namespace VoiceBridge;

/// <summary>
/// Конверт WS-протокола. Сериализуется в {"type": "...", "payload": "..."}.
/// Контракт описан в docs/PROTOCOL.md.
/// </summary>
internal sealed class WsMessage
{
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
}
