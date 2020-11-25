/// SafeExchange

namespace SpaceOyster.SafeExchange
{
    using System;
    using System.Net;
    using Microsoft.Rest.TransientFaultHandling;

    public class PurgingTransientErrorDetectionStrategy : ITransientErrorDetectionStrategy
    {
        public bool IsTransient(Exception ex)
        {
            if (ex != null)
            {
                HttpRequestWithStatusException httpException;
                if ((httpException = ex as HttpRequestWithStatusException) != null)
                {
                    if (httpException.StatusCode == HttpStatusCode.Conflict ||
                        httpException.StatusCode == HttpStatusCode.RequestTimeout ||
                        httpException.StatusCode == (HttpStatusCode)429 ||
                        (httpException.StatusCode >= HttpStatusCode.InternalServerError &&
                         httpException.StatusCode != HttpStatusCode.NotImplemented &&
                         httpException.StatusCode != HttpStatusCode.HttpVersionNotSupported))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}