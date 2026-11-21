/// <summary>
/// ContractAssert
/// </summary>

namespace SafeExchange.Tests.Contract
{
    using Microsoft.Azure.Functions.Worker.Http;
    using NUnit.Framework;
    using SafeExchange.Tests.Utilities;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text.Json.Nodes;
    using System.Text.RegularExpressions;

    public static class ContractAssert
    {
        public const string JsonContentType = "application/json; charset=utf-8";

        private static readonly IReadOnlyDictionary<string, string> NoLiterals = new Dictionary<string, string>();

        /// <summary>
        /// Asserts status, the exact header set (only Content-Type for JSON responses) and the
        /// body: the wire text must be compact default-encoded JSON and, after normalisation,
        /// equal the canonical form of <paramref name="expectedJson"/>.
        /// </summary>
        public static void Json(
            HttpResponseData response,
            HttpStatusCode expectedStatus,
            string expectedJson,
            IReadOnlyDictionary<string, string>? literals = null,
            params string[] maskedProperties)
        {
            var actual = AsTest(response);
            Assert.That(actual.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(HeaderLines(actual), Is.EqualTo(new[] { $"Content-Type: {JsonContentType}" }));

            var raw = actual.ReadBodyAsString();
            Assert.That(ContractJson.IsWireCompact(raw), Is.True, $"Wire body is not compact default-encoded JSON: {raw}");
            var normalised = ContractJson.Normalize(raw, maskedProperties, literals ?? NoLiterals);
            Assert.That(normalised, Is.EqualTo(ContractJson.Canonical(expectedJson)), $"Actual: {normalised}");
        }

        /// <summary>
        /// Asserts status, the exact header set (in order) and the exact body bytes of a raw
        /// (non-JSON) response.
        /// </summary>
        public static void Bytes(
            HttpResponseData response,
            HttpStatusCode expectedStatus,
            byte[] expectedBody,
            params string[] expectedHeaderLines)
        {
            var actual = AsTest(response);
            Assert.That(actual.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(HeaderLines(actual), Is.EqualTo(expectedHeaderLines));
            Assert.That(ReadBodyBytes(actual), Is.EqualTo(expectedBody));
        }

        /// <summary>
        /// Asserts the audit page's hash chain: every non-empty hash/prevHash is a base64 SHA-256
        /// (44 chars) and each event's prevHash equals its predecessor's hash (reversed for a
        /// descending page).
        /// </summary>
        public static void AuditHashChain(HttpResponseData response, bool descending = false)
        {
            var events = JsonNode.Parse(AsTest(response).ReadBodyAsString())!["result"]!["events"]!.AsArray();
            var hashPattern = new Regex("^[A-Za-z0-9+/]{43}=$", RegexOptions.CultureInvariant);
            foreach (var item in events)
            {
                foreach (var property in new[] { "hash", "prevHash" })
                {
                    var value = item![property]!.GetValue<string>();
                    Assert.That(value == string.Empty || hashPattern.IsMatch(value), Is.True, $"{property} '{value}' is not a base64 SHA-256.");
                }
            }

            for (var i = 1; i < events.Count; i++)
            {
                var earlier = events[descending ? i : i - 1]!;
                var later = events[descending ? i - 1 : i]!;
                Assert.That(later["prevHash"]!.GetValue<string>(), Is.EqualTo(earlier["hash"]!.GetValue<string>()), $"Event {i} breaks the hash chain.");
            }
        }

        public static TestHttpResponseData AsTest(HttpResponseData response)
        {
            Assert.That(response, Is.InstanceOf<TestHttpResponseData>());
            return (TestHttpResponseData)response;
        }

        public static string[] HeaderLines(HttpResponseData response)
            => response.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}").ToArray();

        public static byte[] ReadBodyBytes(HttpResponseData response)
        {
            using var copy = new MemoryStream();
            response.Body.Seek(0, SeekOrigin.Begin);
            response.Body.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
