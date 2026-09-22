using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoipNet.Enterprise.Crm;

/// <summary>Settings for Salesforce.</summary>
public sealed class SalesforceOptions : CrmConnectorOptions
{
    /// <summary>Instance URL, for example <c>https://acme.my.salesforce.com</c>.</summary>
    public Uri InstanceUri { get; set; } = new("https://login.salesforce.com");

    /// <summary>OAuth access token. Refreshing it is the application's business.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>API version path segment.</summary>
    public string ApiVersion { get; set; } = "v61.0";
}

/// <summary>
/// Salesforce over the REST API: contacts for callers, cases for their issues, and a task on the
/// contact's activity timeline for call notes.
/// </summary>
/// <param name="options">Org settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class SalesforceCrmConnector(SalesforceOptions options, HttpClient? httpClient = null) : ICrmConnector
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public async Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        var number = CrmPhone.Normalize(phone);
        var soql = $"SELECT Id, Name, Phone, MobilePhone, Email, Description FROM Contact WHERE Phone = {Quote(number)} OR MobilePhone = {Quote(number)} LIMIT 1";
        using var document = await QueryAsync(soql, cancellationToken).ConfigureAwait(false);
        var records = document.RootElement.GetProperty("records");
        if (records.GetArrayLength() == 0)
        {
            return null;
        }

        var contact = records[0];
        return new CustomerRecord(
            Text(contact, "Id"),
            Text(contact, "Name"),
            Text(contact, "Phone") is { Length: > 0 } p ? p : Text(contact, "MobilePhone"),
            Text(contact, "Email"),
            null,
            Text(contact, "Description"));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var soql = $"SELECT Id, CaseNumber, Subject, Status, CreatedDate FROM Case WHERE ContactId = {Quote(customerId)} ORDER BY CreatedDate DESC LIMIT {options.TicketLimit}";
        using var document = await QueryAsync(soql, cancellationToken).ConfigureAwait(false);
        var tickets = new List<TicketRecord>();
        foreach (var record in document.RootElement.GetProperty("records").EnumerateArray())
        {
            tickets.Add(new TicketRecord(
                Text(record, "CaseNumber") is { Length: > 0 } number ? number : Text(record, "Id"),
                customerId,
                Text(record, "Subject"),
                Text(record, "Status"),
                Time(record, "CreatedDate")));
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
            ContactId = customerId,
            Subject = subject,
            Description = description,
            Origin = "Phone",
            Status = "New",
        };

        using var response = await SendAsync(HttpMethod.Post, "sobjects/Case", payload, cancellationToken).ConfigureAwait(false);
        using var document = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return new TicketRecord(
            Text(document.RootElement, "id"),
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
        // A completed Task is what shows up on a contact's activity timeline, which is where a call
        // note belongs.
        var payload = new
        {
            WhoId = customerId,
            Subject = "Call note",
            Description = note,
            Status = "Completed",
            ActivityDate = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };

        using var response = await SendAsync(HttpMethod.Post, "sobjects/Task", payload, cancellationToken).ConfigureAwait(false);
        await CrmHttp.EnsureSuccessAsync(response, "Salesforce", cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> QueryAsync(string soql, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"query?q={Uri.EscapeDataString(soql)}", null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var uri = new Uri(options.InstanceUri, $"/services/data/{options.ApiVersion}/{path}");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await CrmHttp.EnsureSuccessAsync(response, "Salesforce", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Quotes a SOQL literal, because a phone number can contain an apostrophe as easily as a name.</summary>
    private static string Quote(string value) => $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";

    private static string Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static DateTimeOffset Time(JsonElement record, string name) =>
        DateTimeOffset.TryParse(Text(record, name), System.Globalization.CultureInfo.InvariantCulture, out var at) ? at : DateTimeOffset.UtcNow;
}
