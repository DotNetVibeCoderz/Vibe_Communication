using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoipNet.Enterprise.Crm;

/// <summary>Settings for Dynamics 365.</summary>
public sealed class DynamicsOptions : CrmConnectorOptions
{
    /// <summary>Organisation URL, for example <c>https://acme.crm.dynamics.com</c>.</summary>
    public Uri OrganizationUri { get; set; } = new("https://example.crm.dynamics.com");

    /// <summary>OAuth access token for the Dataverse API.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Web API version path segment.</summary>
    public string ApiVersion { get; set; } = "v9.2";
}

/// <summary>
/// Dynamics 365 over the Dataverse Web API: contacts for callers, incidents (cases) for their issues,
/// and an annotation for call notes.
/// </summary>
/// <param name="options">Organisation settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class DynamicsCrmConnector(DynamicsOptions options, HttpClient? httpClient = null) : ICrmConnector
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public async Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var number = CrmPhone.Normalize(phone);
        var filter = $"telephone1 eq '{Escape(number)}' or mobilephone eq '{Escape(number)}'";
        var path = $"contacts?$select=contactid,fullname,telephone1,mobilephone,emailaddress1,description&$filter={Uri.EscapeDataString(filter)}&$top=1";
        using var document = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        var records = document.RootElement.GetProperty("value");
        if (records.GetArrayLength() == 0)
        {
            return null;
        }

        var contact = records[0];
        return new CustomerRecord(
            Text(contact, "contactid"),
            Text(contact, "fullname"),
            Text(contact, "telephone1") is { Length: > 0 } p ? p : Text(contact, "mobilephone"),
            Text(contact, "emailaddress1"),
            null,
            Text(contact, "description"));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var filter = $"_customerid_value eq {customerId}";
        var path = $"incidents?$select=incidentid,ticketnumber,title,statuscode,createdon&$filter={Uri.EscapeDataString(filter)}&$orderby=createdon desc&$top={options.TicketLimit}";
        using var document = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        var tickets = new List<TicketRecord>();
        foreach (var record in document.RootElement.GetProperty("value").EnumerateArray())
        {
            tickets.Add(new TicketRecord(
                Text(record, "ticketnumber") is { Length: > 0 } number ? number : Text(record, "incidentid"),
                customerId,
                Text(record, "title"),
                // Dynamics returns the status as a code; the formatted value is what a person reads.
                Formatted(record, "statuscode"),
                Time(record, "createdon")));
        }

        return tickets;
    }

    /// <inheritdoc/>
    public async Task<TicketRecord> CreateTicketAsync(string customerId, string subject, string description, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var payload = new Dictionary<string, object>
        {
            ["title"] = subject,
            ["description"] = description,
            ["caseorigincode"] = 1, // phone
            ["customerid_contact@odata.bind"] = $"/contacts({customerId})",
        };

        using var response = await SendAsync(HttpMethod.Post, "incidents", payload, cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "Dynamics 365", cancellationToken).ConfigureAwait(false);
        // A create returns the new row's URL in OData-EntityId rather than a body.
        var id = EntityId(response);
        return new TicketRecord(id, customerId, subject, "In Progress", DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async Task AddNoteAsync(string customerId, string note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        var payload = new Dictionary<string, object>
        {
            ["subject"] = "Call note",
            ["notetext"] = note,
            ["objectid_contact@odata.bind"] = $"/contacts({customerId})",
        };

        using var response = await SendAsync(HttpMethod.Post, "annotations", payload, cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "Dynamics 365", cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "Dynamics 365", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var uri = new Uri(options.OrganizationUri, $"/api/data/{options.ApiVersion}/{path}");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("OData-Version", "4.0");
        // Asks for the codes to come back with their labels, so a status reads as words.
        request.Headers.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pulls the identifier out of the OData-EntityId header a create returns.</summary>
    private static string EntityId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("OData-EntityId", out var values))
        {
            return string.Empty;
        }

        var url = values.FirstOrDefault() ?? string.Empty;
        var open = url.LastIndexOf('(');
        var close = url.LastIndexOf(')');
        return open >= 0 && close > open ? url[(open + 1)..close] : url;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Formatted(JsonElement record, string name) =>
        Text(record, $"{name}@OData.Community.Display.V1.FormattedValue");

    private static DateTimeOffset Time(JsonElement record, string name) =>
        DateTimeOffset.TryParse(Text(record, name), System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : DateTimeOffset.UtcNow;
}
