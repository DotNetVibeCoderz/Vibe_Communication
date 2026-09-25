# Contact centre: IVR, queues, recording, CRM

🇮🇩 [Bahasa Indonesia](../id/contact-centre.md) · Made by Gravicode Studios, led by Kang Fadhil

![Call centre wallboard](../images/callcenter-wallboard-light.png)

## IVR

```csharp
var flow = IvrFlow.Create()
    .Welcome("Selamat datang di Gravicode Net.")
    .Menu("main", "Tekan 1 untuk penjualan, 2 untuk bantuan teknis, 0 untuk operator.", m => m
        .Option('1', "Sales", new IvrAction.Enqueue("sales", "Menghubungkan ke tim penjualan."))
        .Option('2', "Support", new IvrAction.Collect("account", "Masukkan nomor pelanggan, akhiri dengan pagar.", 8, "support"))
        .Option('0', "Operator", new IvrAction.Transfer("sip:operator@pbx", "Mohon tunggu."))
        .WaitFor(TimeSpan.FromSeconds(6))
        .Attempts(3)
        .OnFailure(new IvrAction.Hangup("Terima kasih.")))
    .Menu("support", "Tekan 1 untuk asisten AI.", m => m
        .Option('1', "AI assistant", new IvrAction.Handoff("ai", (call, context, ct) =>
            agentFactory.Create(o => o.SystemPrompt += $" Nomor pelanggan: {context.Values["account"]}").RunAsync(call, ct)))
        .Option('9', "Back", new IvrAction.Goto("main")))
    .Build();                                 // validates that every target menu exists

var result = await new IvrRunner(textToSpeech).RunAsync(call, flow);
// result.Outcome: Queued · Transferred · HandedOff · Completed · CallerLeft
// result.Context.Values["account"], result.Context.RequestedQueue, result.Context.Path
```

| Action | Effect |
| --- | --- |
| `Goto(menu)` | show another menu |
| `Say(text)` | speak and repeat the current menu |
| `Collect(key, prompt, maxDigits, nextMenu, terminator)` | read digits into `Context.Values[key]` |
| `Transfer(target, announcement)` | REFER the caller elsewhere |
| `Enqueue(queue, announcement)` | stop and report the queue (feed it to `CallCenterService`) |
| `Handoff(name, handler)` | give the call to any async handler, typically a `VoiceAgent` |
| `Hangup(announcement)` | say goodbye and end the call |

Menus can play WAV files (`PromptFromFile`) instead of synthesised prompts.

![IVR Studio](../images/ivrstudio-test-call.png)

## Queues and agents

```csharp
var centre = new CallCenterService(pbxClient, textToSpeech);
centre.AddQueue(new CallQueueOptions
{
    Name = "support",
    Strategy = RoutingStrategy.SkillBased,   // RoundRobin · LongestIdle · FewestCalls · SkillBased
    RequiredSkill = "support",
    ServiceLevelTarget = TimeSpan.FromSeconds(20),
    MaxWait = TimeSpan.FromMinutes(5),
    OverflowTarget = "sip:voicemail@pbx",
    RingTimeout = TimeSpan.FromSeconds(25),
    WrapupTime = TimeSpan.FromSeconds(15),
    AnnouncePosition = true,
    MusicOnHoldFile = "hold.wav",
});

centre.AddAgent(new Agent { Id = "2001", Name = "Sari", Uri = "sip:2001@pbx", Skills = { "support", "english" } });
centre.SetAgentState("2001", AgentState.Available);

pbxClient.IncomingCall += async (_, e) =>
{
    await e.Call.AnswerAsync();
    var ivr = await ivrRunner.RunAsync(e.Call, flow);
    if (ivr.Outcome == IvrOutcome.Queued)
    {
        var result = await centre.EnqueueAsync(e.Call, ivr.Context.RequestedQueue!, priority: 0, context: ivr.Context.Values);
    }
};
```

What happens while a caller waits: position announcements and/or music on hold play; when the caller is first in line and an agent with the right skill is available, the agent is reserved (`Ringing`) and called. If the agent answers, both legs are bridged in a conference and the agent is `OnCall`; otherwise the next agent is tried. After the call the agent goes through `Wrapup` back to `Available`.

Supervisor listen-in and whisper:

