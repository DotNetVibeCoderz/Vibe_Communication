using System.Net.Http.Json;
using System.Text.Json;

namespace VoipNet.Enterprise.Crm;

/// <summary>Settings for Odoo.</summary>
public sealed class OdooOptions : CrmConnectorOptions
{
    /// <summary>Server URL, for example <c>https://acme.odoo.com</c>.</summary>
    public Uri BaseUri { get; set; } = new("http://localhost:8069");

    /// <summary>Database name.</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>User id that the API key belongs to. Odoo's RPC takes the numeric id, not the login.</summary>
    public int UserId { get; set; } = 2;

    /// <summary>API key or password for that user.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model that holds tickets. Helpdesk is Enterprise; a Community install often uses <c>project.task</c>.</summary>
    public string TicketModel { get; set; } = "helpdesk.ticket";
}

/// <summary>
/// Odoo over its JSON-RPC endpoint: partners for callers, helpdesk tickets (or whatever
/// <see cref="OdooOptions.TicketModel"/> names) for their issues, and a logged message for call notes.
/// </summary>
/// <param name="options">Server settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class OdooCrmConnector(OdooOptions options, HttpClient? httpClient = null) : ICrmConnector
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public async Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var number = CrmPhone.Normalize(phone);
        var domain = new object[]
        {
            "|",
            new object[] { "phone", "=", number },
            new object[] { "mobile", "=", number },
        };
        var options0 = new Dictionary<string, object>
        {
            ["fields"] = new[] { "name", "phone", "mobile", "email", "comment" },
            ["limit"] = 1,
        };

        using var document = await CallAsync("res.partner", "search_read", [domain], options0, cancellationToken).ConfigureAwait(false);
        var records = Result(document);
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
        {
            return null;
        }

        var partner = records[0];
        return new CustomerRecord(
            Number(partner, "id"),
            Text(partner, "name"),
            Text(partner, "phone") is { Length: > 0 } p ? p : Text(partner, "mobile"),
            Text(partner, "email"),
            null,
            Text(partner, "comment"));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var partner = int.Parse(customerId, System.Globalization.CultureInfo.InvariantCulture);
        var domain = new object[] { new object[] { "partner_id", "=", partner } };
        var arguments = new Dictionary<string, object>
        {
            ["fields"] = new[] { "name", "stage_id", "create_date" },
            ["limit"] = options.TicketLimit,
            ["order"] = "create_date desc",
        };

        using var document = await CallAsync(options.TicketModel, "search_read", [domain], arguments, cancellationToken).ConfigureAwait(false);
        var tickets = new List<TicketRecord>();
        foreach (var record in Result(document).EnumerateArray())
        {
            tickets.Add(new TicketRecord(
                Number(record, "id"),
                customerId,
                Text(record, "name"),
                // A many2one comes back as [id, "label"]; the label is the readable one.
                Label(record, "stage_id"),
                Time(record, "create_date")));
        }

        return tickets;
    }

    /// <inheritdoc/>
    public async Task<TicketRecord> CreateTicketAsync(string customerId, string subject, string description, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var partner = int.Parse(customerId, System.Globalization.CultureInfo.InvariantCulture);
        var values = new Dictionary<string, object>
        {
            ["name"] = subject,
            ["description"] = description,
            ["partner_id"] = partner,
        };

        using var document = await CallAsync(options.TicketModel, "create", [values], null, cancellationToken).ConfigureAwait(false);
        var id = Result(document);
        return new TicketRecord(
            id.ValueKind == JsonValueKind.Number ? id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            customerId,
            subject,
            "New",
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async Task AddNoteAsync(string customerId, string note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var partner = int.Parse(customerId, System.Globalization.CultureInfo.InvariantCulture);
        var arguments = new Dictionary<string, object>
        {
            ["body"] = note,
            ["message_type"] = "comment",
            ["subtype_xmlid"] = "mail.mt_note",
        };

        // message_post is how a note lands on a partner's chatter, which is where staff look.
        using var document = await CallAsync("res.partner", "message_post", [new[] { partner }], arguments, cancellationToken).ConfigureAwait(false);
        _ = Result(document);
    }

    private async Task<JsonDocument> CallAsync(
        string model,
        string method,
        object[] positional,
        IDictionary<string, object>? keyword,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            method = "call",
            @params = new
            {
                service = "object",
                method = "execute_kw",
                args = new object[] { options.Database, options.UserId, options.ApiKey, model, method, positional, keyword ?? new Dictionary<string, object>() },
            },
            id = Random.Shared.Next(),
        };

        using var response = await _http.PostAsync(new Uri(options.BaseUri, "/jsonrpc"), JsonContent.Create(payload), cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "Odoo", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Unwraps a JSON-RPC reply. Odoo answers HTTP 200 even when the call failed, with the reason in
    /// an "error" member, so this is where a failure is actually noticed.
    /// </summary>
    private static JsonElement Result(JsonDocument document)
    {
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var detail)
                ? detail.GetString()
                : error.TryGetProperty("message", out var fallback) ? fallback.GetString() : "unknown error";
            throw new HttpRequestException($"Odoo returned an error: {message}");
        }

        return document.RootElement.TryGetProperty("result", out var result) ? result : default;
    }

    private static string Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Number(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Label(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 1
            ? value[1].GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset Time(JsonElement record, string name)
    {
        // Odoo writes "2026-03-02 09:15:00" in UTC, without a zone.
        var text = Text(record, name);
        return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
    }
}
