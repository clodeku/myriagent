using System.Globalization;
using System.Text;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 輸出被導向（管線、Actions 的記錄、PowerShell 擷取）時一律無 BOM 的 UTF-8；直接在主控台時沿用主控台的字碼頁
        if (Console.IsOutputRedirected)
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        if (Console.IsErrorRedirected)
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var context = new ToolContext(
            ReleaseKeys.Production,
            () => Environment.GetEnvironmentVariable(Signer.KeyVariable),
            GitHubOutput.FromEnvironment(),
            Console.Out,
            Console.Error,
            GhCommand.Default);
        return await ToolCommands.RunAsync(args, context, cts.Token);
    }
}

/// <summary>結束碼：成功 0、檢查沒過 1、用法錯 2</summary>
internal static class ToolExit
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 2;
}

/// <summary>
/// 指令用到的外部依賴。Program 給真的：ReleaseKeys.Production、環境變數 DESKTOP_RELEASE_KEY、GITHUB_OUTPUT、主控台、PATH 上的 gh（sign 的 --key-file 直接讀檔）；
/// selftest 給暫時金鑰、暫存的輸出檔、記憶體裡的主控台與假的 gh，跑的是同一份指令程式
/// </summary>
internal sealed record ToolContext(
    IReadOnlyList<ReleaseKey> ProductionKeys,
    Func<string?> ReadSigningKey,
    GitHubOutput Output,
    TextWriter Out,
    TextWriter Error,
    GhCommand Gh);

/// <summary>
/// GitHub Actions 的步驟輸出：有設定 GITHUB_OUTPUT 時，每個值附加一行 key=value（無 BOM 的 UTF-8、換行 \n）。
/// 值一律是工具算出的名稱、雜湊與數字；名稱不合規則或值含換行就丟例外，不讓值夾帶別的輸出
/// </summary>
internal sealed class GitHubOutput
{
    public const string Variable = "GITHUB_OUTPUT";

    private static readonly UTF8Encoding Utf8 = new(false);

    public GitHubOutput(string? filePath) => FilePath = string.IsNullOrEmpty(filePath) ? null : filePath;

    public string? FilePath { get; }

    public static GitHubOutput FromEnvironment() => new(Environment.GetEnvironmentVariable(Variable));

    public void Set(string key, string value)
    {
        if (key.Length == 0 || !key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            throw new ArgumentException($"輸出名稱不合規則：{key}", nameof(key));
        if (value.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException($"輸出 {key} 的值含換行", nameof(value));
        if (FilePath != null)
            File.AppendAllText(FilePath, $"{key}={value}\n", Utf8);
    }
}

/// <summary>用法錯（缺參數、不認得的參數、值不是數字或格式不對）：結束碼 2</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>一個指令的選項：「--名稱 值」與旗標；同一個選項只能出現一次</summary>
internal sealed class CommandArgs
{
    private readonly Dictionary<string, string> _values;
    private readonly HashSet<string> _flags;

    private CommandArgs(Dictionary<string, string> values, HashSet<string> flags)
    {
        _values = values;
        _flags = flags;
    }

    public static CommandArgs Parse(IReadOnlyList<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (valueOptions.Contains(name))
            {
                if (i + 1 >= args.Count)
                    throw new UsageException($"{name} 少了值");
                if (!values.TryAdd(name, args[++i]))
                    throw new UsageException($"{name} 重複");
            }
            else if (flagOptions.Contains(name))
            {
                if (!flags.Add(name))
                    throw new UsageException($"{name} 重複");
            }
            else
            {
                throw new UsageException($"不認得的參數：{name}");
            }
        }
        return new CommandArgs(values, flags);
    }

    public string Required(string name)
        => _values.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new UsageException($"缺少 {name}");

    public string? Optional(string name) => _values.GetValueOrDefault(name);

    public bool Flag(string name) => _flags.Contains(name);

    public int Integer(string name)
        => int.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new UsageException($"{name} 要是十進位整數");
}

/// <summary>指令分派（規格 6.1～6.4；U2 設計 T10）。Program 與 selftest 都經這裡執行指令</summary>
internal static class ToolCommands
{
    public const string Usage = """
        用法：myriagent-release <指令> [選項]
          version         --csproj <路徑> --count <N> --commit <40 碼>
                          輸出 version、build、release
          check-assembly  --publish-dir <資料夾> --release <release> --commit <40 碼>
          package         --publish-dir <資料夾> --release <release> --commit <40 碼> --out <資料夾> [--created-at <yyyy-MM-ddTHH:mm:ssZ>]
                          輸出 zip_name、zip_sha256、manifest_name、manifest_sha256、file_count、total_bytes
          sign            --manifest <路徑> --out <簽章檔路徑> [--key-file <金鑰檔>]
                          （私鑰從 --key-file 的 DPAPI 金鑰檔讀；沒給就從環境變數 DESKTOP_RELEASE_KEY 讀，PKCS#8 base64）
                          輸出 sig_name、sig_sha256、kid
          verify          --dir <資料夾> --release <release>
          keygen          --set-secret <名稱> --repo <owner/repo> | --dpapi-file <金鑰檔> | --dry-run
          selftest
        結束碼：0 成功、1 檢查沒過、2 用法錯。有設定 GITHUB_OUTPUT 時，輸出值另外附加成 key=value。
        """;

