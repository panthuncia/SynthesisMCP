using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;

namespace SafePatch.Mutagen;

/// <summary>
/// How this run's Synthesis reads and writes plugins, from its arguments. The worker's pipeline
/// writes its output this way, so <see cref="ModCodec"/> reads it back, and writes baselines, the same way.
/// </summary>
/// <param name="TargetLanguage">The language embedded strings are read as and written in (<c>--TargetLanguage</c>).</param>
/// <param name="Utf8EmbeddedStrings">Embedded localizable strings are UTF-8 (<c>--UseUtf8ForEmbeddedStrings</c>).</param>
/// <param name="Split">A plugin needing too many masters is written as parts (<c>--SplitIfMaxMastersExceeded</c>).</param>
/// <param name="LoadOrder">Orders the masters of split parts, as Synthesis's writer does.</param>
public sealed record PluginFormat(
    Language TargetLanguage = Language.English,
    bool Utf8EmbeddedStrings = false,
    bool Split = false,
    IReadOnlyList<ModKey>? LoadOrder = null)
{
    public static readonly PluginFormat Default = new();

    internal BinaryReadParameters ReadParameters => new()
    {
        StringsParam = new StringsReadParameters
        {
            TargetLanguage = TargetLanguage,
            EncodingProvider = Utf8EmbeddedStrings ? new Utf8Encodings() : null,
        },
    };

    internal BinaryWriteParameters WriteParameters => new()
    {
        TargetLanguageOverride = TargetLanguage,
        Encodings = Utf8EmbeddedStrings ? new EncodingBundle(NonTranslated: MutagenEncoding._1252, NonLocalized: MutagenEncoding._utf8) : null,
        MastersListOrdering = LoadOrder is null ? null : new MastersListOrderingByLoadOrder(LoadOrder),
    };

    /// <summary>What Synthesis's state factory uses to read with <c>--UseUtf8ForEmbeddedStrings</c>.</summary>
    private sealed class Utf8Encodings : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => MutagenEncoding._utf8;
    }
}
