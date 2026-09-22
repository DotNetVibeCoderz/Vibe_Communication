using System.Net;
using System.Text;
using VoipNet.Enterprise.Crm;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Protocol tests for the CRM connectors against canned HTTP responses.</summary>
public sealed class CrmTests
{
    private sealed class CannedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            Requests.Add(request);
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task HubSpotFindsAContactBySipUri()
    {
        var handler = new CannedHandler((_, _) => Json("""
            {"results":[{"id":"501","properties":{"firstname":"Sari","lastname":"Dewi","email":"sari@example.com","phone":"+628123456","hs_lead_status":"OPEN"}}]}
            """));
        var crm = new HubSpotCrmConnector(new HubSpotOptions { AccessToken = "pat-1" }, new HttpClient(handler));

        var customer = await crm.FindByPhoneAsync("sip:+628123456@pbx.example.com");

        Assert.NotNull(customer);
        Assert.Equal("501", customer.Id);
        Assert.Equal("Sari Dewi", customer.Name);
        Assert.Equal("sari@example.com", customer.Email);
        Assert.Equal("OPEN", customer.Tier);
        // The SIP URI is reduced to the number a CRM would have stored, and both fields are searched.
        // (System.Text.Json escapes the leading plus, so the digits are what to look for.)
        Assert.Contains("628123456", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("mobilephone", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal("Bearer pat-1", handler.Requests[0].Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task HubSpotReturnsNothingWhenTheCallerIsUnknown()
    {
        var handler = new CannedHandler((_, _) => Json("""{"results":[]}"""));
        var crm = new HubSpotCrmConnector(new HubSpotOptions { AccessToken = "pat-1" }, new HttpClient(handler));

        Assert.Null(await crm.FindByPhoneAsync("+628999"));
    }

    [Fact]
    public async Task HubSpotOpensATicketAssociatedWithTheContact()
    {
        var handler = new CannedHandler((_, _) => Json("""
            {"id":"9001","properties":{"subject":"Paket terlambat","hs_pipeline_stage":"1","createdate":"2026-03-02T09:15:00Z"}}
            """));
        var crm = new HubSpotCrmConnector(new HubSpotOptions { AccessToken = "pat-1" }, new HttpClient(handler));

        var ticket = await crm.CreateTicketAsync("501", "Paket terlambat", "Sudah seminggu belum sampai.");

        Assert.Equal("9001", ticket.Id);
        Assert.Equal("501", ticket.CustomerId);
        Assert.Equal("Paket terlambat", ticket.Subject);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 15, 0, TimeSpan.Zero), ticket.CreatedAt);
        Assert.Contains("\"associationTypeId\":16", handler.Bodies[0], StringComparison.Ordinal);
        Assert.EndsWith("/crm/v3/objects/tickets", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HubSpotErrorsCarryTheServiceMessage()
    {
        var handler = new CannedHandler((_, _) => Json("""{"message":"invalid token"}""", HttpStatusCode.Unauthorized));
        var crm = new HubSpotCrmConnector(new HubSpotOptions { AccessToken = "bad" }, new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => crm.FindByPhoneAsync("+62811"));
        Assert.Contains("HubSpot returned 401", failure.Message, StringComparison.Ordinal);
        Assert.Contains("invalid token", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SalesforceQueriesContactsAndCases()
    {
        var handler = new CannedHandler((request, _) =>
            request.RequestUri!.Query.Contains("FROM%20Contact", StringComparison.Ordinal)
                ? Json("""{"records":[{"Id":"003xx","Name":"Budi","Phone":"+628123","Email":"budi@example.com"}]}""")
                : Json("""{"records":[{"Id":"500xx","CaseNumber":"00001234","Subject":"Tagihan dobel","Status":"Working","CreatedDate":"2026-03-01T02:00:00.000+0000"}]}"""));
        var crm = new SalesforceCrmConnector(
            new SalesforceOptions { InstanceUri = new Uri("https://acme.my.salesforce.com"), AccessToken = "tok" },
            new HttpClient(handler));

        var customer = await crm.FindByPhoneAsync("+628123");
        Assert.Equal("003xx", customer!.Id);

        var tickets = await crm.RecentTicketsAsync("003xx");
        var ticket = Assert.Single(tickets);
        Assert.Equal("00001234", ticket.Id);
        Assert.Equal("Working", ticket.Status);

        Assert.Contains("/services/data/v61.0/query", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("Bearer tok", handler.Requests[0].Headers.Authorization?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SalesforceQuotesANumberThatContainsAnApostrophe()
    {
        var handler = new CannedHandler((_, _) => Json("""{"records":[]}"""));
        var crm = new SalesforceCrmConnector(new SalesforceOptions { AccessToken = "tok" }, new HttpClient(handler));

        await crm.FindByPhoneAsync("o'brien");

        // The apostrophe has to be escaped, or the query is no longer the query we meant.
        var query = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        Assert.Contains(@"Phone = 'o\'brien'", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicsReadsCasesAndTheirLabels()
    {
        var handler = new CannedHandler((request, _) =>
            request.RequestUri!.AbsolutePath.Contains("contacts", StringComparison.Ordinal)
                ? Json("""{"value":[{"contactid":"c-1","fullname":"Rina","telephone1":"+628700","emailaddress1":"rina@example.com"}]}""")
                : Json("""
                    {"value":[{"incidentid":"i-1","ticketnumber":"CAS-01","title":"Router mati","statuscode":1,
                    "statuscode@OData.Community.Display.V1.FormattedValue":"In Progress","createdon":"2026-02-28T04:30:00Z"}]}
                    """));
        var crm = new DynamicsCrmConnector(
            new DynamicsOptions { OrganizationUri = new Uri("https://acme.crm.dynamics.com"), AccessToken = "tok" },
            new HttpClient(handler));

        var customer = await crm.FindByPhoneAsync("sip:+628700@pbx");
        Assert.Equal("c-1", customer!.Id);
        Assert.Equal("Rina", customer.Name);

        var ticket = Assert.Single(await crm.RecentTicketsAsync("c-1"));
        Assert.Equal("CAS-01", ticket.Id);
        Assert.Equal("In Progress", ticket.Status);
        Assert.Contains("odata.include-annotations", handler.Requests[0].Headers.GetValues("Prefer").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DynamicsReadsTheNewCaseIdFromTheHeader()
    {
        var handler = new CannedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            response.Headers.Add("OData-EntityId", "https://acme.crm.dynamics.com/api/data/v9.2/incidents(3a1b2c3d-0000-0000-0000-000000000001)");
            return response;
        });
        var crm = new DynamicsCrmConnector(new DynamicsOptions { AccessToken = "tok" }, new HttpClient(handler));

        var ticket = await crm.CreateTicketAsync("c-1", "Internet mati", "Sejak pagi");

        Assert.Equal("3a1b2c3d-0000-0000-0000-000000000001", ticket.Id);
        Assert.Contains("customerid_contact@odata.bind", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdooCallsExecuteKwAndReadsTheResult()
    {
        var handler = new CannedHandler((_, body) =>
            body.Contains("search_read", StringComparison.Ordinal)
                ? Json("""{"jsonrpc":"2.0","id":1,"result":[{"id":42,"name":"Andi","phone":"+628555","email":"andi@example.com"}]}""")
                : Json("""{"jsonrpc":"2.0","id":2,"result":77}"""));
        var crm = new OdooCrmConnector(
            new OdooOptions { BaseUri = new Uri("https://acme.odoo.com"), Database = "acme", UserId = 7, ApiKey = "key" },
            new HttpClient(handler));

        var customer = await crm.FindByPhoneAsync("+628555");
        Assert.Equal("42", customer!.Id);
        Assert.Equal("Andi", customer.Name);

        var ticket = await crm.CreateTicketAsync("42", "Tidak bisa login", "Password direset tapi tetap gagal");
        Assert.Equal("77", ticket.Id);

        Assert.EndsWith("/jsonrpc", handler.Requests[0].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"execute_kw\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("\"acme\"", handler.Bodies[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdooReportsAnErrorEvenThoughItAnswersWithTwoHundred()
    {
        var handler = new CannedHandler((_, _) => Json("""
            {"jsonrpc":"2.0","id":1,"error":{"code":200,"message":"Odoo Server Error","data":{"message":"Access denied"}}}
            """));
        var crm = new OdooCrmConnector(new OdooOptions { Database = "acme", ApiKey = "key" }, new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => crm.FindByPhoneAsync("+628555"));
        Assert.Contains("Access denied", failure.Message, StringComparison.Ordinal);
    }
}
