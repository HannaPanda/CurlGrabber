using System.Buffers.Binary;
using System.Text;

namespace CurlGrabber;

/// <summary>Was am Anfang der heruntergeladenen Datei gefunden wurde.</summary>
public sealed class PayloadScan
{
    /// <summary>Anzahl der Bytes vor der eigentlichen Nutzlast. 0, wenn die Datei sauber beginnt.</summary>
    public long PrefixLength { get; init; }

    /// <summary>Erkanntes Format der Nutzlast, fuer die Anzeige.</summary>
    public string Format { get; init; } = string.Empty;

    public bool HasPrefix => PrefixLength > 0;

    public static PayloadScan None { get; } = new();
}

/// <summary>
/// Manche Videohoster stellen ihren Segmenten einen vorgetaeuschten Datei-Anfang voran - mal ein
/// PNG-Fragment, mal CSS, HTML oder eine WOFF-Signatur -, damit die Antwort wie ein harmloses
/// Asset aussieht. Der Muell steht nur davor, die Nutzlast dahinter ist unveraendert.
///
/// Erkannt wird deshalb nicht die Faelschung, sondern der Beginn der echten Datei: gesucht wird
/// die erste Stelle, ab der ein durchgehendes MPEG-TS-Paketraster oder eine gueltige
/// ISO-BMFF-Boxkette steht. Damit ist gleichgueltig, was der Hoster sich als Tarnung ausdenkt.
/// </summary>
public static class PayloadTrimmer
{
    private const int TsPacketSize = 188;
    private const byte TsSyncByte = 0x47;

    /// <summary>So viele Pakete im Raster muessen stimmen, damit es kein Zufall mehr sein kann.</summary>
    private const int RequiredTsPackets = 50;

    /// <summary>So weit vorne wird nach dem Beginn der Nutzlast gesucht.</summary>
    private const int SearchWindow = 256 * 1024;

    /// <summary>Untersucht den Anfang einer Datei.</summary>
    public static PayloadScan Scan(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int wanted = (int)Math.Min(stream.Length, SearchWindow + (long)RequiredTsPackets * TsPacketSize);
        var head = new byte[wanted];
        stream.ReadExactly(head, 0, wanted);
        return Scan(head);
    }

    /// <summary>Untersucht einen bereits gelesenen Dateianfang.</summary>
    public static PayloadScan Scan(byte[] head)
    {
        int limit = (int)Math.Min(head.Length, SearchWindow);
        for (int offset = 0; offset < limit; offset++)
        {
            if (LooksLikeTransportStream(head, offset))
            {
                return new PayloadScan { PrefixLength = AlignToStreamStart(head, offset), Format = "MPEG-TS" };
            }

            if (LooksLikeIsoBaseMedia(head, offset))
            {
                return new PayloadScan { PrefixLength = offset, Format = "MP4" };
            }
        }

        return PayloadScan.None;
    }

    /// <summary>
    /// Das letzte Byte der Tarnung kann zufaellig ein 0x47 sein und dann genau auf dem Raster
    /// liegen - "GIF89a" faengt zum Beispiel mit dem Sync-Byte an. Der Fundort waere dann ein
    /// Paket zu frueh. Ein TS-Segment beginnt aber mit seiner Programmtabelle, also wird ab dem
    /// Fundort ein paar Pakete weit danach gesucht und auf die erste PAT ausgerichtet.
    /// </summary>
    private static int AlignToStreamStart(byte[] head, int offset)
    {
        const int searchPackets = 8;

        if (IsProgramAssociationTable(head, offset))
        {
            return offset;
        }

        for (int i = 1; i <= searchPackets; i++)
        {
            int candidate = offset + (i * TsPacketSize);
            if (candidate + TsPacketSize > head.Length)
            {
                break;
            }

            if (IsProgramAssociationTable(head, candidate))
            {
                return candidate;
            }
        }

        // Keine PAT in Reichweite: der Strom faengt offenbar mitten drin an, dann bleibt es
        // beim urspruenglichen Fundort.
        return offset;
    }