    private static readonly Dictionary<string, (string[] Values, string[] Flags)> Options = new(StringComparer.Ordinal)
    {
        ["version"] = (["--csproj", "--count", "--commit"], []),
        ["check-assembly"] = (["--publish-dir", "--release", "--commit"], []),
        ["package"] = (["--publish-dir", "--release", "--commit", "--out", "--created-at"], []),
        ["sign"] = (["--manifest", "--out", "--key-file"], []),
        ["verify"] = (["--dir", "--release"], []),
        ["keygen"] = (["--set-secret", "--repo", "--dpapi-file"], ["--dry-run"]),
        ["selftest"] = ([], []),
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, ToolContext context, CancellationToken ct)
    {
        if (args.Count == 1 && args[0] is "-h" or "--help")
        {
            context.Out.WriteLine(Usage);
            return ToolExit.Ok;
        }
        try
        {
            if (args.Count == 0)
                throw new UsageException("缺少指令");
            if (!Options.TryGetValue(args[0], out var allowed))
                throw new UsageException($"不認得的指令：{args[0]}");
            var options = CommandArgs.Parse(args.Skip(1).ToList(), allowed.Values, allowed.Flags);
            return args[0] switch
            {
                "version" => Version(options, context),
                "check-assembly" => CheckAssembly(options, context),
                "package" => await PackageAsync(options, context, ct),
                "sign" => await SignAsync(options, context, ct),
                "verify" => await VerifyAsync(options, context, ct),
                "keygen" => await KeyGen.RunAsync(options.Optional("--set-secret"), options.Optional("--repo"), options.Optional("--dpapi-file"), options.Flag("--dry-run"),
                    context.Gh, context.Out, context.Error, ct),
                "selftest" => await SelfTest.RunAsync(context.Out, ct),
                _ => throw new UsageException($"不認得的指令：{args[0]}"),
            };
        }
        catch (UsageException ex)
        {
            context.Error.WriteLine(ex.Message);
            context.Error.WriteLine(Usage);
            return ToolExit.Usage;
        }
        catch (OperationCanceledException)
        {
            return Fail(context, "已取消");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(context, ex.Message);
        }
    }

