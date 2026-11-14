using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kinshout.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Kinshout.Api.Services;

public class AdvertModerationService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenAiSettings> options,
    ILogger<AdvertModerationService> logger) : IAdvertModerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Dictionary<string, string> ModerationCategoryViolations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sexual"] = "sexual_content",
        ["sexual/minors"] = "sexual_content",
        ["hate"] = "hate_content",
        ["hate/threatening"] = "hate_content",
        ["harassment"] = "harassment",
        ["harassment/threatening"] = "harassment",
        ["violence"] = "violence",
        ["violence/graphic"] = "violence",
    };

    private static readonly string[] TextFallbackBlockPatterns =
    [
        @"\b(porno|pornograph|sexe\s+payant|escort|nud(?:e|ité)|onlyfans|plan\s+cul)\b",
        @"\b(prostitu|massage\s+érotique|massage\s+erotique)\b",
        @"\b(haine\s+racial(?:e|)|supremac|génocide|genocide|nazi(?:sme)?|antisémit|antisemit)\b",
        @"\b(harc[eè]lement\s+(sexuel|racial)|insulte\s+racial)\b",
    ];

    private readonly OpenAiSettings _settings = options.Value;

    public Task EnsureTextAllowedAsync(string text, CancellationToken ct = default)
    {
        if (!_settings.ModerationEnabled)
        {
            logger.LogWarning("Content moderation is disabled; skipping text check.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        return EnsureTextAllowedCoreAsync(text, ct);
    }

    public Task EnsureDiscussionTextAllowedAsync(string text, CancellationToken ct = default)
    {
        if (!_settings.DiscussionModerationEnabled)
        {
            logger.LogWarning("Discussion moderation is disabled; skipping text check.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        return EnsureTextAllowedCoreAsync(text, ct);
    }

    private async Task EnsureTextAllowedCoreAsync(string text, CancellationToken ct)
    {
        var result = string.IsNullOrWhiteSpace(_settings.ApiKey)
            ? FallbackTextCheck(text)
            : await ModerateTextWithOpenAiAsync(text, ct);

        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Contenu texte non autorisé.");
    }

    public async Task EnsureImageAllowedAsync(Stream imageStream, string contentType, CancellationToken ct = default)
    {
        if (!IsMediaModerationActive())
        {
            logger.LogWarning("Image moderation is disabled; skipping image check.");
            return;
        }

        await using var buffer = new MemoryStream();
        await imageStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Image vide.");

        var result = await ModerateImageWithOpenAiAsync(buffer.ToArray(), contentType, ct);
        if (!result.Allowed)
            throw new AdvertModerationException(result.Reason ?? "Photo non autorisée.");
    }

    public async Task EnsureVideoAllowedAsync(
        Stream videoStream,
        string contentType,
        Stream? posterStream = null,
        string? posterContentType = null,
        CancellationToken ct = default)
    {
        if (!IsMediaModerationActive())
        {
            logger.LogWarning("Video moderation is disabled; skipping video check.");
            return;
        }

        if (posterStream is not null)
        {
            await EnsureImageAllowedAsync(posterStream, posterContentType ?? "image/jpeg", ct);
        }

        await using var videoBuffer = new MemoryStream();
        await videoStream.CopyToAsync(videoBuffer, ct);
        if (videoBuffer.Length == 0)
            throw new ArgumentException("Vidéo vide.");

        videoBuffer.Position = 0;
        var frames = await TryExtractVideoFramesAsync(videoBuffer, ct);
        if (frames.Count == 0)
        {
            if (posterStream is not null)
                return;

            logger.LogWarning(
                "Could not sample video frames for moderation (ffmpeg unavailable); allowing upload without frame check.");
            return;
        }

        foreach (var frame in frames)
        {
            var result = await ModerateImageWithOpenAiAsync(frame, "image/jpeg", ct);
            if (!result.Allowed)
                throw new AdvertModerationException(result.Reason ?? "Vidéo non autorisée.");
        }
    }

    private bool IsMediaModerationActive() =>
        _settings.ModerationEnabled
        && _settings.ImageModerationEnabled
        && !string.IsNullOrWhiteSpace(_settings.ApiKey);

    public async Task EnsureDocumentAllowedAsync(
        Stream fileStream,
        string contentType,
        string fileName,
        CancellationToken ct = default)
    {
        if (!_settings.ModerationEnabled)
        {
            logger.LogWarning("Content moderation is disabled; skipping document check.");
            return;
        }

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
            throw new ArgumentException("Fichier vide.");

        var extension = Path.GetExtension(fileName);
        var extracted = ExtractDocumentText(buffer.ToArray(), extension);
        var textToModerate = string.IsNullOrWhiteSpace(extracted)
            ? fileName
            : $"{fileName}\n{extracted}";

        if (textToModerate.Length > 8000)
            textToModerate = textToModerate[..8000];

        await EnsureTextAllowedCoreAsync(textToModerate, ct);
    }

    private async Task<AdvertModerationCheckResult> ModerateTextWithOpenAiAsync(string text, CancellationToken ct)
    {
        var client = CreateClient();
        var body = new { model = _settings.ModerationModel, input = text };

        using var response = await client.PostAsync(
            "https://api.openai.com/v1/moderations",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var result = doc.RootElement.GetProperty("results")[0];
        if (!result.GetProperty("flagged").GetBoolean())
            return Allowed();

        var violations = new List<string>();
        if (result.TryGetProperty("categories", out var categories))
        {
            foreach (var category in ModerationCategoryViolations.Keys)
            {
                if (categories.TryGetProperty(category, out var flagged)
                    && flagged.GetBoolean()
                    && ModerationCategoryViolations.TryGetValue(category, out var violation))
                {
                    violations.Add(violation);
                }
            }
        }

        if (violations.Count == 0)
            violations.Add("policy_violation");

        violations = violations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        logger.LogWarning("Text blocked by OpenAI moderation: {Violations}", string.Join(", ", violations));
        return Blocked(BuildTextBlockReason(violations), violations);
    }

    private async Task<AdvertModerationCheckResult> ModerateImageWithOpenAiAsync(
        byte[] imageBytes,
        string contentType,
        CancellationToken ct)
    {
        var client = CreateClient();
        var dataUrl = ToDataUrl(imageBytes, contentType);
        var prompt = """
            Tu es la modération média de Kinoiserie (discussions à Kinshasa / RDC).
            Analyse cette image (photo ou image extraite d'une vidéo) et réponds UNIQUEMENT en JSON:
            {
              "allowed": true/false,
              "violations": ["sexual_content", "hate_content", "political_content"],
              "reason": "explication courte en français pour l'utilisateur"
            }

            Refuser (allowed=false) UNIQUEMENT si:
            1) Contenu adulte / obscénité / pornographie (violations: sexual_content)
               - nudité sexuelle, pornographie, contenu érotique explicite, obscénité sexuelle
            2) Haine / racisme (violations: hate_content)
               - symboles haineux, racisme, discrimination ethnique ou religieuse agressive
            3) Politique congolaise / guerre à l'est (violations: political_content)
               - figures ou familles politiques congolaises (ex. Kabila, Olive Lembe / Maman wa Roho,
                 Tshisekedi / Fatshi, Fayulu, Katumbi, Kamerhe, Jean-Pierre Bemba / MLC,
                 Corneille Nangaa / Naanga, UDPS, FCC, etc.)
               - campagne électorale, partis, gouvernement, présidence, parlement congolais
               - guerre / groupes armés à l'est (M23, AFC, FARDC, FDLR, CODECO, ADF, FRN,
                 rebelles, milices, conflit Rwanda / Kigali lié à la RDC)

            Accepter (allowed=true) le reste de la vie quotidienne à Kinshasa: musique, sport,
            nourriture, quartier, humour, culture, écoles, commerce, nature, famille non politique.
            N'invente pas de refus pour "photo non authentique" ou banque d'images.

            En cas de doute léger hors des 3 catégories ci-dessus, autorise.
            En cas de doute sur politique / adulte / haine, refuse.
            """;

        var body = new
        {
            model = _settings.VisionModel,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
            temperature = 0.1,
            response_format = new { type = "json_object" },
        };

        using var response = await client.PostAsync(
            "https://api.openai.com/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var json = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var parsed = JsonSerializer.Deserialize<VisionModerationResponse>(json, JsonOptions)
            ?? new VisionModerationResponse(false, ["policy_violation"], "Média non autorisé.");

        if (parsed.Allowed)
            return Allowed();

        var reason = parsed.Reason ?? BuildDefaultImageBlockReason(parsed.Violations);
        logger.LogWarning(
            "Image blocked: {Reason} ({Violations})",
            reason,
            string.Join(", ", parsed.Violations));

        return Blocked(reason, parsed.Violations.Count > 0 ? parsed.Violations : ["policy_violation"]);
    }

    /// <summary>
    /// Samples up to two JPEG frames (early + mid) via ffmpeg when available on PATH.
    /// </summary>
    private async Task<IReadOnlyList<byte[]>> TryExtractVideoFramesAsync(
        Stream videoStream,
        CancellationToken ct)
    {
        var frames = new List<byte[]>();
        var tempDir = Path.Combine(Path.GetTempPath(), "kinoiserie-mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var videoPath = Path.Combine(tempDir, "input.bin");
        var framePaths = new[]
        {
            (Path.Combine(tempDir, "frame1.jpg"), "00:00:01"),
            (Path.Combine(tempDir, "frame2.jpg"), "00:00:05"),
        };

        try
        {
            await using (var file = File.Create(videoPath))
            {
                await videoStream.CopyToAsync(file, ct);
            }

            foreach (var (framePath, seek) in framePaths)
            {
                ct.ThrowIfCancellationRequested();
                var ok = await RunFfmpegFrameAsync(videoPath, framePath, seek, ct);
                if (!ok || !File.Exists(framePath))
                    continue;

                var bytes = await File.ReadAllBytesAsync(framePath, ct);
                if (bytes.Length > 0)
                    frames.Add(bytes);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Video frame extraction for moderation failed.");
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }

        return frames;
    }

    private async Task<bool> RunFfmpegFrameAsync(
        string videoPath,
        string framePath,
        string seek,
        CancellationToken ct)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    ArgumentList =
                    {
                        "-hide_banner",
                        "-loglevel",
                        "error",
                        "-ss",
                        seek,
                        "-i",
                        videoPath,
                        "-frames:v",
                        "1",
                        "-q:v",
                        "3",
                        "-y",
                        framePath,
                    },
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "ffmpeg not available for video moderation at {Seek}", seek);
            return false;
        }
    }

    private static AdvertModerationCheckResult FallbackTextCheck(string text)
    {
        var normalized = text.ToLowerInvariant();
        foreach (var pattern in TextFallbackBlockPatterns)
        {
            if (!Regex.IsMatch(normalized, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;

            var isHatePattern = pattern.Contains("haine", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("harc", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("supremac", StringComparison.OrdinalIgnoreCase)
                || pattern.Contains("nazi", StringComparison.OrdinalIgnoreCase);
            var violations = new List<string> { isHatePattern ? "hate_content" : "sexual_content" };

            return Blocked(BuildTextBlockReason(violations), violations);
        }

        return Allowed();
    }

    private static string BuildTextBlockReason(IReadOnlyList<string> violations)
    {
        if (violations.Contains("sexual_content"))
            return "Ce contenu contient un texte sexuel ou adulte non autorisé sur Kinshout.";

        if (violations.Contains("hate_content"))
            return "Ce contenu contient des propos discriminatoires ou haineux non autorisés sur Kinshout.";

        if (violations.Contains("harassment"))
            return "Ce contenu contient du harcèlement non autorisé sur Kinshout.";

        if (violations.Contains("violence"))
            return "Ce contenu contient de la violence non autorisée sur Kinshout.";

        return "Ce contenu ne respecte pas les règles de Kinshout.";
    }

    private static string BuildDefaultImageBlockReason(IReadOnlyList<string> violations)
    {
        if (violations.Contains("sexual_content"))
            return "Ce média contient un contenu sexuel, obscène ou adulte non autorisé.";

        if (violations.Contains("hate_content"))
            return "Ce média contient un contenu discriminatoire, raciste ou haineux non autorisé.";

        if (violations.Contains("political_content"))
            return "Ce média évoque la politique congolaise ou la guerre à l'est, non autorisées sur Kinoiserie.";

        return "Ce média ne respecte pas les règles de Kinoiserie.";
    }

    private static string ExtractDocumentText(byte[] bytes, string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pdf" => ExtractPdfText(bytes),
            ".docx" => ExtractDocxText(bytes),
            ".doc" => ExtractBinaryDocumentText(bytes),
            _ => string.Empty,
        };
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        var content = Encoding.Latin1.GetString(bytes);
        var parts = new List<string>();

        foreach (Match match in Regex.Matches(content, @"\((?:\\.|[^\\)])*\)\s*Tj"))
            parts.Add(UnescapePdfText(match.Groups[1].Value));

        foreach (Match match in Regex.Matches(content, @"\[((?:\\.|[^\]])*)\]\s*TJ"))
        {
            foreach (Match textMatch in Regex.Matches(match.Groups[1].Value, @"\((?:\\.|[^\\)])*\)"))
                parts.Add(UnescapePdfText(textMatch.Groups[0].Value.Trim('(', ')')));
        }

        return string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string UnescapePdfText(string value) =>
        value
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\r", " ", StringComparison.Ordinal)
            .Replace("\\t", " ", StringComparison.Ordinal);

    private static string ExtractDocxText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        if (entry is null)
            return string.Empty;

        using var reader = new StreamReader(entry.Open());
        var xml = reader.ReadToEnd();
        return Regex.Replace(xml, "<[^>]+>", " ");
    }

    private static string ExtractBinaryDocumentText(byte[] bytes)
    {
        var content = Encoding.Latin1.GetString(bytes);
        var matches = Regex.Matches(content, @"[\x20-\x7E]{4,}");
        return string.Join(' ', matches.Select(m => m.Value));
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient("OpenAI");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        return client;
    }

    private static string ToDataUrl(byte[] bytes, string contentType)
    {
        var mime = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType.Split(';')[0].Trim();
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static AdvertModerationCheckResult Allowed() => new(true, null, []);

    private static AdvertModerationCheckResult Blocked(string reason, IReadOnlyList<string> violations) =>
        new(false, reason, violations);

    private sealed record VisionModerationResponse(
        bool Allowed,
        List<string> Violations,
        string? Reason);
}
