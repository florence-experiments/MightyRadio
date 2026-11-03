using System.IO;
using System.Text.Json.Serialization;

namespace MightyRadio.Services;

/// <summary>
/// Remembers that the user accepted the recording notice, in %APPDATA%\MightyRadio\recording-consent.json.
/// Deleting that file brings the notice back.
/// </summary>
public static class RecordingConsent
{
    public const int NoticeVersion = 1;

    public sealed class Record
    {
        [JsonPropertyName("accepted")] public bool Accepted { get; set; }
        [JsonPropertyName("acceptedAt")] public DateTimeOffset AcceptedAt { get; set; }
        [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("noticeVersion")] public int NoticeVersion { get; set; }
    }

    private static bool? _cached;

    public static bool IsAccepted
    {
        get
        {
            if (_cached == true) return true;
            try
            {
                if (!File.Exists(Storage.ConsentPath)) return false;
                var r = Storage.Load<Record?>(Storage.ConsentPath, () => null);
                _cached = r is { Accepted: true } && r.NoticeVersion >= 1;
                return _cached.Value;
            }
            catch { return false; }
        }
    }

    public static bool Save()
    {
        var ok = Storage.Save(Storage.ConsentPath, new Record
        {
            Accepted = true,
            AcceptedAt = DateTimeOffset.Now,
            AppVersion = AppInfo.Version,
            NoticeVersion = NoticeVersion,
        });
        _cached = true; // even if the file could not be written, don't nag again this session
        return ok;
    }

    public const string NoticeTitle = "Before you record";

    public const string NoticeText =
        "Recording a radio stream or a scanner feed may be restricted where you live.\n\n" +
        "• Most broadcasts, music and talk programmes are protected by copyright. Recording them is usually " +
        "allowed only for limited personal use, and sharing or publishing recordings usually is not.\n\n" +
        "• Some scanner feeds relay radio traffic that is not meant for the public (for example air traffic " +
        "control, marine or emergency services). In the UK, the Wireless Telegraphy Act 2006 makes it an offence " +
        "to obtain or disclose information from such transmissions without authority, and similar laws exist " +
        "elsewhere.\n\n" +
        "You alone are responsible for making sure that what you record, and what you do with it, is lawful " +
        "where you are. Mighty Radio is free software provided \"as is\", without warranty of any kind, and its " +
        "author accepts no liability for how it is used (see the MIT licence).";
}