    private static int Version(CommandArgs a, ToolContext c)
    {
        var csproj = a.Required("--csproj");
        var count = a.Integer("--count");
        var commit = a.Required("--commit");
        if (Versioning.ReadCsprojVersion(csproj, out var baseVersion) is { } csprojError)
            return Fail(c, csprojError);
        if (Versioning.Compute(baseVersion, count, commit, out var computed) is { } error)
            return Fail(c, error);
        Emit(c, "version", computed!.Version.ToString());
        Emit(c, "build", computed.Build);
        Emit(c, "release", computed.Release);
        return ToolExit.Ok;
    }

    private static int CheckAssembly(CommandArgs a, ToolContext c)
    {
        var publishDir = a.Required("--publish-dir");
        var release = a.Required("--release");
        var commit = a.Required("--commit");
        if (AssemblyChecks.Check(publishDir, release, commit) is { } error)
            return Fail(c, error);
        c.Out.WriteLine($"通過：{AssemblyChecks.DesktopDll} 是 {release}（InformationalVersion 帶完整提交雜湊），沒有測試金鑰的中繼資料");
        return ToolExit.Ok;
    }

    private static async Task<int> PackageAsync(CommandArgs a, ToolContext c, CancellationToken ct)
    {
        var publishDir = a.Required("--publish-dir");
        var release = a.Required("--release");
        var commit = a.Required("--commit");
        var outDir = a.Required("--out");
        // 沒指定就用現在（UTC、整秒）；manifest 的 createdAt 與 zip 項目的時間都用它
        var createdAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (a.Optional("--created-at") is { } text && !Packager.TryParseCreatedAt(text, out createdAt))
            throw new UsageException("--created-at 要寫成 yyyy-MM-ddTHH:mm:ssZ（UTC）");
        var (output, error) = await Packager.PackAsync(publishDir, release, commit, outDir, createdAt, ct);
        if (error != null)
            return Fail(c, error);
        Emit(c, "zip_name", Path.GetFileName(output!.ZipPath));
        Emit(c, "zip_sha256", output.ZipSha256);
        Emit(c, "manifest_name", Path.GetFileName(output.ManifestPath));
        Emit(c, "manifest_sha256", output.ManifestSha256);
        Emit(c, "file_count", output.FileCount.ToString(CultureInfo.InvariantCulture));
        Emit(c, "total_bytes", output.TotalBytes.ToString(CultureInfo.InvariantCulture));
        return ToolExit.Ok;
    }

    private static async Task<int> SignAsync(CommandArgs a, ToolContext c, CancellationToken ct)
    {
        var manifest = a.Required("--manifest");
        var outPath = a.Required("--out");
        var (output, error) = a.Optional("--key-file") is { } keyFile
            ? await Signer.SignWithKeyFileAsync(manifest, outPath, keyFile, c.ProductionKeys, ct)
            : await Signer.SignAsync(manifest, outPath, c.ReadSigningKey(), c.ProductionKeys, ct);
        if (error != null)
            return Fail(c, error);
        Emit(c, "sig_name", Path.GetFileName(output!.SigPath));
        Emit(c, "sig_sha256", output.SigSha256);
        Emit(c, "kid", output.Kid);
        return ToolExit.Ok;
    }

    private static async Task<int> VerifyAsync(CommandArgs a, ToolContext c, CancellationToken ct)
    {
        var dir = a.Required("--dir");
        var release = a.Required("--release");
        var (result, error) = await Packager.VerifyDirectoryAsync(dir, release, c.ProductionKeys, ct);
        if (error != null)
            return Fail(c, error);
        if (!result!.Ok)
            return Fail(c, $"{result.Step}：{result.Error}");
        c.Out.WriteLine($"通過：{release}（簽章金鑰 {result.Signature!.Kid}）");
        return ToolExit.Ok;
    }

    private static void Emit(ToolContext c, string key, string value)
    {
        c.Output.Set(key, value);
        c.Out.WriteLine($"{key}={value}");
    }

    private static int Fail(ToolContext c, string message)
    {
        c.Error.WriteLine($"失敗：{message}");
        return ToolExit.Failed;
    }
}