```csharp
var supervisor = await pbxClient.CallAsync("sip:supervisor@pbx");
centre.Monitor(callerCallId, supervisor, whisper: false);   // listen only
```

Listen-only mode makes the leg to the supervisor send-only: they hear the conversation, but their audio never enters the bridge.

### Callbacks and estimated wait

A caller who does not want to hold can keep their place and put the phone down:

```csharp
centre.CallQueued += async (_, queued) =>
{
    var wait = centre.EstimatedWait(queued.QueueName, queued.Position);
    if (wait > TimeSpan.FromMinutes(2) && await OffersCallbackAsync(queued.Call, wait))
    {
        centre.RequestCallback(queued);      // defaults to the caller's own number
    }
};

centre.CallbackCompleted += (_, request) => log.Info($"{request.Destination}: {request.Outcome}");
```

`EnqueueAsync` then returns `QueueOutcome.CallbackScheduled` so the application can thank the caller and
hang up. When an agent is free and the callback has waited longer than anyone still holding the line,
the service reserves that agent, rings the caller, plays `CallbackOptions.Announcement`, and bridges the
two. A caller who does not pick up is tried again after `RetryAfter`, up to `MaxAttempts`, and the
request then ends as `NoAnswer`. `PendingCallbacks(queue)` lists what is still owed, and
`CancelCallback(id)` drops one.

`EstimatedWait(queue, position)` spreads the average handling time (talk plus wrap-up, three minutes
until the queue has history) across the agents signed in for that queue. It returns `TimeSpan.Zero`
when an agent is free, and `TimeSpan.MaxValue` when nobody is signed in at all — there is no honest
estimate to give then, and it is better to say so than to invent a number.

### Sharing state between nodes

One node holds the calls it answered, but who is signed in and which callbacks are still owed have to
be agreed on. `ICallCenterStore` keeps exactly that, and `SqlCallCenterStore` implements it over any
ADO.NET provider — SQLite for a single node that should survive a restart, SQL Server or PostgreSQL for
several:

```csharp
var store = new SqlCallCenterStore(() => new SqliteConnection("Data Source=callcentre.db"), node: "pbx-1");
var centre = new CallCenterService(pbxClient, textToSpeech, store: store);
centre.AddQueue(new CallQueueOptions { Name = "support" });
await centre.RestoreAsync();        // take back the callbacks this deployment still owes
```

Agent states are written as they change and read back with `AllAgentsAsync()`, which is what a
dashboard spanning nodes needs. Before ringing a caller back, the node claims the callback with a
single conditional update, so two nodes never ring the same customer; a caller who does not pick up is
released again for whichever node is free next. The tables are created on first use, and a store that
is briefly unavailable is logged and stepped over rather than allowed to stop the call centre.

### Historical reports

With a store attached, every finished queue call is written to the history, and reports are read back
from it:

```csharp
var rows = await centre.ReportAsync(
    DateTimeOffset.UtcNow.AddDays(-7),
    DateTimeOffset.UtcNow,
    TimeSpan.FromMinutes(30),
    queueName: "support");

File.WriteAllText("support.csv", WorkforceReport.ToCsv(rows));
foreach (var (agent, calls, talk, average) in WorkforceReport.ByAgent(await store.LoadCallsAsync(from, to)))
{
    Console.WriteLine($"{agent}: {calls} calls, {talk:hh\:mm} talking, {average:mm\:ss} each");
}
```

Each row covers one queue in one interval: offered, answered, abandoned, overflowed, average and
longest wait, average talk time, service level and abandon rate. The CSV is plain UTF-8 with an ISO
timestamp per row, which loads into a spreadsheet, a warehouse or a Grafana source without a converter.

### Opening hours

A queue can have a schedule, so callers arriving out of hours are not left waiting for an agent who
is not there. Hours are written in their own time zone, and a date that breaks the weekly pattern —
a holiday, or a half day — is an exception:

