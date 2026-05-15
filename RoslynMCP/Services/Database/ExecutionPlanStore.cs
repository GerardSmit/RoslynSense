using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Xml;

namespace RoslynMCP.Services.Database;

public sealed class ExecutionPlanStore
{
    public const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    public const string ShowplanPrefix = "sp";

    public sealed record PlanSession(
        string Id,
        string Alias,
        string ProviderName,
        string Sql,
        Dictionary<string, object?>? Parameters,
        DateTime CapturedAt,
        PlanFormat Format,
        string Payload,
        XmlDocument? ParsedXml,
        XmlNamespaceManager? Namespaces,
        JsonNode? ParsedJson);

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (PlanSession Session, DateTime LastAccessed)> _sessions = new();

    public string Store(string alias, string providerName, string sql, Dictionary<string, object?>? parameters, PlanFormat format, string payload)
    {
        EvictExpired();

        var id = $"plan-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString()[..4]}";

        XmlDocument? doc = null;
        XmlNamespaceManager? ns = null;
        JsonNode? json = null;

        switch (format)
        {
            case PlanFormat.Xml:
                doc = new XmlDocument { XmlResolver = null };
                using (var sr = new StringReader(payload))
                using (var xr = XmlReader.Create(sr, new XmlReaderSettings
                {
                    XmlResolver = null,
                    DtdProcessing = DtdProcessing.Prohibit,
                }))
                {
                    doc.Load(xr);
                }
                ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace(ShowplanPrefix, ShowplanNamespace);
                break;
            case PlanFormat.Json:
                json = JsonNode.Parse(payload);
                break;
        }

        var session = new PlanSession(id, alias, providerName, sql, parameters, DateTime.UtcNow, format, payload, doc, ns, json);
        _sessions[id] = (session, DateTime.UtcNow);
        return id;
    }

    public PlanSession? Get(string id)
    {
        if (_sessions.TryGetValue(id, out var entry))
        {
            _sessions[id] = (entry.Session, DateTime.UtcNow);
            return entry.Session;
        }
        return null;
    }

    public IReadOnlyList<(string Id, string Alias, string ProviderName, PlanFormat Format, DateTime CapturedAt, int PayloadBytes)> List()
    {
        EvictExpired();
        return _sessions.Values
            .OrderByDescending(e => e.Session.CapturedAt)
            .Select(e => (e.Session.Id, e.Session.Alias, e.Session.ProviderName, e.Session.Format, e.Session.CapturedAt, e.Session.Payload.Length))
            .ToList();
    }

    private void EvictExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryGetValue(key, out var entry) && now - entry.LastAccessed > SessionTtl)
                _sessions.TryRemove(key, out _);
        }
    }
}
