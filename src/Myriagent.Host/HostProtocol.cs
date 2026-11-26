// 代理主機與 app 之間的協定（設計 docs/plans/2026-11-26-agent-host.md 3.1）。
// 這個檔同時編進主機（.NET Framework 4.8）與桌面專案（連結進去）：只能用兩邊都有的 API，不要用 Span、範圍運算子、檔案範圍命名空間以外的新執行階段功能。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Myriagent.Host
{
    /// <summary>要主機開的程序</summary>
    internal sealed class HostSpawnRequest
    {
        public int RequestId;
        public string FileName = string.Empty;
        public List<string> Arguments = new List<string>();
        public string WorkingDirectory = string.Empty;

        /// <summary>子程序完整的環境變數（不是差異：主機照這一份開，不帶自己的）</summary>
        public List<KeyValuePair<string, string>> Environment = new List<KeyValuePair<string, string>>();
    }

    internal sealed class HostSpawnResult
    {
        public int RequestId;
        public bool Ok;

        /// <summary>主機給這個程序的代號（之後的 Write／CloseInput／Output／Exited 都用它）</summary>
        public int Handle;
        public int ProcessId;
        public string? Error;

        /// <summary>Win32 錯誤碼（啟動失敗時；0＝不是 Win32 錯誤）：app 據此判斷是不是 CLI 換檔空窗的暫時失敗</summary>
        public int NativeError;
    }

    /// <summary>主機手上的一個程序（List 的回覆）</summary>
    internal sealed class HostProcessInfo
    {
        public int Handle;
        public int ProcessId;
        public bool Exited;
        public int? ExitCode;

        /// <summary>兩條輸出各自目前的結尾位移（主機讀到的總位元組數）</summary>
        public long OutEnd;
        public long ErrEnd;

        /// <summary>暫存超過上限、已經丟掉：接不回去（從頭的輸出不完整）</summary>
        public bool Overflow;

        /// <summary>目前有 app 接著它（輸出正在轉送）</summary>
        public bool Attached;
    }

    internal sealed class HostOutput
    {
        public int Handle;

        /// <summary>1＝stdout、2＝stderr</summary>
        public byte Stream;

        /// <summary>這一段在那條串流裡的起始位移（位元組，從 0 起算、單調遞增）</summary>
        public long Offset;
        public byte[] Data = new byte[0];
    }

    /// <summary>
    /// 框的格式：4 位元組長度（小端；型別加本文的位元組數）＋1 位元組型別＋本文。本文以 BinaryWriter 寫（字串是 7 位元長度前綴的 UTF-8）。
    /// 一條連線上兩個方向各自依序送，不對應請求與回應（Spawn 以 RequestId 對回 SpawnResult）
    /// </summary>
    internal static class HostProtocol
    {
        /// <summary>
        /// 協定版本：兩邊在 Hello／HelloOk 各報自己的，app 只和 <see cref="MinVersion"/> 以上的主機說話。
        /// 1＝H0（開程序、轉送、app 斷線就結束全部）；2＝H1（<see cref="KeepVersion"/>：app 交棒後保留程序、暫存輸出、新 app 接回）
        /// </summary>
        public const int Version = 2;

        public const int MinVersion = 1;

        /// <summary>這一版起主機會在 app 交棒後保留程序、認得 Ack／Detach／Handoff／List／Attach；用戶端也要這一版起主機才替它留著還沒確認的輸出</summary>
        public const int KeepVersion = 2;

        /// <summary>單一框的上限（防止壞掉的長度讓接收端配置很大的緩衝區）</summary>
        public const int MaxFrame = 8 * 1024 * 1024;

        // app → 主機
        public const byte Hello = 1;
        public const byte Spawn = 2;
        public const byte Write = 3;
        public const byte CloseInput = 4;
        public const byte Shutdown = 5;

        // ── 第 2 版 ──

        /// <summary>app 已經處理到哪裡（兩條輸出各自的位移，整行的邊界）：主機丟掉在這之前的暫存</summary>
        public const byte Ack = 6;

        /// <summary>這個程序先不要再送輸出（app 要放開它、留給之後的 app 接回）：主機回 <see cref="Detached"/></summary>
        public const byte Detach = 7;

        /// <summary>app 要結束了、之後會有新的 app 連上來：斷線後保留程序這麼多秒，沒人連上才結束它們</summary>
        public const byte Handoff = 8;

        /// <summary>列出主機手上的程序：主機回 <see cref="ListResult"/></summary>
        public const byte List = 9;

        /// <summary>接回一個程序：從給的位移起把暫存的輸出補送、之後照常轉送；主機回 <see cref="AttachResult"/></summary>
        public const byte Attach = 10;

        // 主機 → app
        public const byte HelloOk = 101;
        public const byte SpawnResult = 102;
        public const byte Output = 103;
        public const byte Exited = 104;

        /// <summary>一次 Write 的結果（寫進去了、或那個程序的 stdin 已經關了）：app 的寫入等它，才和直接寫管線一樣在失敗時馬上知道</summary>
        public const byte WriteResult = 105;

        /// <summary>Detach 的回覆：到這兩個位移為止的輸出都已經送出（在這個框之前），之後的留在主機</summary>
        public const byte Detached = 106;

        public const byte ListResult = 107;

        public const byte AttachResult = 108;

        public const byte StdOut = 1;
        public const byte StdErr = 2;

        /// <summary>具名管道的名稱：每個資料根目錄一個（同一台電腦可以同時跑正常使用的與 --data 的測試實例）</summary>
        public static string PipeName(string dataRoot)
        {
            return "Myriagent.Host." + RootHash(dataRoot);
        }

        /// <summary>主機工作物件的名稱（Local\ 命名空間）：app 以名稱打開它，查某個程序是不是主機開的 CLI 或它的子孫</summary>
        public static string JobName(string dataRoot)
        {
            return "Local\\Myriagent.Host." + RootHash(dataRoot);
        }

        private static string RootHash(string dataRoot)
        {
            var normalized = Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToUpperInvariant();
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var text = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                    text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        // ───────────────────────────── 框 ─────────────────────────────

        /// <summary>組一個完整的框（呼叫端以一次 Write 送出，多執行緒時自己加鎖）</summary>
        public static byte[] Frame(byte type, byte[]? payload)
        {
            int length = 1 + (payload == null ? 0 : payload.Length);
            var frame = new byte[4 + length];
            frame[0] = (byte)length;
            frame[1] = (byte)(length >> 8);
            frame[2] = (byte)(length >> 16);
            frame[3] = (byte)(length >> 24);
            frame[4] = type;
            if (payload != null && payload.Length > 0)
                Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
            return frame;
        }

        /// <summary>讀一個框；對方正常關閉（一個位元組都沒讀到）回 false，讀到一半斷掉或長度不合理丟 IOException</summary>
        public static bool ReadFrame(Stream stream, out byte type, out byte[] payload)
        {
            type = 0;
            payload = new byte[0];
            var header = new byte[4];
            int got = ReadFully(stream, header, 0, 4);
            if (got == 0)
                return false;
            if (got < 4)
                throw new IOException("框的長度沒有讀完");
            int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            if (length < 1 || length > MaxFrame)
                throw new IOException("框的長度不合理：" + length);
            var body = new byte[length];
            if (ReadFully(stream, body, 0, length) < length)
                throw new IOException("框的本文沒有讀完");
            type = body[0];
            payload = new byte[length - 1];
            Buffer.BlockCopy(body, 1, payload, 0, length - 1);
            return true;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return total;
        }

        // ───────────────────────────── 本文 ─────────────────────────────

        public static byte[] EncodeHello(int version)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(version);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static int DecodeHello(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
                return r.ReadInt32();
        }

        /// <summary>HelloOk：主機的協定版本與程序 id</summary>
        public static byte[] EncodeHelloOk(int version, int hostProcessId)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(version);
                w.Write(hostProcessId);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void DecodeHelloOk(byte[] payload, out int version, out int hostProcessId)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                version = r.ReadInt32();
                hostProcessId = r.ReadInt32();
            }
        }

        public static byte[] EncodeSpawn(HostSpawnRequest request)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(request.RequestId);
                w.Write(request.FileName);
                w.Write(request.Arguments.Count);
                foreach (var argument in request.Arguments)
                    w.Write(argument);
                w.Write(request.WorkingDirectory);
                w.Write(request.Environment.Count);
                foreach (var pair in request.Environment)
                {
                    w.Write(pair.Key);
                    w.Write(pair.Value);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static HostSpawnRequest DecodeSpawn(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var request = new HostSpawnRequest();
                request.RequestId = r.ReadInt32();
                request.FileName = r.ReadString();
                int argc = r.ReadInt32();
                for (int i = 0; i < argc; i++)
                    request.Arguments.Add(r.ReadString());
                request.WorkingDirectory = r.ReadString();
                int envc = r.ReadInt32();
                for (int i = 0; i < envc; i++)
                {
                    var key = r.ReadString();
                    var value = r.ReadString();
                    request.Environment.Add(new KeyValuePair<string, string>(key, value));
                }
                return request;
            }
        }

        public static byte[] EncodeSpawnResult(HostSpawnResult result)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(result.RequestId);
                w.Write(result.Ok);
                w.Write(result.Handle);
                w.Write(result.ProcessId);
                w.Write(result.Error ?? string.Empty);
                w.Write(result.NativeError);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static HostSpawnResult DecodeSpawnResult(byte[] payload)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                var result = new HostSpawnResult();
                result.RequestId = r.ReadInt32();
                result.Ok = r.ReadBoolean();
                result.Handle = r.ReadInt32();
                result.ProcessId = r.ReadInt32();
                var error = r.ReadString();
                result.Error = error.Length == 0 ? null : error;
                result.NativeError = r.ReadInt32();
                return result;
            }
        }

        /// <summary>Write：代號（4）＋序號（4，app 給的，WriteResult 帶回來）＋寫進那個程序 stdin 的位元組</summary>
        public static byte[] EncodeWrite(int handle, int sequence, byte[] data)
        {
            var payload = new byte[8 + data.Length];
            WriteInt32(payload, 0, handle);
            WriteInt32(payload, 4, sequence);
            Buffer.BlockCopy(data, 0, payload, 8, data.Length);
            return payload;
        }

        public static void DecodeWrite(byte[] payload, out int handle, out int sequence, out byte[] data)
        {
            if (payload.Length < 8)
                throw new IOException("Write 的本文太短");
            handle = ReadInt32(payload, 0);
            sequence = ReadInt32(payload, 4);
            data = new byte[payload.Length - 8];
            Buffer.BlockCopy(payload, 8, data, 0, data.Length);
        }

        /// <summary>WriteResult：代號（4）＋序號（4）＋成功（1）</summary>
        public static byte[] EncodeWriteResult(int handle, int sequence, bool ok)
        {
            var payload = new byte[9];
            WriteInt32(payload, 0, handle);
            WriteInt32(payload, 4, sequence);
            payload[8] = (byte)(ok ? 1 : 0);
            return payload;
        }

        public static void DecodeWriteResult(byte[] payload, out int handle, out int sequence, out bool ok)
        {
            if (payload.Length < 9)
                throw new IOException("WriteResult 的本文太短");
            handle = ReadInt32(payload, 0);
            sequence = ReadInt32(payload, 4);
            ok = payload[8] != 0;
        }

        public static byte[] EncodeHandle(int handle)
        {
            var payload = new byte[4];
            WriteInt32(payload, 0, handle);
            return payload;
        }

        public static int DecodeHandle(byte[] payload)
        {
            if (payload.Length < 4)
                throw new IOException("本文太短");
            return ReadInt32(payload, 0);
        }

        /// <summary>Output：代號（4）＋串流（1）＋位移（8）＋資料</summary>
        public static byte[] EncodeOutput(int handle, byte stream, long offset, byte[] buffer, int count)
        {
            var payload = new byte[13 + count];
            WriteInt32(payload, 0, handle);
            payload[4] = stream;
            for (int i = 0; i < 8; i++)
                payload[5 + i] = (byte)(offset >> (8 * i));
            Buffer.BlockCopy(buffer, 0, payload, 13, count);
            return payload;
        }

        public static HostOutput DecodeOutput(byte[] payload)
        {
            if (payload.Length < 13)
                throw new IOException("Output 的本文太短");
            var output = new HostOutput();
            output.Handle = ReadInt32(payload, 0);
            output.Stream = payload[4];
            long offset = 0;
            for (int i = 0; i < 8; i++)
                offset |= (long)payload[5 + i] << (8 * i);
            output.Offset = offset;
            output.Data = new byte[payload.Length - 13];
            Buffer.BlockCopy(payload, 13, output.Data, 0, output.Data.Length);
            return output;
        }

        /// <summary>Exited：代號（4）＋有沒有結束碼（1）＋結束碼（4）</summary>
        public static byte[] EncodeExited(int handle, int? exitCode)
        {
            var payload = new byte[9];
            WriteInt32(payload, 0, handle);
            payload[4] = (byte)(exitCode.HasValue ? 1 : 0);
            WriteInt32(payload, 5, exitCode ?? 0);
            return payload;
        }

        public static void DecodeExited(byte[] payload, out int handle, out int? exitCode)
        {
            if (payload.Length < 9)
                throw new IOException("Exited 的本文太短");
            handle = ReadInt32(payload, 0);
            exitCode = payload[4] != 0 ? ReadInt32(payload, 5) : (int?)null;
        }

        // ───────────────────────────── 第 2 版的本文 ─────────────────────────────

        /// <summary>Ack、Detached：代號（4）＋stdout 的位移（8）＋stderr 的位移（8）</summary>
        public static byte[] EncodeOffsets(int handle, long outOffset, long errOffset)
        {
            var payload = new byte[20];
            WriteInt32(payload, 0, handle);
            WriteInt64(payload, 4, outOffset);
            WriteInt64(payload, 12, errOffset);
            return payload;
        }

        public static void DecodeOffsets(byte[] payload, out int handle, out long outOffset, out long errOffset)
        {
            if (payload.Length < 20)
                throw new IOException("位移的本文太短");
            handle = ReadInt32(payload, 0);
            outOffset = ReadInt64(payload, 4);
            errOffset = ReadInt64(payload, 12);
        }

        /// <summary>Handoff：保留幾秒（4）</summary>
        public static byte[] EncodeHandoff(int keepSeconds)
        {
            return EncodeHandle(keepSeconds);
        }

        public static int DecodeHandoff(byte[] payload)
        {
            return DecodeHandle(payload);
        }

        /// <summary>Attach：請求編號（4）＋代號（4）＋兩個位移</summary>
        public static byte[] EncodeAttach(int requestId, int handle, long outOffset, long errOffset)
        {
            var payload = new byte[24];
            WriteInt32(payload, 0, requestId);
            WriteInt32(payload, 4, handle);
            WriteInt64(payload, 8, outOffset);
            WriteInt64(payload, 16, errOffset);
            return payload;
        }

        public static void DecodeAttach(byte[] payload, out int requestId, out int handle, out long outOffset, out long errOffset)
        {
            if (payload.Length < 24)
                throw new IOException("Attach 的本文太短");
            requestId = ReadInt32(payload, 0);
            handle = ReadInt32(payload, 4);
            outOffset = ReadInt64(payload, 8);
            errOffset = ReadInt64(payload, 16);
        }

        public static byte[] EncodeAttachResult(int requestId, bool ok, string? error)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(requestId);
                w.Write(ok);
                w.Write(error ?? string.Empty);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static void DecodeAttachResult(byte[] payload, out int requestId, out bool ok, out string? error)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                ok = r.ReadBoolean();
                var text = r.ReadString();
                error = text.Length == 0 ? null : text;
            }
        }

        public static byte[] EncodeListResult(int requestId, IList<HostProcessInfo> processes)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(requestId);
                w.Write(processes.Count);
                foreach (var info in processes)
                {
                    w.Write(info.Handle);
                    w.Write(info.ProcessId);
                    w.Write(info.Exited);
                    w.Write(info.ExitCode.HasValue);
                    w.Write(info.ExitCode ?? 0);
                    w.Write(info.OutEnd);
                    w.Write(info.ErrEnd);
                    w.Write(info.Overflow);
                    w.Write(info.Attached);
                }
                w.Flush();
                return ms.ToArray();
            }
        }

        public static List<HostProcessInfo> DecodeListResult(byte[] payload, out int requestId)
        {
            using (var r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8))
            {
                requestId = r.ReadInt32();
                int count = r.ReadInt32();
                var list = new List<HostProcessInfo>(count);
                for (int i = 0; i < count; i++)
                {
                    var info = new HostProcessInfo();
                    info.Handle = r.ReadInt32();
                    info.ProcessId = r.ReadInt32();
                    info.Exited = r.ReadBoolean();
                    bool hasCode = r.ReadBoolean();
                    int code = r.ReadInt32();
                    info.ExitCode = hasCode ? code : (int?)null;
                    info.OutEnd = r.ReadInt64();
                    info.ErrEnd = r.ReadInt64();
                    info.Overflow = r.ReadBoolean();
                    info.Attached = r.ReadBoolean();
                    list.Add(info);
                }
                return list;
            }
        }

        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
                buffer[offset + i] = (byte)(value >> (8 * i));
        }

        private static long ReadInt64(byte[] buffer, int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
                value |= (long)buffer[offset + i] << (8 * i);
            return value;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24);
        }

        // ───────────────────────────── 命令列 ─────────────────────────────

        /// <summary>
        /// 把參數組成 Windows 的命令列（.NET Framework 的 ProcessStartInfo 沒有 ArgumentList）：規則同 CommandLineToArgvW／.NET 的 PasteArguments——
        /// 沒有空白與引號的原樣，其他的包雙引號、引號前的反斜線加倍、引號本身加反斜線
        /// </summary>
        public static string BuildCommandLine(IEnumerable<string> arguments)
        {
            var text = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (text.Length > 0)
                    text.Append(' ');
                AppendArgument(text, argument ?? string.Empty);
            }
            return text.ToString();
        }

        private static void AppendArgument(StringBuilder text, string argument)
        {
            if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                text.Append(argument);
                return;
            }
            text.Append('"');
            int i = 0;
            while (i < argument.Length)
            {
                char c = argument[i++];
                if (c == '\\')
                {
                    int slashes = 1;
                    while (i < argument.Length && argument[i] == '\\')
                    {
                        i++;
                        slashes++;
                    }
                    if (i == argument.Length)
                        text.Append('\\', slashes * 2);          // 結尾的反斜線：後面接著結束的引號，全部加倍
                    else if (argument[i] == '"')
                    {
                        text.Append('\\', slashes * 2 + 1);      // 引號前的反斜線加倍，再跳脫引號
                        text.Append('"');
                        i++;
                    }
                    else
                        text.Append('\\', slashes);
                }
                else if (c == '"')
                {
                    text.Append('\\');
                    text.Append('"');
                }
                else
                    text.Append(c);
            }
            text.Append('"');
        }
    }
}
