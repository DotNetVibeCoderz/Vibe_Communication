using System.Text;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using Xunit;

namespace VoipNet.Tests;

/// <summary>
/// Real calls to hosted models. Keys are read at run time from the file named by
/// VOIPNET_TEST_KEYS (default: ..\testkey.txt next to the repository) and never stored in the repo.
/// The tests skip when the file or a section is missing.
/// </summary>
public sealed class LiveLlmTests
{
    private static IChatClient? Azure()
    {
        var s = TestKeys.Section("Azure OpenAI");
        if (s is null || !s.TryGetValue("apikey", out var key) || !s.TryGetValue("endpoint", out var endpoint) || !s.TryGetValue("model", out var model))
        {
            return null;
        }

        // The key file may list several deployments ("gpt-5-mini, gpt-5.6-luna"); the first one is tested.
        return new OpenAiChatClient(OpenAiChatOptions.ForAzure(endpoint, key, model.Split(',')[0].Trim()));
    }

    private static IChatClient? DeepSeek()
    {
        var s = TestKeys.Section("DeepSeek");
        if (s is null)
        {
            return null;
        }

        var key = s.FirstOrDefault(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Value;
        var endpoint = s.FirstOrDefault(p => p.Key.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)).Value;
        var model = s.FirstOrDefault(p => p.Key.Contains("Model", StringComparison.OrdinalIgnoreCase)).Value?.Split(',')[0].Trim();
        if (key is null || endpoint is null || model is null)
        {
            return null;
        }

        return new OpenAiChatClient(new OpenAiChatOptions { ApiKey = key, BaseUri = new Uri(endpoint.TrimEnd('/') + "/"), Model = model });
    }

    [Fact]
    public async Task AzureOpenAiAnswersInIndonesian()
    {
        using var client = Azure();
        Assert.SkipWhen(client is null, "No Azure OpenAI key available.");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Jawab dengan satu kata saja: apa ibu kota Indonesia?")],
            new ChatOptions { MaxOutputTokens = 2000 });

        Assert.Contains("jakarta", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureOpenAiStreamsAndCallsTools()
    {
        using var inner = Azure();
        Assert.SkipWhen(inner is null, "No Azure OpenAI key available.");

        var called = false;
        var tool = AIFunctionFactory.Create(
            (string orderId) => { called = true; return $"Order {orderId} dikirim hari ini via JNE."; },
            "get_order_status",
            "Returns the shipping status of an order.");
        using var client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Bagaimana status pesanan INV-778?")],
            new ChatOptions { Tools = [tool], MaxOutputTokens = 3000 }))
        {
            text.Append(update.Text);
        }

        Assert.True(called, "the model did not call the tool");
        Assert.Contains("JNE", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeepSeekAnswersThroughTheCompatibleClient()
    {
        using var client = DeepSeek();
        Assert.SkipWhen(client is null, "No DeepSeek key available.");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with the single word: pong")],
            new ChatOptions { MaxOutputTokens = 400, Temperature = 0 });

        Assert.Contains("pong", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureRealtimeAgentGreetsAndAnswersOnACall()
    {
        var s = TestKeys.Section("Azure OpenAI Realtime Model");
        var (endpoint, key, model) = s is null ? default : (TestKeys.Value(s, "endpoint"), TestKeys.Value(s, "key"), TestKeys.Value(s, "model"));
        Assert.SkipWhen(endpoint is null || key is null || model is null, "No Azure OpenAI realtime key available.");

        await using var pair = await LoopbackPair.ConnectAsync("caller", "agent");
        var options = VoipNet.AI.Realtime.RealtimeVoiceOptions.ForAzure(endpoint!, key!, model!);
        options.Instructions = "You are a phone agent for a test. Always answer in one short English sentence.";
        options.Greeting = "Hello, this is the Voip dot NET test agent.";
        var agent = new VoipNet.AI.Realtime.RealtimeVoiceAgent(options);

        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        agent.AgentSaid += (_, text) => said.Enqueue(text);
        agent.ErrorReceived += (_, error) => errors.Enqueue(error);

        // Record what the caller hears, to play it back as the caller's own question.
        var heard = new List<short>();
        var heardRate = 0;
        pair.CallerLeg.AudioReceived += (_, direction, rate, samples) =>
        {
            if (direction == AudioDirection.Inbound)
            {
                lock (heard)
                {
                    heardRate = rate;
                    foreach (var sample in samples)
                    {
                        heard.Add(sample);
                    }
                }
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var run = agent.RunAsync(pair.CalleeLeg, cts.Token);

        await TestHelpers.WaitUntilAsync(() => !said.IsEmpty || !errors.IsEmpty, TimeSpan.FromSeconds(40), "the spoken greeting");
        Assert.True(errors.IsEmpty, string.Join(" | ", errors));
        Assert.Contains("test agent", said.First(), StringComparison.OrdinalIgnoreCase);
        await TestHelpers.WaitUntilAsync(() => pair.CalleeLeg.QueuedAudioMs == 0, TimeSpan.FromSeconds(20), "greeting to finish playing");

        short[] loud;
        lock (heard)
        {
            loud = [.. heard];
        }

        Assert.True(loud.Max(v => Math.Abs((int)v)) > 2000, "the caller heard no speech");

        // The caller "speaks" (the greeting it heard, framed by silence); server VAD must end the turn and answer.
        var silence = new short[heardRate / 2];
        pair.CallerLeg.SendAudio(silence, heardRate);
        pair.CallerLeg.SendAudio(loud, heardRate);
        pair.CallerLeg.SendAudio(new short[heardRate * 2], heardRate);
        await TestHelpers.WaitUntilAsync(() => said.Count >= 2 || !errors.IsEmpty, TimeSpan.FromSeconds(40), "an answer to the caller");
        Assert.True(errors.IsEmpty, string.Join(" | ", errors));

        await pair.CallerLeg.HangupAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
