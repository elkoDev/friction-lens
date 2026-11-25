using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrictionLens.Api.Extraction;

public interface IEmbeddingProvider
{
    string ProviderName { get; }
    string ModelName { get; }
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}

public static class VectorMath
{
    public static byte[] Pack(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Unpack(byte[]? blob)
    {
        if (blob is null || blob.Length == 0 || blob.Length % sizeof(float) != 0)
            return [];
        var arr = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, arr, 0, blob.Length);
        return arr;
    }

    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 1e-6f ? dot / denom : 0f;
    }

    public static void NormalizeInPlace(float[] vec)
    {
        float sumSq = 0f;
        for (var i = 0; i < vec.Length; i++) sumSq += vec[i] * vec[i];
        var norm = MathF.Sqrt(sumSq);
        if (norm > 1e-6f)
        {
            for (var i = 0; i < vec.Length; i++) vec[i] /= norm;
        }
    }
}

/// <summary>
/// Zero-cost offline deterministic 128-dim L2-normalized feature-hashing embedding provider
/// (word tokens + character trigrams). Works with zero downloads and upgrades seamlessly to Ollama/OpenAI.
/// </summary>
public sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    private const int Dims = 128;
    public string ProviderName => "LocalHash";
    public string ModelName => "char3-word-hash-128d";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var vec = new float[Dims];
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(vec);

        var norm = text.ToLowerInvariant().Trim();
        var words = norm.Split([' ', '\t', '_', '-', '/', ',', '.', '|', ':'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var w in words)
        {
            AddFeature(vec, $"w:{w}", 2.0f);
            if (w.Length >= 3)
            {
                for (var i = 0; i <= w.Length - 3; i++)
                {
                    AddFeature(vec, $"c:{w.AsSpan(i, 3)}", 1.0f);
                }
            }
        }

        VectorMath.NormalizeInPlace(vec);
        return Task.FromResult(vec);
    }

    private static void AddFeature(float[] vec, string feature, float weight)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(feature));
        var idx = BitConverter.ToUInt32(hash, 0) % Dims;
        var sign = (hash[4] & 1) == 0 ? 1f : -1f;
        vec[idx] += sign * weight;
    }
}

/// <summary>
/// Zero-cost local Ollama embedding provider (default: nomic-embed-text).
/// </summary>
public sealed class OllamaEmbeddingProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : IEmbeddingProvider
{
    public string ProviderName => "Ollama";
    public string ModelName => configuration["Embedding:OllamaModel"] ?? "nomic-embed-text";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var baseUrl = (configuration["Extraction:OllamaBaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var client = httpClientFactory.CreateClient("LlmClient");
        using var resp = await client.PostAsJsonAsync($"{baseUrl}/api/embeddings", new { model = ModelName, prompt = text }, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var arr = json.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        VectorMath.NormalizeInPlace(arr);
        return arr;
    }
}

/// <summary>
/// Cloud OpenAI-compatible embedding provider (default: text-embedding-3-small).
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : IEmbeddingProvider
{
    public string ProviderName => "OpenAiCompatible";
    public string ModelName => configuration["Embedding:CloudModel"] ?? "text-embedding-3-small";

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var apiKey = configuration["Extraction:ApiKey"] ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM_API_KEY / Extraction:ApiKey is required for cloud embeddings.");

        var baseUrl = (configuration["Extraction:CloudBaseUrl"] ?? "https://api.openai.com/v1").TrimEnd('/');
        var client = httpClientFactory.CreateClient("LlmClient");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(new { model = ModelName, input = text });

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        var arr = json.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        VectorMath.NormalizeInPlace(arr);
        return arr;
    }
}
