using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace VoipNet.Enterprise.Crm;

/// <summary>A customer record as the AI agent sees it.</summary>
/// <param name="Id">CRM identifier.</param>
/// <param name="Name">Full name.</param>
/// <param name="Phone">Phone number.</param>
/// <param name="Email">E-mail address.</param>
/// <param name="Tier">Service tier, for example Gold.</param>
/// <param name="Notes">Free-form notes useful on a call.</param>
public sealed record CustomerRecord(string Id, string Name, string? Phone, string? Email, string? Tier, string? Notes);

/// <summary>A support ticket.</summary>
/// <param name="Id">Ticket identifier.</param>
/// <param name="CustomerId">Customer the ticket belongs to.</param>
/// <param name="Subject">Short summary.</param>
/// <param name="Status">Status, for example Open.</param>
/// <param name="CreatedAt">When it was opened.</param>
public sealed record TicketRecord(string Id, string CustomerId, string Subject, string Status, DateTimeOffset CreatedAt);

/// <summary>
/// The operations an AI agent needs from a CRM. Implement it over Salesforce, Dynamics, HubSpot or
/// an internal database; <see cref="CrmToolset"/> turns it into tools the model can call.
/// </summary>
public interface ICrmConnector
{
    /// <summary>Finds a customer by phone number.</summary>
    /// <param name="phone">Phone number or SIP URI of the caller.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default);

    /// <summary>Lists a customer's recent tickets.</summary>
    /// <param name="customerId">Customer identifier.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default);

    /// <summary>Opens a ticket.</summary>
    /// <param name="customerId">Customer identifier.</param>
    /// <param name="subject">Short summary.</param>
    /// <param name="description">Details of the request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<TicketRecord> CreateTicketAsync(string customerId, string subject, string description, CancellationToken cancellationToken = default);

    /// <summary>Adds a note to the customer's timeline.</summary>
    /// <param name="customerId">Customer identifier.</param>
    /// <param name="note">Note text.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task AddNoteAsync(string customerId, string note, CancellationToken cancellationToken = default);
}

/// <summary>Exposes a CRM to the model as AI functions.</summary>
public static class CrmToolset
{
    /// <summary>Creates the CRM tools.</summary>
    /// <param name="crm">The CRM connector.</param>
    public static IReadOnlyList<AITool> Create(ICrmConnector crm)
    {
        ArgumentNullException.ThrowIfNull(crm);

        [Description("Look up the caller in the CRM by phone number. Call this early to personalise the conversation.")]
        async Task<object> LookupCustomer([Description("Caller phone number or SIP address.")] string phone)
        {
            var customer = await crm.FindByPhoneAsync(phone).ConfigureAwait(false);
            return customer is null ? "No customer found for this number." : customer;
        }

        [Description("List the customer's recent support tickets.")]
        async Task<IReadOnlyList<TicketRecord>> RecentTickets([Description("CRM customer id.")] string customerId) =>
            await crm.RecentTicketsAsync(customerId).ConfigureAwait(false);

        [Description("Open a support ticket for the customer. Confirm the subject with the caller first.")]
        async Task<TicketRecord> CreateTicket(
            [Description("CRM customer id.")] string customerId,
            [Description("Short summary of the problem.")] string subject,
            [Description("Details the caller gave.")] string description) =>
            await crm.CreateTicketAsync(customerId, subject, description).ConfigureAwait(false);

        [Description("Add a note to the customer's timeline, for example a summary of this call.")]
        async Task<string> AddNote([Description("CRM customer id.")] string customerId, [Description("Note text.")] string note)
        {
            await crm.AddNoteAsync(customerId, note).ConfigureAwait(false);
            return "Note saved.";
        }

        return
        [
            AIFunctionFactory.Create(LookupCustomer, "crm_lookup_customer"),
            AIFunctionFactory.Create(RecentTickets, "crm_recent_tickets"),
            AIFunctionFactory.Create(CreateTicket, "crm_create_ticket"),
            AIFunctionFactory.Create(AddNote, "crm_add_note"),
        ];
    }
}

/// <summary>An in-memory CRM for demos and tests.</summary>
public sealed class InMemoryCrmConnector : ICrmConnector
{
    private readonly List<CustomerRecord> _customers = [];
    private readonly List<TicketRecord> _tickets = [];
    private readonly Dictionary<string, List<string>> _notes = [];
    private readonly Lock _gate = new();

    /// <summary>Adds a customer.</summary>
    /// <param name="customer">The record.</param>
    public InMemoryCrmConnector Add(CustomerRecord customer)
    {
        lock (_gate)
        {
            _customers.Add(customer);
        }

        return this;
    }

    /// <summary>Notes stored for a customer.</summary>
    /// <param name="customerId">Customer identifier.</param>
    public IReadOnlyList<string> NotesFor(string customerId)
    {
        lock (_gate)
        {
            return _notes.TryGetValue(customerId, out var notes) ? [.. notes] : [];
        }
    }

    /// <inheritdoc/>
    public Task<CustomerRecord?> FindByPhoneAsync(string phone, CancellationToken cancellationToken = default)
    {
        var digits = Subscriber(phone);
        lock (_gate)
        {
            var match = _customers.FirstOrDefault(c => c.Phone is not null && digits.Length >= 6 && Subscriber(c.Phone) == digits);
            return Task.FromResult(match);
        }
    }

    /// <summary>
    /// Compares numbers by their last nine digits, so +62 812..., 62812... and 0812... all match
    /// regardless of country code or trunk prefix.
    /// </summary>
    private static string Subscriber(string value)
    {
        var digits = Digits(value);
        return digits.Length > 9 ? digits[^9..] : digits;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TicketRecord>> RecentTicketsAsync(string customerId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TicketRecord> result = _tickets.Where(t => t.CustomerId == customerId).OrderByDescending(t => t.CreatedAt).Take(5).ToList();
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc/>
    public Task<TicketRecord> CreateTicketAsync(string customerId, string subject, string description, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var ticket = new TicketRecord($"TCK-{_tickets.Count + 1001}", customerId, subject, "Open", DateTimeOffset.UtcNow);
            _tickets.Add(ticket);
            return Task.FromResult(ticket);
        }
    }

    /// <inheritdoc/>
    public Task AddNoteAsync(string customerId, string note, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_notes.TryGetValue(customerId, out var list))
            {
                _notes[customerId] = list = [];
            }

            list.Add(note);
        }

        return Task.CompletedTask;
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
}
