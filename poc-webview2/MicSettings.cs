using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebView2Poc;

/// <summary>
/// Настройки усиления микрофона — персист рядом с профилем (data/mic.json).
/// Читаются при старте, вшиваются в дефолты MicBoostScript, меняются из тулбара.
/// </summary>
internal sealed class MicSettings
{
    public bool Sound { get; set; } = true;       // пики программы (пункт «Звук» в меню трея, 22.11.2026); --no-beep глушит на старте
    public bool Enabled { get; set; } = true;    // включён ли перехват+усиление
    public double Gain { get; set; } = 2.0;       // множитель усиления (диапазон ползунка 1.0–4.0)
    public bool Noise { get; set; } = false;      // шумоподавление Chromium: по умолч. ВЫКЛ — на тихом
                                                  // микрофоне (AirPods) NS глушит слабый сигнал в ноль
    public string DeviceId { get; set; } = "";    // выбранный микрофон (deviceId); пусто = устройство по умолч.
    // Потоковый режим: слова уходят в окно-приёмник во время записи. В файл НЕ пишется: каждый запуск программы
    // начинается в обычном режиме, поток включают кнопкой (решение пользователя 21.11.2026).
    [JsonIgnore]
    public bool Stream { get; set; } = false;
    // Подрежим потока: false - «Поток» (ручной: запись не кончается сама, раз в 5 с тишины напоминание),
    // true - «Поток-авто» (10 с тишины - автостоп, потом сторож сам включает запись по голосу). Решение пользователя 21.11.2026.
    [JsonIgnore]
    public bool StreamAuto { get; set; } = false;

    // Не сериализуем сюда путь; файл лежит рядом с профилем WebView2.
    [JsonIgnore]
    private static string FilePath => Path.Combine(Diag.Dir, "mic.json");

    public static MicSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var m = JsonSerializer.Deserialize<MicSettings>(File.ReadAllText(FilePath));
                if (m != null) { m.Gain = Math.Clamp(m.Gain, 1.0, 8.0); return m; }
            }
        }
        catch (Exception ex) { Diag.Write("MicSettings.Load: " + ex.Message); }
        return new MicSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { Diag.Write("MicSettings.Save: " + ex.Message); }
    }
}
