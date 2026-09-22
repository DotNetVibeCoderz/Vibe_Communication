using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoipNet.Enterprise.Crm;

/// <summary>Settings shared by the hosted CRM connectors.</summary>
public abstract class CrmConnectorOptions
{
    /// <summary>How many tickets a lookup returns.</summary>
    public int TicketLimit { get; set; } = 10;
}

/// <summary>Settings for HubSpot.</summary>
public sealed class HubSpotOptions : CrmConnectorOptions
{
    /// <summary>Private app token. HubSpot deprecated API keys, so this is a bearer token.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>API base, for a sandbox or a proxy.</summary>
    public Uri BaseUri { get; set; } = new("https://api.hubapi.com");
}

/// <summary>
/// HubSpot CRM over its v3 object API: contacts for callers, tickets for their open issues, and notes
/// on the contact's timeline.
/// </summary>
/// <param name="options">Account settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class HubSpotCrmConnector(HubSpotOptions options, HttpClient? httpClient = null) : ICrmConnector
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public async Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var number = CrmPhone.Normalize(phone);
        // Callers reach a company on either number, so both properties are searched.
        var search = new
        {
            filterGroups = new[]
            {
                new { filters = new[] { new { propertyName = "phone", @operator = "EQ", value = number } } },
                new { filters = new[] { new { propertyName = "mobilephone", @operator = "EQ", value = number } } },
            },
            properties = new[] { "firstname", "lastname", "email", "phone", "mobilephone", "hs_lead_status" },
            limit = 1,
        };

        using var response = await SendAsync(HttpMethod.Post, "/crm/v3/objects/contacts/search", search, cancellationToken).ConfigureAwait(false);
        using var document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var results = document.RootElement.GetProperty("results");
        if (results.GetArrayLength() == 0)
        {
            return null;
        }

        var contact = results[0];
        var properties = contact.GetProperty("properties");
        var name = string.Join(' ', new[] { Text(properties, "firstname"), Text(properties, "lastname") }.Where(p => p.Length > 0));
        return new CustomerRecord(
            contact.GetProperty("id").GetString() ?? string.Empty,
            name.Length > 0 ? name : Text(properties, "email"),
            Text(properties, "phone") is { Length: > 0 } p ? p : Text(properties, "mobilephone"),
            Text(properties, "email"),
            Text(properties, "hs_lead_status"),
            null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var search = new
        {
            filterGroups = new[]
            {
                new { filters = new[] { new { propertyName = "associations.contact", @operator = "EQ", value = customerId } } },
            },
            properties = new[] { "subject", "hs_pipeline_stage", "createdate" },
            sorts = new[] { new { propertyName = "createdate", direction = "DESCENDING" } },
            limit = options.TicketLimit,
        };

        using var response = await SendAsync(HttpMethod.Post, "/crm/v3/objects/tickets/search", search, cancellationToken).ConfigureAwait(false);
        using var document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var tickets = new List<TicketRecord>();
        foreach (var ticket in document.RootElement.GetProperty("results").EnumerateArray())
        {
            var properties = ticket.GetProperty("properties");
            tickets.Add(new TicketRecord(
                ticket.GetProperty("id").GetString() ?? string.Empty,
                customerId,
                Text(properties, "subject"),
                Text(properties, "hs_pipeline_stage"),
                Time(properties, "createdate")));
        }

        return tickets;
    }

    /// <inheritdoc/>
    public async Task<TicketRecord> CreateTicketAsync(string customerId, string subject, string description, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var payload = new
        {
            properties = new Dictionary<string, string>
            {
                ["subject"] = subject,
                ["content"] = description,
                ["hs_pipeline"] = "0",
                ["hs_pipeline_stage"] = "1",
            },
            // 16 is the contact-to-ticket association HubSpot defines.
            associations = new[]
            {
                new
                {
                    to = new { id = customerId },
                    types = new[] { new { associationCategory = "HUBSPOT_DEFINED", associationTypeId = 16 } },
                },
            },
        };

        using var response = await SendAsync(HttpMethod.Post, "/crm/v3/objects/tickets", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var properties = root.GetProperty("properties");
        return new TicketRecord(
            root.GetProperty("id").GetString() ?? string.Empty,
            customerId,
            Text(properties, "subject") is { Length: > 0 } s ? s : subject,
            Text(properties, "hs_pipeline_stage"),
            Time(properties, "createdate"));
    }

    /// <inheritdoc/>
    public async Task AddNoteAsync(string customerId, string note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var payload = new
        {
            properties = new Dictionary<string, string>
            {
                ["hs_note_body"] = note,
                ["hs_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            // 202 is the note-to-contact association.
            associations = new[]
            {
                new
                {
                    to = new { id = customerId },
                    types = new[] { new { associationCategory = "HUBSPOT_DEFINED", associationTypeId = 202 } },
                },
            },
        };

        using var response = await SendAsync(HttpMethod.Post, "/crm/v3/objects/notes", payload, cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "HubSpot", cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await CrmHttp.EnsureSuccessAsync(response, "HubSpot", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string Text(JsonElement properties, string name) =>
        properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static DateTimeOffset Time(JsonElement properties, string name) =>
        DateTimeOffset.TryParse(Text(properties, name), System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : DateTimeOffset.UtcNow;
}

/// <summary>Phone numbers as CRMs store them.</summary>
internal static class CrmPhone
{
    /// <summary>
    /// Strips a SIP URI down to the number a CRM would have stored: <c>sip:+628123@pbx</c> becomes
    /// <c>+628123</c>. Anything that is already a number is left alone.
    /// </summary>
    public static string Normalize(string phone)
    {
        var value = phone.Trim();
        if (value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(value.IndexOf(':', StringComparison.Ordinal) + 1)..];
        }

        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            value = value[..at];
        }

        return value.Trim('<', '>', ' ');
    }
}

/// <summary>Shared error handling, so a failure names the service and repeats what it said.</summary>
internal static class CrmHttp
{
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = body.Length > 500 ? body[..500] : body;
        throw new HttpRequestException($"{provider} returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}", null, response.StatusCode);
    }
}