```csharp
var hours = RoutingSchedule.Weekdays(new TimeOnly(8, 0), new TimeOnly(17, 0), "Asia/Jakarta");
hours.Exceptions.Add(new ScheduleException { Date = new DateOnly(2026, 3, 19), Reason = "Nyepi" });
hours.Exceptions.Add(new ScheduleException
{
    Date = new DateOnly(2026, 12, 24),
    Reason = "Christmas Eve",
    Hours = [new OpeningHours(DayOfWeek.Thursday, new TimeOnly(8, 0), new TimeOnly(12, 0))],
});

centre.AddQueue(new CallQueueOptions
{
    Name = "support",
    Schedule = new RoutingSchedule
    {
        TimeZone = "Asia/Jakarta",
        Hours = hours.Hours,
        Exceptions = hours.Exceptions,
        ClosedTarget = "sip:voicemail@pbx",   // where callers go while the queue is shut
    },
});
```

`EnqueueAsync` checks the schedule before the caller joins: a closed queue transfers them to
`ClosedTarget` (when there is one) and returns `QueueOutcome.Closed` straight away, so the call is
never counted as abandoned. Ask the schedule yourself to tell a caller when to ring back:

```csharp
var status = queueOptions.Schedule!.Check(DateTimeOffset.UtcNow);
if (!status.IsOpen)
{
    await tts.SpeakAsync(call, status.Reason is { } why
        ? $"We are closed today for {why}. We open again at {status.Until:HH:mm}."
        : $"We are closed. We open again at {status.Until:HH:mm}.");
}
```

The time zone is an IANA id (`Asia/Jakarta`), a Windows id, or a fixed offset (`+07:00`). Zone ids
depend on what the operating system knows, so an offset is the portable way to write a schedule into
a configuration file; an id this machine does not know is read as UTC.

An opening period whose closing time is at or before its opening time runs past midnight, so
`(Friday, 22:00, 02:00)` keeps the queue open until two on Saturday morning. A schedule with no hours
and no exceptions is always open. Closed calls are counted as offered in the metrics and stored in
the history, so a supervisor can see how much demand arrives while the queue is shut.

### Metrics

```csharp
var s = centre.Metrics.Snapshot("support");
// Offered, Answered, Abandoned, Overflowed, AverageWait, LongestWait, AverageTalk, ServiceLevel, AbandonRate
centre.Metrics.Changed += (_, _) => dashboard.Refresh();
```

## Recording service

```csharp
using var recordings = new RecordingService(pbxClient, new RecordingOptions
{
    Directory = "recordings", Format = RecordingFormat.Mp3, Layout = RecordingLayout.Stereo,
    RecordAllCalls = true, Retention = TimeSpan.FromDays(90),
});
recordings.RecordingSaved += (_, info) => Upload(info.Path);
var latest = recordings.List(50);           // index stored as JSON next to each file
recordings.ApplyRetention();
```

## CRM tools

Four CRMs ship with a connector, and any other one is an `ICrmConnector` away:

| CRM | Connector | Authentication |
| --- | --- | --- |
| HubSpot | `HubSpotCrmConnector` (contacts, tickets, notes) | private app token |
| Salesforce | `SalesforceCrmConnector` (contacts, cases, activity tasks) | OAuth access token and instance URL |
| Dynamics 365 | `DynamicsCrmConnector` (contacts, incidents, annotations) | OAuth access token for Dataverse |
| Odoo | `OdooCrmConnector` (partners, helpdesk tickets, chatter) | database, user id and API key |

```csharp
services.AddHubSpotCrm(o => o.AccessToken = configuration["HubSpot:Token"]!);

// or directly
var crm = new SalesforceCrmConnector(new SalesforceOptions
{
    InstanceUri = new Uri("https://acme.my.salesforce.com"),
    AccessToken = token,      // refreshing it is the application's business
});

var tools = CrmToolset.Create(crm);
var options = new VoiceAgentOptions { ChatOptions = new ChatOptions { Tools = [.. tools] } };
```

A lookup takes whatever the call gives it: `sip:+628123456@pbx` is reduced to `+628123456` before the
search, and both the fixed and the mobile number are checked. `InMemoryCrmConnector` matches phone
numbers on their last nine digits, so `+62 812…`, `62812…` and `0812…` refer to the same customer; it
is what the samples and tests use.

## Analytics with AI

The Call Centre sample sends the wallboard snapshot to a model and shows staffing advice — a pattern you can reuse for shift reports, QA summaries of recordings (transcribe with `TranscribeOnceAsync`, summarise with any `IChatClient`), or intent analytics.
