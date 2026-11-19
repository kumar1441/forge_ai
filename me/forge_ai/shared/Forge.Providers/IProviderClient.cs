using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Forge.Providers
{
    /// <summary>A single chat turn. Role is "system", "user", or "assistant".</summary>
    public class ChatMessage
    {
        public string Role;
        public string Content;
    }

    /// <summary>Per-call overrides. Null fields defer to the provider default.</summary>
    public class ProviderOptions
    {
        public int? MaxTokens;
        public double? Temperature;
        public string Model;
        public string BaseUrlOverride;
    }

    /// <summary>A successful completion. Never fabricated: on failure CompleteAsync throws or returns null.</summary>
    public class ProviderResult
    {
        public string Text;
        public long TokIn;
        public long TokOut;
        public string Model;
    }

    /// <summary>Typed failure for network, HTTP-status, timeout, or parse errors from an LLM provider.</summary>
    public class ProviderException : System.Exception
    {
        public ProviderException(string message) : base(message) { }
        public ProviderException(string message, System.Exception inner) : base(message, inner) { }
    }

    /// <summary>A bring-your-own-key LLM backend.</summary>
    public interface IProviderClient
    {
        string Id { get; }
        Task<ProviderResult> CompleteAsync(IReadOnlyList<ChatMessage> messages, ProviderOptions opts, CancellationToken ct);
    }
}