    /// <summary>Erkennt das Paket, mit dem ein TS-Segment regulaer beginnt: die PAT auf PID 0.</summary>
    private static bool IsProgramAssociationTable(byte[] head, int offset)
    {
        if (head[offset] != TsSyncByte)
        {
            return false;
        }

        bool payloadStart = (head[offset + 1] & 0x40) != 0;
        int pid = ((head[offset + 1] & 0x1f) << 8) | head[offset + 2];
        int adaptation = (head[offset + 3] >> 4) & 0x03;

        // PID 0, Beginn einer Sektion und reine Nutzlast ohne Adaptationsfeld.
        if (!payloadStart || pid != 0 || adaptation != 1)
        {
            return false;
        }

        // Danach folgt das pointer_field und die Tabellenkennung 0x00 fuer die PAT.
        int pointer = head[offset + 4];
        int tableId = offset + 5 + pointer;
        return tableId < offset + TsPacketSize && head[tableId] == 0x00;
    }

    /// <summary>
    /// Schneidet den Praefix weg, indem der Rest der Datei nach vorne geschoben wird. Das kommt
    /// ohne zweite Kopie auf der Platte aus - bei mehreren Gigabyte Video ist das der Unterschied
    /// zwischen "geht" und "kein Platz mehr".
    /// </summary>
    public static void RemovePrefix(string path, long prefixLength)
    {
        if (prefixLength <= 0)
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var buffer = new byte[1 << 20];
        long readAt = prefixLength;
        long writeAt = 0;

        while (true)
        {
            stream.Position = readAt;
            int got = stream.Read(buffer, 0, buffer.Length);
            if (got == 0)
            {
                break;
            }

            readAt += got;
            stream.Position = writeAt;
            stream.Write(buffer, 0, got);
            writeAt += got;
        }

        stream.SetLength(writeAt);
    }

    // ------------------------------------------------------------------ Erkennung

    private static bool LooksLikeTransportStream(byte[] head, int offset)
    {
        if (head[offset] != TsSyncByte)
        {
            return false;
        }

        int available = (head.Length - offset) / TsPacketSize;
        int packets = Math.Min(RequiredTsPackets, available);
        if (packets < 8)
        {
            return false;
        }

        for (int i = 0; i < packets; i++)
        {
            if (head[offset + (i * TsPacketSize)] != TsSyncByte)
            {
                return false;
            }
        }

        // Ein 0x47 im Fuellmuell koennte zufaellig auf dem Raster liegen und dann ein Paket zu
        // frueh schneiden. Deshalb muessen die ersten Paketkoepfe auch inhaltlich plausibel sein.
        for (int i = 0; i < Math.Min(packets, 8); i++)
        {
            if (!IsPlausibleTsHeader(head, offset + (i * TsPacketSize)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlausibleTsHeader(byte[] head, int offset)
    {
        // transport_error_indicator muss 0 sein, sonst haette der Sender das Paket als kaputt
        // markiert; adaptation_field_control 0 ist laut Norm verboten.
        bool errorFlag = (head[offset + 1] & 0x80) != 0;
        int adaptation = (head[offset + 3] >> 4) & 0x03;
        return !errorFlag && adaptation != 0;
    }

    private static bool LooksLikeIsoBaseMedia(byte[] head, int offset)
    {
        if (offset + 16 > head.Length)
        {
            return false;
        }

        if (ReadBoxType(head, offset) is not ("ftyp" or "styp"))
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(offset, 4));
        if (size is < 16 or > (1 << 20) || size % 4 != 0)
        {
            return false;
        }

        // Hinter der ftyp-Box muss unmittelbar die naechste Box beginnen.
        long next = offset + size;
        if (next + 8 > head.Length)
        {
            return true; // Nicht mehr pruefbar - die Signatur allein ist schon deutlich.
        }

        return ReadBoxType(head, (int)next) is
            "moov" or "moof" or "mdat" or "free" or "skip" or "sidx" or "styp" or "wide" or "pdin" or "meta";
    }

    private static string ReadBoxType(byte[] head, int offset)
        => Encoding.ASCII.GetString(head, offset + 4, 4);
}
