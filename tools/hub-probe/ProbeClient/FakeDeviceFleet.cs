using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyriagentHub.ProbeClient;

/// <summary>假裝置群的失敗（訊息給使用者看）</summary>
internal sealed class FleetException(string message) : Exception(message);

/// <summary>一台 FakeDevice session 的程序與進度</summary>
internal sealed partial class FleetMember
{
    private readonly object gate = new();
    private readonly Queue<string> tail = new();
    private StreamWriter? stdout;
    private StreamWriter? stderr;
    private string? lastJson;

    /// <param name="chatStep">角色的對話是劇本的第幾步（<see cref="FleetRules.RoleChatStep"/>；轉送階段的媒體短對話在它之前，不算對話跑完）</param>
    /// <param name="relayMedia">轉送階段的媒體檔；沒有轉送階段是 null</param>
    public FleetMember(FleetDevice device, string dataRoot, string scriptPath, int? chatStep = null, RelayMediaFile? relayMedia = null)
    {
        Device = device;
        DataRoot = dataRoot;
        ScriptPath = scriptPath;
        ChatStep = chatStep;
        RelayMedia = relayMedia;
    }

    public FleetDevice Device { get; }

    public string DataRoot { get; }

    public string ScriptPath { get; }

    public int? ChatStep { get; }

    public RelayMediaFile? RelayMedia { get; }

    public Process? Process { get; private set; }

