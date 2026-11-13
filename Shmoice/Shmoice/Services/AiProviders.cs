using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>How to talk to an AI service.</summary>
public enum AiApiKind
{
    /// <summary>Anthropic's own API (Claude), through the Anthropic SDK.</summary>
    Anthropic,
    /// <summary>
    /// The "chat completions" API that OpenAI introduced and nearly every other service and local
    /// model runner (Ollama, LM Studio, llama.cpp, vLLM, OpenRouter, Groq…) also speaks.
    /// </summary>
    OpenAiCompatible,
}

/// <summary>An AI service the user can pick in Settings.</summary>
/// <param name="DefaultBaseUrl">Address of its API; the user can change it. Empty for Custom.</param>
/// <param name="DefaultModel">Used when the user hasn't chosen a model. Empty = the user must choose one.</param>
/// <param name="Local">Runs on the user's own PC or network: usually no key, and nothing leaves the machine.</param>
/// <param name="Note">One line of setup help shown under the picker.</param>
public record AiProvider(string Id, string Name, AiApiKind Kind, string DefaultBaseUrl, string DefaultModel,
    bool Local, string Note, string[] SuggestedModels)
{
    public override string ToString() => Name;
}

/// <summary>Everything needed to make a request: which API, where, with what key and model.</summary>
/// <param name="IsOllama">Ollama: talked to through its own API, which (unlike its OpenAI-style one) lets us give the model more room.</param>
public record AiConnection(AiApiKind Kind, string BaseUrl, string ApiKey, string Model, string ProviderName, bool IsOllama = false);

public static class AiProviders
{
    public const string AnthropicId = "anthropic", CustomId = "custom", OllamaId = "ollama";

    public static IReadOnlyList<AiProvider> All { get; } =
    [
        new(AnthropicId, "Anthropic (Claude)", AiApiKind.Anthropic, "", "claude-opus-5-5", false,
            "Needs an API key from console.anthropic.com → API Keys.",
            ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2"]),
        new("openai", "OpenAI", AiApiKind.OpenAiCompatible, "https://api.openai.com/v1", "", false,
            "Needs an API key from platform.openai.com. Press Find models to pick one.", []),
        new("gemini", "Google Gemini", AiApiKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", "", false,
            "Needs an API key from aistudio.google.com. Press Find models to pick one.", []),
        new("openrouter", "OpenRouter", AiApiKind.OpenAiCompatible, "https://openrouter.ai/api/v1", "", false,
            "One key for models from many companies. Needs an API key from openrouter.ai.", []),
        new("groq", "Groq", AiApiKind.OpenAiCompatible, "https://api.groq.com/openai/v1", "", false,
            "Needs an API key from console.groq.com.", []),
        new("mistral", "Mistral", AiApiKind.OpenAiCompatible, "https://api.mistral.ai/v1", "", false,
            "Needs an API key from console.mistral.ai.", []),
        new("deepseek", "DeepSeek", AiApiKind.OpenAiCompatible, "https://api.deepseek.com/v1", "", false,
            "Needs an API key from platform.deepseek.com.", []),
        new("xai", "xAI (Grok)", AiApiKind.OpenAiCompatible, "https://api.x.ai/v1", "", false,
            "Needs an API key from console.x.ai.", []),
        new(OllamaId, "Ollama (on this PC)", AiApiKind.OpenAiCompatible, "http://localhost:11434/v1", "", true,
            "Runs models on your own PC: free, private, no key. Ollama must be running, with a model downloaded (e.g. “ollama pull llama3.2”).", []),
        new("lmstudio", "LM Studio (on this PC)", AiApiKind.OpenAiCompatible, "http://localhost:1234/v1", "", true,
            "Runs models on your own PC: free, private, no key. In LM Studio, load a model and start its local server.", []),
        new(CustomId, "Custom (any OpenAI-compatible address)", AiApiKind.OpenAiCompatible, "", "", true,
            "For anything else that speaks the OpenAI chat API: llama.cpp, vLLM, LocalAI, Jan, text-generation-webui, a company gateway, or another PC on your network. The key is optional.", []),
    ];

    public static AiProvider Get(string? id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>The saved details for a service, created empty the first time it's asked for.</summary>
    public static AiProviderConfig ConfigFor(AppSettings settings, AiProvider provider)
    {
        if (!settings.AiProviders.TryGetValue(provider.Id, out var config))
            settings.AiProviders[provider.Id] = config = new AiProviderConfig();
        return config;
    }

    /// <summary>The key to use: the saved one, or for Anthropic the ANTHROPIC_API_KEY environment variable.</summary>
    public static string ApiKey(AiProvider provider, AiProviderConfig config)
    {
        var saved = SecretStore.Unprotect(config.EncryptedApiKey);
        if (saved.Length > 0 || provider.Id != AnthropicId) return saved;
        return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
    }

    /// <summary>
    /// The connection for the service chosen in Settings. Throws with a message saying what's missing
    /// (key, address or model) when it isn't fully set up; <paramref name="needModel"/> is false when
    /// only listing the service's models.
    /// </summary>
    public static AiConnection Connect(AppSettings settings, bool needModel = true)
    {
        var provider = Get(settings.AiProvider);
        var config = ConfigFor(settings, provider);
        var key = ApiKey(provider, config);
        var model = config.Model.Trim().Length > 0 ? config.Model.Trim() : provider.DefaultModel;
        var baseUrl = provider.Kind == AiApiKind.OpenAiCompatible
            ? NormalizeBaseUrl(config.BaseUrl.Trim().Length > 0 ? config.BaseUrl : provider.DefaultBaseUrl)
            : "";

        const string where = " Set it up in Settings → AI.";
        if (provider.Kind == AiApiKind.OpenAiCompatible && baseUrl.Length == 0)
            throw new InvalidOperationException($"{provider.Name} needs the address of the AI service." + where);
        if (!provider.Local && key.Length == 0)
            throw new InvalidOperationException($"{provider.Name} needs an API key." + where);
        if (needModel && model.Length == 0)
            throw new InvalidOperationException($"Choose which {provider.Name} model to use." + where);
        return new AiConnection(provider.Kind, baseUrl, key, model, provider.Name, IsOllama: provider.Id == OllamaId);
    }

    /// <summary>
    /// Tidies an address someone typed or pasted: adds http(s)://, drops a trailing slash or a pasted
    /// "/chat/completions", and adds "/v1" to a bare host like localhost:8080. "" if it isn't a web address.
    /// </summary>
    public static string NormalizeBaseUrl(string text)
    {
        var url = text.Trim().TrimEnd('/');
        if (url.Length == 0) return "";
        if (!url.Contains("://"))
        {
            // Plain http for this PC and home-network addresses, which rarely have certificates.
            var host = url.Split('/', ':')[0];
            var local = host is "localhost" || host.EndsWith(".local") || System.Net.IPAddress.TryParse(host, out _);
            url = (local ? "http://" : "https://") + url;
        }
        foreach (var suffix in new[] { "/chat/completions", "/completions", "/models" })
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) url = url[..^suffix.Length];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0) return "";
        return uri.AbsolutePath is "" or "/" ? url.TrimEnd('/') + "/v1" : url;
    }
}
