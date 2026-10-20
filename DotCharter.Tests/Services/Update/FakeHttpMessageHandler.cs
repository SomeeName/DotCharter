using System.Net;
using System.Text;

namespace DotCharter.Tests.Services.Update;

/// <summary>
/// Scripted HttpMessageHandler: responses are dequeued in order; every
/// request (headers + body read eagerly) is recorded for assertions.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];

    /// <summary>Answers requests once the scripted queue is empty. Used by the
    /// partial-update tests, where the number of range requests is a property of
    /// the plan rather than something a test should have to script.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    /// <summary>
    ///     Serves <paramref name="asset" /> the way a release asset endpoint does:
    ///     <c>206</c> with a <c>Content-Range</c> for a ranged request, the whole
    ///     thing otherwise.
    ///     <para>
    ///         <paramref name="honourRanges" /> = false is the case the fallback
    ///         exists for — a server (or a proxy) that ignores <c>Range</c> and
    ///         answers 200 with all 414 MB.
    ///     </para>
    /// </summary>
    public void ServeAsset(byte[] asset, bool honourRanges = true)
    {
        Fallback = request =>
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            if (range?.From == null || !honourRanges)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(asset) };

            var from = (int)range.From.Value;
            var to = (int)Math.Min(range.To ?? asset.Length - 1, asset.Length - 1);
            var slice = asset.AsSpan(from, to - from + 1).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, asset.Length);
            return response;
        };
    }

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    public void EnqueueBytes(byte[] payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(payload)
        });
    }

    public void EnqueueStatus(HttpStatusCode statusCode)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(string.Empty)
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content == null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0 && Fallback != null)
            return Fallback(request);

        if (_responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"fake handler ran out of scripted responses\"}")
            };
        }

        return _responses.Dequeue()(request);
    }

    public string? HeaderOf(int requestIndex, string name)
    {
        return Requests[requestIndex].Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;
    }
}