    /// <summary>劇本在中樞啟動之後的第一步（addAccount）做完：已配對、連上（UTC 毫秒）</summary>
    public TaskCompletionSource<long> Live { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>對話（chat 步驟）跑完（UTC 毫秒）</summary>
    public TaskCompletionSource<long> ChatDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>程序結束（結束碼）</summary>
    public TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Tail
    {
        get
        {
            lock (gate)
                return string.Join(Environment.NewLine, tail);
        }
    }

    /// <summary>session 最後印的結果（一行 JSON）</summary>
    public string? LastJson
    {
        get
        {
            lock (gate)
                return lastJson;
        }
    }

    public void Start(ProcessStartInfo info, string logPrefix)
    {
        stdout = new StreamWriter(logPrefix + ".out.txt", append: false, new UTF8Encoding(false)) { AutoFlush = true };
        stderr = new StreamWriter(logPrefix + ".err.txt", append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnStdout(e.Data);
        process.ErrorDataReceived += (_, e) => OnStderr(e.Data);
        process.Exited += (_, _) =>
        {
            // 等非同步讀取把剩下的行讀完（WaitForExit() 在 Exited 之後等輸出串流關閉）
            try
            {
                process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
            }

            Exited.TrySetResult(process.ExitCode);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Process = process;
    }

    /// <summary>以 PID 結束這台的程序樹（只結束自己啟動的）</summary>
    public void Kill()
    {
        try
        {
            if (Process is { HasExited: false } running)
                running.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void CloseLogs()
    {
        lock (gate)
        {
            stdout?.Dispose();
            stderr?.Dispose();
        }
    }

    private void OnStdout(string? line)
    {
        if (line is null)
            return;
        lock (gate)
        {
            stdout?.WriteLine(line);
            if (line.StartsWith('{'))
                lastJson = line;
        }
    }

    private void OnStderr(string? line)
    {
        if (line is null)
            return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (gate)
        {
            stderr?.WriteLine(line);
            tail.Enqueue(line);
            while (tail.Count > 40)
                tail.Dequeue();
        }

        // FakeScript 每一步做完印「第 N 步 op：{結果}」；對話跑完只看角色的那一步（轉送階段的媒體短對話在它之前）
        var step = StepLine().Match(line);
        if (!step.Success)
            return;
        switch (step.Groups[2].Value)
        {
            case "addAccount":
                Live.TrySetResult(now);
                break;
            case "chat" when ChatStep is null || step.Groups[1].Value == ChatStep.Value.ToString(CultureInfo.InvariantCulture):
                ChatDone.TrySetResult(now);
                break;
        }
    }

    [GeneratedRegex(@"^第 (\d+) 步 (\w+)：")]
    private static partial Regex StepLine();
}

/// <summary>
/// 假裝置群（WP4.9 計畫 D6）：N 個 <c>FakeDevice session --ephemeral-key</c>（每台自己的資料根、裝置金鑰只在記憶體，不碰 CNG 的金鑰存放區），
/// 一批最多 4 台、這一批都配對好（劇本的 addAccount 做完）才送下一批。環境變數照 hub-e2e 剝掉，擁有者密碼的環境變數再明確放回
/// （密碼不上命令列）。程序的輸出寫進工作資料夾；只以 PID 結束自己啟動的程序
/// </summary>
internal sealed class FakeDeviceFleet : IAsyncDisposable
{
    private readonly string fakeExe;
    private readonly string server;
    private readonly string ownerUser;
    private readonly string ownerOrigin;
    private readonly KeyValuePair<string, string> password;
    private readonly Action<object> emit;

    public FakeDeviceFleet(string fakeExe, string workDir, string server, string ownerOrigin, string ownerUser, string passwordVariable, string passwordValue,
        IReadOnlyList<FleetDevice> plan, FleetTiming timing, Action<object> emit)
    {
        this.fakeExe = Path.GetFullPath(fakeExe);
        this.server = server;
        this.ownerOrigin = ownerOrigin;
        this.ownerUser = ownerUser;
        password = new KeyValuePair<string, string>(passwordVariable, passwordValue);
        this.emit = emit;
        Timing = timing;
        var devices = Path.Combine(workDir, "devices");
        Directory.CreateDirectory(devices);
        // 轉送階段的媒體檔放在裝置的資料根以外（資料根在 RemoteFileGuard 的禁止清單裡；項目登記過的精確檔案才給 /m/）
        var media = Path.GetFullPath(Path.Combine(workDir, "media"));
        if (timing.RelaySeconds > 0)
            Directory.CreateDirectory(media);
        Members = plan.Select(d =>
        {
            var root = Path.Combine(devices, d.Name);
            var script = Path.Combine(devices, d.Name + ".script.json");
            RelayMediaFile? relay = null;
            if (timing.RelaySeconds > 0)
            {
                var file = Path.Combine(media, d.Name + ".mp4");
                var content = FleetRules.RelayMediaContent(FleetRules.RelaySeedBase + d.Index);
                File.WriteAllBytes(file, content);
                relay = RelayMediaFile.Describe(file, content);
            }

            var json = FleetRules.Script(d, timing, relay?.Path);
            File.WriteAllText(script, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            return new FleetMember(d, root, script, FleetRules.RoleChatStep(json), relay);
        }).ToList();
    }

    public FleetTiming Timing { get; }

    public IReadOnlyList<FleetMember> Members { get; }

    /// <summary>依配對順序一批一批啟動；每一批的每一台都配對好（或任何一台失敗、逾時就丟例外）才送下一批。回傳全部配對好的時間（UTC 毫秒）</summary>
    public async Task<long> PairAllAsync(TimeSpan perDevice, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        foreach (var batch in Members.GroupBy(m => m.Device.Batch).OrderBy(g => g.Key))
        {
            var members = batch.ToList();
            foreach (var member in members)
                Launch(member);
            foreach (var member in members)
            {
                var first = await Task.WhenAny(member.Live.Task, member.Exited.Task, Task.Delay(perDevice, ct));
                ct.ThrowIfCancellationRequested();
                if (first == member.Live.Task)
                    continue;
                var why = first == member.Exited.Task ? $"結束碼 {member.Exited.Task.Result}" : $"{perDevice.TotalSeconds:0} 秒內沒有配對好";
                throw new FleetException($"{member.Device.Name} 配對失敗（{why}）：{Environment.NewLine}{member.Tail}");
            }

            emit(new
            {
                @event = "batch",
                batch = batch.Key,
                devices = members.Select(m => m.Device.Name).ToList(),
                roles = members.Select(m => m.Device.Role.ToString()).ToList(),
                seconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
            });
        }

        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>串流與持久的對話都跑完（或逾時）：回傳是否全部跑完</summary>
    public async Task<bool> WaitChatsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var chats = Members.Where(m => m.Device.Role != FleetRole.Idle).Select(m => (Task)m.ChatDone.Task).ToList();
        if (chats.Count == 0)
            return true;
        var all = Task.WhenAll(chats);
        return await Task.WhenAny(all, Task.Delay(timeout, ct)) == all;
    }

    /// <summary>全部的程序都結束（或逾時）：回傳是否全部結束</summary>
    public async Task<bool> WaitExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var all = Task.WhenAll(Members.Where(m => m.Process is not null).Select(m => (Task)m.Exited.Task));
        return await Task.WhenAny(all, Task.Delay(timeout, ct)) == all;
    }

    /// <summary>還在跑的全部以 PID 結束</summary>
    public void KillAll()
    {
        foreach (var member in Members)
            member.Kill();
    }

    /// <summary>每台的結果：角色、結束碼、配對好與對話跑完的時間、session 的 drained／rejected、BatchAck、即時轉送</summary>
    public JsonArray Results()
    {
        var result = new JsonArray();
        foreach (var member in Members)
        {
            JsonNode? session = null;
            if (member.LastJson is { } json)
            {
                try
                {
                    session = JsonNode.Parse(json);
                }
                catch (JsonException)
                {
                }
            }

            var status = session?["status"];
            result.Add(new JsonObject
            {
                ["name"] = member.Device.Name,
                ["role"] = member.Device.Role.ToString(),
                ["batch"] = member.Device.Batch,
                ["pid"] = member.Process?.Id,
                ["exitCode"] = member.Exited.Task.IsCompletedSuccessfully ? member.Exited.Task.Result : null,
                ["liveAtMs"] = member.Live.Task.IsCompletedSuccessfully ? member.Live.Task.Result : null,
                ["chatDoneAtMs"] = member.ChatDone.Task.IsCompletedSuccessfully ? member.ChatDone.Task.Result : null,
                ["pairSeconds"] = session?["pair"]?["seconds"]?.DeepClone(),
                ["drained"] = session?["drained"]?.DeepClone(),
                ["rejected"] = session?["rejected"]?.DeepClone(),
                ["ephemeralKey"] = session?["ephemeralKey"]?.DeepClone(),
                ["unpaired"] = session?["unpair"]?["deviceId"] is not null,
                ["features"] = status?["features"]?.DeepClone(),
                ["batchAck"] = status?["batchAck"]?.DeepClone(),
                ["liveRelay"] = status?["liveRelay"]?.DeepClone(),
                ["refusedCli"] = status?["refusedCli"]?.DeepClone(),
            });
        }

        return result;
    }

    public ValueTask DisposeAsync()
    {
        KillAll();
        foreach (var member in Members)
        {
            member.Process?.Dispose();
            member.CloseLogs();
        }

        return ValueTask.CompletedTask;
    }

    private void Launch(FleetMember member)
    {
        var info = new ProcessStartInfo(fakeExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(fakeExe)!,
        };
        string[] args =
        [
            "--data", member.DataRoot, "session", "--server", server, "--name", member.Device.Name, "--dev-trust",
            "--owner-user", ownerUser, "--owner-password-env", password.Key, "--owner-origin", ownerOrigin,
            "--script", member.ScriptPath, "--ephemeral-key", "--timeout", "120",
        ];
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        ChildEnvironment.Apply(info, new Dictionary<string, string> { [password.Key] = password.Value });
        member.Start(info, Path.Combine(Path.GetDirectoryName(member.ScriptPath)!, member.Device.Name));
        emit(new { @event = "device", name = member.Device.Name, role = member.Device.Role.ToString(), batch = member.Device.Batch, pid = member.Process!.Id });
    }
}
