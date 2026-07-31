using System.Text.Json.Nodes;

namespace BossCam.Core.Utilities;

/// <summary>
/// Deep-redacts secret-bearing leaf values (keys matching password-ish names) in arbitrary
/// JSON so settings snapshots and audit entries persisted to SQLite — or echoed back over the
/// API — never carry plaintext credentials. Defense-in-depth: adapters should avoid embedding
/// secrets in the first place, but this guarantees any residual password field is stripped at
/// the persistence boundary (see <c>SettingsService.ReadAsync</c> and the audit paths in
/// <c>SettingsService.WriteAsync</c>/<c>ExecuteMaintenanceAsync</c>).
/// </summary>
public static class SensitiveDataRedactor
{
    public const string RedactedMarker = "***REDACTED***";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey",
        "api_key"
    };

    /// <summary>
    /// Returns a deep clone of <paramref name="node"/> with sensitive leaf values replaced by
    /// <see cref="RedactedMarker"/>. Non-object/array nodes are deep-cloned untouched. Returns
    /// null for null input.
    /// </summary>
    public static JsonNode? Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var clone = new JsonObject();
                foreach (var property in obj)
                {
                    clone[property.Key] = IsSensitive(property.Key)
                        ? JsonValue.Create(RedactedMarker)
                        : Redact(property.Value);
                }

                return clone;
            }
            case JsonArray array:
            {
                var clone = new JsonArray();
                foreach (var item in array)
                {
                    clone.Add(Redact(item));
                }

                return clone;
            }
            default:
                return node?.DeepClone();
        }
    }

    private static bool IsSensitive(string key)
        => key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("passwd", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || SensitiveKeys.Contains(key);
}
