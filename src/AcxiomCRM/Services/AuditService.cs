using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Services;

// Writes audit records (AUD-01/02). Every module calls this after its own save.
public class AuditService(AppDbContext db, IHttpContextAccessor http)
{
    // Any property whose name contains one of these is dropped from OldValue/NewValue (AUD-05).
    static readonly string[] SecretWords = ["password", "hash", "token", "secret", "securitystamp", "concurrencystamp"];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
    };

    // userId overrides the signed-in user, for events like a failed login where nobody is signed in yet.
    public async Task LogAsync(string module, string action, string entityName, string? recordId,
        object? oldValue = null, object? newValue = null, string result = "Success",
        string? details = null, string? userId = null)
    {
        var context = http.HttpContext;
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId ?? context?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            Module = module,
            Action = action,
            EntityName = entityName,
            RecordId = recordId,
            OldValue = ToJson(oldValue),
            NewValue = ToJson(newValue),
            Result = result,
            Details = details is { Length: > 2000 } ? details[..2000] : details,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            CreatedDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    // Audit trail of one record, newest first, for the record's Details page (CUS-07 and the other modules).
    public async Task<List<HistoryEntry>> HistoryAsync(string entityName, string recordId)
    {
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.RecordId == recordId)
            .OrderByDescending(a => a.AuditLogId)
            .Select(a => new { a.CreatedDate, UserName = a.User!.Name, a.Action, a.OldValue, a.NewValue })
            .ToListAsync();
        return rows.Select(h => new HistoryEntry(h.CreatedDate, h.UserName, h.Action, DescribeChanges(h.OldValue, h.NewValue))).ToList();
    }

    // "Field: old → new" for each field that differs; used by record history pages.
    public static string? DescribeChanges(string? oldJson, string? newJson)
    {
        if (oldJson is null || newJson is null) return null;
        if (JsonNode.Parse(oldJson) is not JsonObject before || JsonNode.Parse(newJson) is not JsonObject after) return null;

        var changes = after
            .Where(p => !JsonNode.DeepEquals(p.Value, before[p.Key]))
            .Select(p => $"{p.Key}: {Show(before[p.Key])} → {Show(p.Value)}")
            .ToList();
        return changes.Count == 0 ? null : string.Join("; ", changes);

        static string Show(JsonNode? n) => n is null ? "(empty)" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
    }

    static string? ToJson(object? value)
    {
        if (value is null) return null;
        var node = JsonSerializer.SerializeToNode(value, JsonOptions);
        RemoveSecrets(node);
        return node?.ToJsonString();
    }

    static void RemoveSecrets(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase))) obj.Remove(key);
                else RemoveSecrets(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array) RemoveSecrets(item);
        }
    }
}
