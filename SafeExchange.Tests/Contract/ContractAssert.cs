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
            Assert.That(
                ContractJson.Normalize(raw, maskedProperties, literals ?? NoLiterals),
                Is.EqualTo(ContractJson.Canonical(expectedJson)));
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
