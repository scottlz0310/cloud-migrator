using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// <c>logs/rate_state.json</c> の atomic 読み書きを担うストア（設計書 v2 §6、#163）。
/// <para>
/// v0.6.0 形式（<c>version=2</c>、<c>rate_tokens_per_sec</c> + <c>max_inflight</c>）を書込対象とし、
/// v0.5.x 形式（<c>rate</c> キーのみ、<c>version</c> なし）も後方互換で読込する。
/// v0.5.x からのアップグレード時は前回レートを復元しつつ次回保存時に v2 形式へ移行する。
/// </para>
/// <para>
/// 書込は temp→rename の atomic 方式で、クラッシュ時にパーシャルライトを残さない。
/// 本クラスは I/O のみを担当し、<c>[minRate, maxRate]</c> クランプ等の意味付けは呼び出し側で行う。
/// </para>
/// </summary>
public sealed class RateStateStore
{
    private const int SupportedVersion = 2;
    private readonly string _filePath;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>ストアを初期化する。</summary>
    /// <param name="filePath">状態ファイルの絶対パス。親ディレクトリは <see cref="SaveAsync"/> 実行時に作成する。</param>
    public RateStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>状態ファイルのパス。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 状態ファイルを読み込む。
    /// </summary>
    /// <returns>
    /// ファイルが存在せず、壊れている、または v2/v0.5.x いずれの形式としても解釈できない場合は <c>null</c>。
    /// この場合は呼び出し側で <c>initialRate</c> を用いたコールドスタートを行う。
    /// </returns>
    public RateState? Load()
    {
        if (!File.Exists(_filePath)) return null;

        string text;
        try
        {
            text = File.ReadAllText(_filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // v2 形式: version=SupportedVersion(現状は 2) + rate_tokens_per_sec + max_inflight
            // 未知バージョンは前方互換を仮定せず、下の互換判定にも一致しなければ null（コールドスタート）にフォールバックする。
            if (root.TryGetProperty("version", out var vEl)
                && vEl.ValueKind == JsonValueKind.Number
                && vEl.TryGetInt32(out var version)
                && version == SupportedVersion)
            {
                if (!TryGetDouble(root, "rate_tokens_per_sec", out var rate)) return null;
                // max_inflight は MinInflight >= 1 制約があるため、欠落・非数値・<=0 は無効としてコールドスタート扱いにする。
                if (!root.TryGetProperty("max_inflight", out var maxInflightEl)
                    || maxInflightEl.ValueKind != JsonValueKind.Number
                    || !maxInflightEl.TryGetInt32(out var maxInflight)
                    || maxInflight <= 0)
                {
                    return null;
                }
                return new RateState(rate, maxInflight, RateStateFormat.V2);
            }

            // v0.5.x 互換: rate キーのみ（file/sec 単位）。値は引き継ぎ、max_inflight は呼び出し側の既定値を使う。
            if (TryGetDouble(root, "rate", out var legacyRate))
            {
                return new RateState(legacyRate, MaxInflight: null, RateStateFormat.Legacy);
            }

            return null;
        }
        catch (JsonException)
        {
            // 壊れた JSON はコールドスタート扱いにする。エラー伝播させるとアプリ起動を妨げる。
            return null;
        }
    }

    /// <summary>状態を v2 形式で atomic に書き込む。</summary>
    public async Task SaveAsync(double rateTokensPerSec, int maxInflight, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var payload = new V2Payload
        {
            Version = SupportedVersion,
            RateTokensPerSec = rateTokensPerSec,
            MaxInflight = maxInflight,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var tmpPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
        // Move は atomic 置換。rename 中のクラッシュでも元ファイルは残る。
        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0.0;
        return element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private sealed class V2Payload
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("rate_tokens_per_sec")]
        public double RateTokensPerSec { get; set; }

        [JsonPropertyName("max_inflight")]
        public int MaxInflight { get; set; }

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;
    }
}

/// <summary>読込結果が由来するフォーマット。呼び出し側のログ表示用。</summary>
public enum RateStateFormat
{
    V2,
    Legacy,
}

/// <summary>
/// 読み込んだ状態。<see cref="MaxInflight"/> が <c>null</c> の場合は旧形式で値が含まれていなかったことを示す。
/// </summary>
public sealed record RateState(double RateTokensPerSec, int? MaxInflight, RateStateFormat Format);
