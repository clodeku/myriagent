using System.Buffers;
using System.Text;

namespace MyriagentHub.Tls;

/// <summary>從 TLS ClientHello 原始位元組取出 SNI 與 ALPN（只讀、不修改；格式不對回 false，任何長度欄位都先檢查邊界）</summary>
internal static class ClientHelloParser
{
    public sealed record Result(string? ServerName, IReadOnlyList<string> Alpn);

    public static bool TryParse(ReadOnlySequence<byte> sequence, out Result? result) => TryParse(sequence.ToArray(), out result);

    public static bool TryParse(byte[] data, out Result? result)
    {
        result = null;
        // 把一個以上的 handshake 記錄（content type 22）片段串起來
        var handshake = new List<byte>();
        var pos = 0;
        while (pos + 5 <= data.Length && data[pos] == 0x16)
        {
            var recordLength = (data[pos + 3] << 8) | data[pos + 4];
            var available = Math.Min(recordLength, data.Length - pos - 5);
            handshake.AddRange(new ArraySegment<byte>(data, pos + 5, available));
            pos += 5 + recordLength;
            if (handshake.Count >= 4)
            {
                var needed = 4 + ((handshake[1] << 16) | (handshake[2] << 8) | handshake[3]);
                if (handshake.Count >= needed)
                    break;
            }
        }

        var h = handshake.ToArray();
        if (h.Length < 4 || h[0] != 0x01)
            return false; // 不是 ClientHello
        var bodyLength = (h[1] << 16) | (h[2] << 8) | h[3];
        var end = Math.Min(h.Length, 4 + bodyLength);
        var p = 4 + 2 + 32; // client_version + random
        if (p + 1 > end)
            return false;
        p += 1 + h[p]; // session_id
        if (p + 2 > end)
            return false;
        p += 2 + ((h[p] << 8) | h[p + 1]); // cipher_suites
        if (p + 1 > end)
            return false;
        p += 1 + h[p]; // compression_methods
        if (p + 2 > end)
        {
            result = new Result(null, []);
            return true;
        }
        var extEnd = Math.Min(end, p + 2 + ((h[p] << 8) | h[p + 1]));
        p += 2;

        string? sni = null;
        var alpn = new List<string>();
        while (p + 4 <= extEnd)
        {
            var type = (h[p] << 8) | h[p + 1];
            var len = (h[p + 2] << 8) | h[p + 3];
            var dataStart = p + 4;
            var dataEnd = Math.Min(extEnd, dataStart + len);
            if (type == 0x0000 && dataStart + 5 <= dataEnd)
            {
                // server_name：list length(2) + name_type(1) + name length(2) + name
                var nameType = h[dataStart + 2];
                var nameLen = (h[dataStart + 3] << 8) | h[dataStart + 4];
                if (nameType == 0 && dataStart + 5 + nameLen <= dataEnd)
                    sni = Encoding.ASCII.GetString(h, dataStart + 5, nameLen);
            }
            else if (type == 0x0010 && dataStart + 2 <= dataEnd)
            {
                // application_layer_protocol_negotiation：list length(2) + (len(1) + name)*
                var q = dataStart + 2;
                var listEnd = Math.Min(dataEnd, dataStart + 2 + ((h[dataStart] << 8) | h[dataStart + 1]));
                while (q + 1 <= listEnd)
                {
                    var n = h[q];
                    if (q + 1 + n > listEnd)
                        break;
                    alpn.Add(Encoding.ASCII.GetString(h, q + 1, n));
                    q += 1 + n;
                }
            }
            p = dataStart + len;
        }

        result = new Result(sni, alpn);
        return true;
    }
}
