using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Tests.Fakes
{
    /// <summary>
    /// Intercepts HttpClient calls and returns pre-registered responses.
    /// Register responses with RespondWith() before the code under test runs.
    /// Requests with no matching registration throw InvalidOperationException.
    /// </summary>
    public sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly List<(string UrlSubstring, Queue<(string Body, HttpStatusCode Status)> Responses)> _rules
            = new List<(string, Queue<(string, HttpStatusCode)>)>();

        public List<string> ReceivedUrls { get; } = new List<string>();

        /// <summary>Register a single response for URLs containing <paramref name="urlSubstring"/>.</summary>
        public void RespondWith(string urlSubstring, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            q.Enqueue((body, status));
            _rules.Add((urlSubstring, q));
        }

        /// <summary>Register multiple ordered responses for the same URL pattern.</summary>
        public void RespondWithSequence(string urlSubstring, IEnumerable<string> bodies, HttpStatusCode status = HttpStatusCode.OK)
        {
            var q = new Queue<(string, HttpStatusCode)>();
            foreach (var b in bodies)
                q.Enqueue((b, status));
            _rules.Add((urlSubstring, q));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            ReceivedUrls.Add(url);

            foreach (var (urlSubstring, queue) in _rules)
            {
                if (url.Contains(urlSubstring) && queue.Count > 0)
                {
                    var (body, status) = queue.Dequeue();
                    var response = new HttpResponseMessage(status)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                }
            }

            throw new InvalidOperationException(
                $"FakeHttpHandler: no registered response for URL: {url}\n" +
                $"Register one with handler.RespondWith(\"{url}\", json)");
        }

        protected override void Dispose(bool disposing) { }
    }
}
