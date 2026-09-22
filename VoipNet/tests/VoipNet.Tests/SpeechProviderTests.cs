using System.Net;
using System.Text;
using VoipNet.AI.Speech;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Protocol tests for the speech providers against canned HTTP responses.</summary>
public sealed class SpeechProviderTests
{
    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<byte[]> BinaryBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                BinaryBodies.Add(bytes);
                Bodies.Add(Encoding.UTF8.GetString(bytes));
            }

            Requests.Add(request);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Audio(byte[] pcm) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };

    [Fact]
    public async Task AzureSpeechSendsWavAndReadsTheDisplayText()
    {
        var handler = new CannedHandler(_ => Json("""{"RecognitionStatus":"Success","DisplayText":"Halo, apa kabar?"}"""));
        var stt = new AzureSpeechToText(new AzureSpeechOptions { ApiKey = "k", Region = "southeastasia", Language = "id-ID" }, new HttpClient(handler));

        var pcm = new byte[3200];
        var text = await stt.TranscribeOnceAsync(pcm, 16000);

        Assert.Equal("Halo, apa kabar?", text);
        var request = handler.Requests[0];
        Assert.Contains("stt.speech.microsoft.com", request.RequestUri!.Host);
        Assert.Contains("language=id-ID", request.RequestUri.Query);
        Assert.Equal("k", request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
        // The endpoint reads WAV, so the PCM must arrive with a header in front of it.
        Assert.Equal("RIFF", Encoding.ASCII.GetString(handler.BinaryBodies[0], 0, 4));
        Assert.Equal(44 + pcm.Length, handler.BinaryBodies[0].Length);
    }

    [Fact]
    public async Task AzureSpeechFallsBackToTheBestAlternative()
    {
        var handler = new CannedHandler(_ => Json("""{"RecognitionStatus":"Success","NBest":[{"Display":"Nomor pesanan saya 123."}]}"""));
        var stt = new AzureSpeechToText(new AzureSpeechOptions { ApiKey = "k" }, new HttpClient(handler));

        Assert.Equal("Nomor pesanan saya 123.", await stt.TranscribeOnceAsync(new byte[320], 8000));
    }

    [Fact]
    public async Task AzureTextToSpeechAsksForRawPcmWithSsml()
    {
        var pcm = new byte[640];
        var handler = new CannedHandler(_ => Audio(pcm));
        var tts = new AzureTextToSpeech(new AzureSpeechOptions { ApiKey = "k", Voice = "id-ID-GadisNeural" }, new HttpClient(handler));

        var audio = await tts.SynthesizeOnceAsync("Selamat pagi", new SpeechSynthesisOptions { SampleRate = 16000, Speed = 1.2 });

        Assert.Equal(pcm.Length, audio.Data.Length);
        Assert.Equal(16000, audio.SampleRate);
        var request = handler.Requests[0];
        Assert.Equal("raw-16khz-16bit-mono-pcm", request.Headers.GetValues("X-Microsoft-OutputFormat").Single());
        Assert.Contains("id-ID-GadisNeural", handler.Bodies[0]);
        Assert.Contains("prosody", handler.Bodies[0]);
        Assert.Contains("Selamat pagi", handler.Bodies[0]);
    }

    [Fact]
    public async Task CartesiaAsksForRawPcmAndStreamsIt()
    {
        var pcm = new byte[1024];
        var handler = new CannedHandler(_ => Audio(pcm));
        var tts = new CartesiaTextToSpeech(new CartesiaOptions { ApiKey = "k", Voice = "voice-1", Model = "sonic-2" }, new HttpClient(handler));

        var audio = await tts.SynthesizeOnceAsync("Pesanan Anda sudah dikirim", new SpeechSynthesisOptions { SampleRate = 24000 });

        Assert.Equal(pcm.Length, audio.Data.Length);
        var request = handler.Requests[0];
        Assert.Equal("k", request.Headers.GetValues("X-API-Key").Single());
        Assert.Contains("pcm_s16le", handler.Bodies[0]);
        Assert.Contains("\"sample_rate\":24000", handler.Bodies[0]);
        Assert.Contains("voice-1", handler.Bodies[0]);
    }

    [Fact]
    public async Task ProviderErrorsCarryTheServiceMessage()
    {
        var handler = new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid subscription key"}}""", Encoding.UTF8, "application/json"),
        });
        var tts = new AzureTextToSpeech(new AzureSpeechOptions { ApiKey = "wrong" }, new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => tts.SynthesizeOnceAsync("halo"));
        Assert.Contains("invalid subscription key", failure.Message);
    }
}
