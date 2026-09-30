using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>Replaces what looks like a secret in a trace's text with <see cref="Redacted"/> (D44).</summary>
/// <remarks>
/// <para>
/// A trace carries a descriptor's text — patch values, refusals quoting a rule — and an operator pastes connection
/// strings and keys into places they should never be. These shapes are recognised: a provider token by its prefix; a
/// <c>Bearer</c> credential; the password of a URI's userinfo (<c>postgres://user:pass@host</c>); an unbroken
/// alphanumeric run of 32 or more (a key without a known prefix, a 32-hex Azure key); and a <c>name=value</c> or
/// <c>name: value</c> pair whose name is a secret's — <c>password</c>, <c>token</c>, <c>client_secret</c>, an
/// <c>…_API_KEY</c>, Azure's <c>AccountKey</c> and <c>SharedAccessKey</c> — with its value, quoted or not, replaced. A
/// <c>==</c> is a comparison, not a pair, so a CEL rule over a field called <c>token</c> is left alone.
/// </para>
/// <para>
/// <b>What a trace is for survives it.</b> A snake_case name always has an <c>_</c> and a pointer a <c>/</c>, so
/// neither is an unbroken run; CEL and the framework's refusals hold spaces and punctuation. A scrub is a net, not a
/// proof: it is why the trace is shown only to the operator who asked, and why the log carries less.
/// </para>
/// </remarks>
internal static partial class SecretScrub
{
    internal const string Redacted = "[redacted]";

    /// <summary>The text, each secret-like value replaced.</summary>
    internal static string Scrub(string text)
    {
        var scrubbed = Token().Replace(text, Redacted);
        scrubbed = Bearer().Replace(scrubbed, "${scheme}" + Redacted);
        scrubbed = UserInfo().Replace(scrubbed, "${user}" + Redacted + "@");
        scrubbed = LongRun().Replace(scrubbed, Redacted);
        return KeyValue().Replace(scrubbed, "${name}${separator}" + Redacted);
    }

    /// <summary>Scrubs every string value of <paramref name="node"/>, in place, and returns it.</summary>
    /// <remarks>The members are snapshotted before any is replaced, since a <see cref="JsonObject"/> cannot change while it is walked.</remarks>
    internal static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                ScrubMembers(members);
                return members;
            case JsonArray items:
                ScrubItems(items);
                return items;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return Scrub(text) is var scrubbed && scrubbed != text ? JsonValue.Create(scrubbed) : value;
            default:
                return node;
        }
    }

    /// <summary>Scrubs each member's value, and its key: a key is a string too (D44), and an unknown one is kept by name.</summary>
    private static void ScrubMembers(JsonObject members)
    {
        foreach (var key in members.Select(member => member.Key).ToList())
        {
            if (Scrub(members[key]) is var scrubbed && !ReferenceEquals(scrubbed, members[key]))
            {
                members[key] = scrubbed;
            }

            RenameIfSecret(members, key);
        }
    }

    /// <summary>Moves a member whose key looks like a secret under the scrubbed key, keeping its value.</summary>
    private static void RenameIfSecret(JsonObject members, string key)
    {
        var scrubbed = Scrub(key);
        if (scrubbed == key)
        {
            return;
        }

        var value = members[key];
        members.Remove(key);
        var renamed = scrubbed;
        for (var index = 2; members.ContainsKey(renamed); index++)
        {
            renamed = $"{scrubbed} {index}";
        }

        members[renamed] = value;
    }

    private static void ScrubItems(JsonArray items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (Scrub(items[index]) is var scrubbed && !ReferenceEquals(scrubbed, items[index]))
            {
                items[index] = scrubbed;
            }
        }
    }

    [GeneratedRegex(
        @"sk-(?:ant-|proj-|live-)?[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?<scheme>\bBearer\s+)[A-Za-z0-9._~+/=\-]{16,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?<user>\b[a-z][a-z0-9+.\-]*://[^/\s:@]+:)[^@\s/]+@", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"\b[A-Za-z0-9]{32,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongRun();

    /// <summary>
    /// A secret's name — after any <c>WORD_</c> prefixes, so <c>OPENAI_API_KEY</c> and <c>client_secret</c> match, where
    /// a <c>\b</c> after the underscore never would — then <c>=</c> (not <c>==</c>) or <c>:</c>, then the value, quoted or bare.
    /// </summary>
    [GeneratedRegex(
        @"(?<name>(?<![A-Za-z0-9])(?:[A-Za-z0-9]+_)*(?:password|passwd|pwd|secret|client[_-]?secret|api[_-]?key|access[_-]?token|token|accountkey|sharedaccesskey)\b)"
        + @"(?<separator>\s*(?:=(?!=)|:)\s*)(?:""[^""]*""|'[^']*'|[^;\s""',]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();
}
